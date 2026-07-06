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
    /// Acquires the raw $MFT of an NTFS drive from the first source available:
    /// WinSearchService pipe -> direct volume read (only when this process is elevated)
    /// -> elevated broker. Returns null when none worked - the caller falls back
    /// to the zero-privilege directory walk.
    /// </summary>
    internal static class MftSource
    {
        public static MftBuffer TryAcquire(DriveInfo drive, out MftOrigin origin)
        {
            var volume = drive.RootDirectory.FullName;

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

            try
            {
                // The broker's UAC prompt may still be unanswered during the first scan
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
            catch (Exception e) when (e is TimeoutException || e is IOException)
            {
                return null; // Service not installed or not running
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
