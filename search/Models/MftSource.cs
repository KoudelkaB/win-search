using System;
using System.IO;
using System.IO.Pipes;
using search.Core;

namespace search.Models
{
    /// <summary>
    /// How the content of a drive was obtained
    /// </summary>
    internal enum MftOrigin { Service, Direct, Broker, Walk }

    /// <summary>
    /// Acquires the raw $MFT of an NTFS drive from the first source available, in order:
    /// direct in-process read (only when elevated) -> elevated broker when already connected
    /// -> WinSearchService pipe (no prompt, instant availability check) -> waiting for the
    /// broker's UAC prompt. The wait comes last on purpose: the first scan starts while the
    /// prompt is still unanswered, and blocking on it would delay a scan the service (or
    /// nothing at all) should serve. Returns null when none worked - the caller falls back
    /// to the zero-privilege directory walk.
    /// </summary>
    internal static class MftSource
    {
        public static MftBuffer TryAcquire(DriveInfo drive, out MftOrigin origin)
        {
            var volume = drive.RootDirectory.FullName;

            // Elevated (admin shell, "Run as administrator", or the VS debugger): read the
            // volume directly in-process. This must come before the service pipe, otherwise
            // an elevated process would needlessly stream the whole MFT over a pipe.
            if (Program.IsProcessElevated)
            {
                try
                {
                    origin = MftOrigin.Direct;
                    using var raw = RawMft.Open(volume);
                    return MftBuffer.From(raw);
                }
                catch (Exception e)
                {
                    $"direct MFT read of {volume} failed: {e.Message}".Debug();
                }
            }

            // Elevation consented at startup: the elevated broker reads the volume and
            // streams the raw $MFT back. Used without waiting when already connected -
            // during the first scan its UAC prompt is usually still unanswered, and that
            // wait must not delay a scan the service could serve right now.
            try
            {
                if (Broker.Available)
                {
                    origin = MftOrigin.Broker;
                    return Broker.ReadMft(volume);
                }
            }
            catch (Exception e)
            {
                $"broker MFT read of {volume} failed: {e.Message}".Debug();
            }

            // The optional WinSearchService serves the raw $MFT over its pipe with no
            // prompt and no wait (500ms connect timeout when not installed)
            try
            {
                origin = MftOrigin.Service;
                var buffer = FromService(volume);
                if (buffer != null) return buffer;
            }
            catch (Exception e)
            {
                $"service MFT read of {volume} failed: {e.Message}".Debug();
            }

            // No service => the broker is the last chance to avoid the slow folder walk,
            // so now it is worth waiting out the UAC prompt
            try
            {
                if (Broker.WaitAvailable(TimeSpan.FromSeconds(60)))
                {
                    origin = MftOrigin.Broker;
                    return Broker.ReadMft(volume);
                }
            }
            catch (Exception e)
            {
                $"broker MFT read of {volume} failed: {e.Message}".Debug();
            }

            origin = MftOrigin.Walk;
            return null;
        }

        /// <summary>
        /// Request the raw $MFT from the WinSearchService. Returns null when the
        /// service is not installed/running (fast connect timeout).
        /// </summary>
        static MftBuffer FromService(string volume)
        {
            using var pipe = new NamedPipeClientStream(".", ServicePipe.PipeName, PipeDirection.InOut);
            try
            {
                pipe.Connect(500);
            }
            catch (Exception e) when (e is TimeoutException || e is IOException || e is UnauthorizedAccessException)
            {
                // TimeoutException/IOException: service not installed or not running.
                // UnauthorizedAccessException: the pipe ACL denies this user - treat as
                // unavailable and fall through rather than failing the whole drive scan.
                return null;
            }

            pipe.WriteByte(ServicePipe.ProtocolVersion);
            ServicePipe.WriteString(pipe, volume);
            pipe.Flush();

            var status = pipe.ReadByte();
            if (status < 0)
                throw new IOException("The service pipe closed unexpectedly.");
            if (status != ServicePipe.StatusOk)
                throw new IOException(ServicePipe.ReadString(pipe));

            var bytesPerRecord = ServicePipe.ReadInt32(pipe);
            var length = ServicePipe.ReadInt64(pipe);
            if (bytesPerRecord <= 0 || bytesPerRecord > (1 << 16) || length < 0)
                throw new InvalidDataException($"Invalid MFT header from the service: {bytesPerRecord}/{length}.");

            return MftBuffer.From(pipe, bytesPerRecord, length);
        }
    }
}
