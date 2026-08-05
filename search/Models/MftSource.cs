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
        internal static readonly TimeSpan BrokerSourceWait = TimeSpan.FromSeconds(60);

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
                var nodes = FromService(volume, cancellationToken, out var serviceFailure);
                if (nodes != null) return nodes;
                StorageMaintenance.AppendDiagnostic(
                    $"MFT source {volume}: service unavailable ({serviceFailure}); trying broker");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                StorageMaintenance.AppendDiagnostic(
                    $"MFT source {volume}: service failed ({e.GetType().Name}: {e.Message}); trying broker");
                $"service MFT read of {volume} failed: {e.Message}".Debug();
            }

            // No service => the broker is the last chance to avoid the slow folder walk.
            // A successful startup pipe probe is only a point-in-time observation: the service
            // can disappear before this connection. In that race Program deliberately skipped
            // StartClient, so waiting for an already-started broker returns immediately and
            // incorrectly falls through to Walk. Start it lazily when no offer has been made.
            try
            {
                var canOffer = Broker.CanOffer;
                if (BrokerAfterServiceFailure(canOffer,
                        Broker.EnsureStarted, Broker.WaitAvailable, BrokerSourceWait))
                {
                    StorageMaintenance.AppendDiagnostic(
                        $"MFT source {volume}: broker available after service fallback; started={canOffer}");
                    origin = MftOrigin.Broker;
                    return Broker.ReadMftNodes(volume, cancellationToken);
                }
                StorageMaintenance.AppendDiagnostic(
                    $"MFT source {volume}: broker unavailable after service fallback; "
                    + $"canOffer={canOffer}; declined={Broker.Declined}; using folder walk");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                StorageMaintenance.AppendDiagnostic(
                    $"MFT source {volume}: broker fallback failed ({e.GetType().Name}: {e.Message}); "
                    + "using folder walk");
                $"broker MFT read of {volume} failed: {e.Message}".Debug();
            }

            origin = MftOrigin.Walk;
            return null;
        }

        /// <summary>
        /// Testable service-failure policy. If startup skipped the broker because the service
        /// pipe looked available, raise the broker now; otherwise wait for the already pending
        /// startup offer. Exactly one of the delegates is invoked.
        /// </summary>
        internal static bool BrokerAfterServiceFailure(bool canOffer,
            Func<TimeSpan, bool> ensureStarted, Func<TimeSpan, bool> waitAvailable,
            TimeSpan timeout)
        {
            if (ensureStarted == null) throw new ArgumentNullException(nameof(ensureStarted));
            if (waitAvailable == null) throw new ArgumentNullException(nameof(waitAvailable));
            return canOffer ? ensureStarted(timeout) : waitAvailable(timeout);
        }

        /// <summary>
        /// Request the raw $MFT from the WinSearchService and parse it as it streams in.
        /// Returns null when the service is not installed/running (fast connect timeout).
        /// </summary>
        static IEnumerable<INode> FromService(string volume,
            CancellationToken cancellationToken, out string failure)
        {
            failure = "none";
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
                failure = $"{e.GetType().Name}: {e.Message}";
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
