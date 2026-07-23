using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using search.Core;

namespace search.Service
{
    /// <summary>
    /// Serves raw $MFT bytes and batched file-reference metadata over a named pipe
    /// to local authenticated users.
    /// One request per connection (parallel drive scans open parallel connections);
    /// the $MFT is streamed in 1 MB chunks and never buffered in this process.
    /// The service only ever opens volumes read-only and never parses anything.
    /// </summary>
    static class PipeServer
    {
        public static void Run(CancellationToken ct)
        {
            try
            {
                AcceptLoop(ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }
        }

        static async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = NamedPipeServerStreamAcl.Create(
                        ServicePipe.PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        inBufferSize: 0, outBufferSize: 0, CreatePipeSecurity());
                    await server.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    return;
                }
                catch
                {
                    server?.Dispose();
                    await Task.Delay(1000, ct); // Do not spin on persistent errors
                    continue;
                }

                var client = server;
                _ = Task.Run(() => HandleClient(client, ct));
            }
        }

        static PipeSecurity CreatePipeSecurity()
        {
            var security = new PipeSecurity();
            // Any local authenticated user may request MFT data.
            // Synchronize is REQUIRED: a NamedPipeClientStream opened for PipeDirection.InOut
            // requests FILE_GENERIC_READ | FILE_GENERIC_WRITE, both of which include SYNCHRONIZE.
            // Granting only ReadWrite (which omits Synchronize) makes every unelevated client
            // connect fail with UnauthorizedAccessException, so the app silently falls back to
            // the slow folder walk.
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            // Explicitly deny remote clients: .NET does not expose PIPE_REJECT_REMOTE_CLIENTS
            // and PipeOptions.CurrentUserOnly cannot be used here (the service runs as
            // LocalSystem while the clients are arbitrary local users)
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            return security;
        }

        static void HandleClient(NamedPipeServerStream pipe, CancellationToken ct)
        {
            try
            {
                using (pipe)
                {
                    var request = ReadRequest(pipe, ct);
                    if (!request.HasValue) return; // Bad version or silent client - just close
                    var (version, volume) = request.Value;

                    if (!ServicePipe.IsValidVolume(volume))
                    {
                        pipe.WriteByte(ServicePipe.StatusError);
                        ServicePipe.WriteString(pipe, $"'{volume}' is not a volume mount point.");
                        return;
                    }

                    if (version == ServicePipe.MetadataProtocolVersion)
                    {
                        ServeMetadata(pipe, volume, ct);
                        return;
                    }

                    RawMft raw;
                    try
                    {
                        raw = RawMft.Open(volume);
                    }
                    catch (Exception e)
                    {
                        pipe.WriteByte(ServicePipe.StatusError);
                        ServicePipe.WriteString(pipe, e.Message);
                        return;
                    }

                    using (raw)
                    {
                        pipe.WriteByte(ServicePipe.StatusOk);
                        ServicePipe.WriteInt32(pipe, raw.BytesPerMftRecord);
                        ServicePipe.WriteInt64(pipe, raw.Length);
                        raw.CopyTo((chunk, count) =>
                        {
                            ct.ThrowIfCancellationRequested();
                            pipe.Write(chunk, 0, count);
                        });
                        pipe.Flush();
                        pipe.WaitForPipeDrain();
                    }
                }
            }
            catch
            {
                // Client disconnects mid-stream are normal; a request must never kill the service
            }
        }

        /// <summary>
        /// Read the request (version byte + volume string) with a timeout so a
        /// connected-but-silent client cannot pin a worker forever
        /// </summary>
        static (byte Version, string Volume)? ReadRequest(
            NamedPipeServerStream pipe, CancellationToken ct)
        {
            var read = Task.Run(() =>
            {
                var version = pipe.ReadByte();
                if (version != ServicePipe.ProtocolVersion
                    && version != ServicePipe.MetadataProtocolVersion)
                    return ((byte Version, string Volume)?)null;
                return (Version: (byte)version, Volume: ServicePipe.ReadString(pipe));
            });
            try
            {
                // On timeout the caller disposes the pipe, which unblocks the read task
                return read.Wait(15000, ct) ? read.Result : null;
            }
            catch
            {
                return null;
            }
        }

        static void ServeMetadata(NamedPipeServerStream pipe, string volume,
            CancellationToken ct)
        {
            var count = ServicePipe.ReadInt32(pipe);
            if (count < 0 || count > ServicePipe.MaxMetadataBatch)
            {
                pipe.WriteByte(ServicePipe.StatusError);
                ServicePipe.WriteString(pipe, $"Invalid metadata batch size {count}.");
                return;
            }

            var frns = new ulong[count];
            for (var i = 0; i < count; i++)
                frns[i] = unchecked((ulong)ServicePipe.ReadInt64(pipe));

            using var reader = NtfsFileMetadataReader.TryOpen(volume);
            if (reader == null)
            {
                pipe.WriteByte(ServicePipe.StatusError);
                ServicePipe.WriteString(pipe,
                    $"Could not open '{volume}' for file-reference metadata.");
                return;
            }

            pipe.WriteByte(ServicePipe.StatusOk);
            ServicePipe.WriteInt32(pipe, count);
            foreach (var frn in frns)
            {
                ct.ThrowIfCancellationRequested();
                ServicePipe.WriteMetadata(pipe,
                    reader.TryRead(frn, out var metadata) ? metadata : null);
            }
            pipe.Flush();
            pipe.WaitForPipeDrain();
        }
    }
}
