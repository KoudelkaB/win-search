using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using search.Core;

namespace search.Models
{
    /// <summary>
    /// How the content of a drive was obtained
    /// </summary>
    internal enum MftOrigin { Service, Direct, Broker, Walk }

    /// <summary>
    /// Reads the raw $MFT of an NTFS drive from the first source available, in order:
    /// direct in-process read (only when elevated) -> elevated broker when already connected
    /// -> WinSearchService pipe (no prompt, instant availability check) -> waiting for the
    /// broker's UAC prompt. The wait comes last on purpose: the first scan starts while the
    /// prompt is still unanswered, and blocking on it would delay a scan the service (or
    /// nothing at all) should serve. The $MFT is parsed while it streams in - it is never
    /// buffered whole. Returns null when no source worked - the caller falls back to the
    /// zero-privilege directory walk.
    /// </summary>
    internal static class MftSource
    {
        public static IEnumerable<INode> TryGetNodes(DriveInfo drive, out MftOrigin origin,
            CancellationToken cancellationToken = default)
        {
            var volume = drive.RootDirectory.FullName;
            cancellationToken.ThrowIfCancellationRequested();

            // Elevated (admin shell, "Run as administrator", or the VS debugger): read the
            // volume directly in-process. This must come before the service pipe, otherwise
            // an elevated process would needlessly stream the whole MFT over a pipe.
            if (Program.IsProcessElevated)
            {
                try
                {
                    origin = MftOrigin.Direct;
                    using var raw = RawMft.Open(volume);
                    using var stream = raw.CreateStream();
                    return MftDriveReader.GetNodes(stream, raw.BytesPerMftRecord, raw.Length, volume,
                        cancellationToken: cancellationToken, drainOnCancellation: false);
                }
                catch (OperationCanceledException) { throw; }
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
                    return Broker.ReadMftNodes(volume, cancellationToken);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                $"broker MFT read of {volume} failed: {e.Message}".Debug();
            }

            // The optional WinSearchService serves the raw $MFT over its pipe with no
            // prompt and no wait (500ms connect timeout when not installed)
            try
            {
                origin = MftOrigin.Service;
                var nodes = FromService(volume, cancellationToken);
                if (nodes != null) return nodes;
            }
            catch (OperationCanceledException) { throw; }
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
                    return Broker.ReadMftNodes(volume, cancellationToken);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                $"broker MFT read of {volume} failed: {e.Message}".Debug();
            }

            origin = MftOrigin.Walk;
            return null;
        }

        /// <summary>
        /// Request the raw $MFT from the WinSearchService and parse it as it streams in.
        /// Returns null when the service is not installed/running (fast connect timeout).
        /// </summary>
        static IEnumerable<INode> FromService(string volume, CancellationToken cancellationToken)
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
            if (bytesPerRecord <= 0 || length < 0)
                throw new InvalidDataException($"Invalid MFT header from the service: {bytesPerRecord}/{length}.");

            // GetNodes consumes the whole payload before returning, so disposing the pipe here is safe
            return MftDriveReader.GetNodes(pipe, bytesPerRecord, length, volume,
                cancellationToken: cancellationToken, drainOnCancellation: false);
        }

        /// <summary>
        /// Query a batch of exact NTFS references through the installed service.
        /// Null means the service/protocol is unavailable; individual null entries
        /// mean that the corresponding reference no longer resolves.
        /// </summary>
        internal static NtfsFileMetadata?[] TryReadMetadataFromService(
            string volume, IReadOnlyList<ulong> frns)
        {
            if (frns == null || frns.Count > ServicePipe.MaxMetadataBatch)
                throw new ArgumentOutOfRangeException(nameof(frns));
            try
            {
                using var pipe = new NamedPipeClientStream(".", ServicePipe.PipeName,
                    PipeDirection.InOut);
                pipe.Connect(500);
                pipe.WriteByte(ServicePipe.MetadataProtocolVersion);
                ServicePipe.WriteString(pipe, volume);
                ServicePipe.WriteInt32(pipe, frns.Count);
                foreach (var frn in frns)
                    ServicePipe.WriteInt64(pipe, unchecked((long)frn));
                pipe.Flush();

                var status = pipe.ReadByte();
                if (status < 0) return null; // Older service: unsupported version
                if (status != ServicePipe.StatusOk)
                    throw new IOException(ServicePipe.ReadString(pipe));
                var count = ServicePipe.ReadInt32(pipe);
                if (count != frns.Count)
                    throw new InvalidDataException(
                        $"Invalid metadata response count {count}/{frns.Count}.");
                var results = new NtfsFileMetadata?[count];
                for (var i = 0; i < count; i++)
                    results[i] = ServicePipe.ReadMetadata(pipe);
                return results;
            }
            catch (Exception e) when (e is TimeoutException || e is IOException
                || e is UnauthorizedAccessException || e is EndOfStreamException)
            {
                return null;
            }
        }
    }
}
