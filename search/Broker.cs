using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using search.Core;

namespace search
{
    /// <summary>
    /// Optional elevated helper. The unelevated UI spawns an elevated copy of itself
    /// (one UAC prompt at startup); when the user consents, the broker executes
    /// elevated actions promptlessly on the UI's behalf: 'A'-key opens, access to
    /// admin-only files, and raw $MFT reads when the WinSearchService is not installed.
    /// When the user declines, the app keeps working - each elevated action then
    /// falls back to its own UAC prompt (see Extensions.Open).
    ///
    /// Protocol: UTF-8 text lines written directly on the pipe stream (no buffering
    /// readers, so binary payloads can follow a header line without desync).
    /// Every command ends with a status line: empty = success, anything else = error
    /// message. Commands with a payload (GET_FILE, READ_MFT) send a parsable header
    /// line first; on failure the error message arrives in the header position and
    /// the client throws instead of reading a payload - the channel stays balanced.
    /// </summary>
    public static class Broker
    {
        enum Command : byte { READY, FILES_TO_CLIPBOARD, GET_FILE, SAVE_FILE, DELETE_FILE, OPEN_FILE, READ_MFT, EXIT }

        // ------------------------------------------------------------------
        // Client side - runs in the unelevated UI process
        // ------------------------------------------------------------------

        static PipeStream pipe;
        static readonly object channel = new object(); // Single serialized request channel
        static Task startup = Task.CompletedTask;
        static volatile bool elevationAccepted;

        /// <summary>
        /// True once the elevated broker is connected and answered the handshake
        /// </summary>
        public static bool Available { get; private set; }

        /// <summary>
        /// True when the user declined the UAC prompt for the broker
        /// </summary>
        public static bool Declined { get; private set; }

        /// <summary>
        /// True once the startup UAC prompt was accepted, independently of whether the
        /// elevated broker subsequently completed its pipe handshake.
        /// </summary>
        public static bool ElevationAccepted => elevationAccepted;

        /// <summary>
        /// Raised on a worker thread as soon as the startup UAC prompt is accepted and the
        /// elevated broker process has been launched.
        /// </summary>
        public static event Action StartupElevationAccepted;

        /// <summary>
        /// Spawn the elevated broker in the background. Never blocks the UI:
        /// the UAC prompt can sit unanswered for minutes.
        /// </summary>
        public static void StartClient()
        {
            if (Program.IsProcessElevated) return; // The UI itself is elevated => everything works in-process

            startup = Task.Run(() =>
            {
                // The server end exists before the spawn and allows a single instance only,
                // and the GUID name is unpredictable - an elevated broker connecting to a
                // squatter's pipe would otherwise be an elevated command executor
                var pipeName = $"WinSearch-{Guid.NewGuid():N}";
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath,
                        Arguments = $"--broker {pipeName}",
                        Verb = "runas", // Blocks this background task until the UAC prompt is answered
                        UseShellExecute = true,
                        CreateNoWindow = true
                    });
                    elevationAccepted = true;
                    try { StartupElevationAccepted?.Invoke(); }
                    catch (Exception e) { $"broker elevation notification failed: {e.Message}".Debug(); }

                    var connect = server.WaitForConnectionAsync();
                    if (!connect.Wait(TimeSpan.FromSeconds(30)))
                        throw new TimeoutException("The elevated broker did not connect.");

                    pipe = server;
                    Run(Command.READY);
                    Available = true;
                    "broker available".Debug();
                }
                catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED
                {
                    Declined = true;
                    "broker elevation declined by the user".Debug();
                    server?.Dispose();
                }
                catch (Exception e)
                {
                    $"broker start failed: {e.Message}".Debug();
                    server?.Dispose();
                }
            });
        }

        /// <summary>
        /// Wait for a pending broker spawn (UAC prompt may still be open).
        /// Returns immediately once the spawn finished either way.
        /// </summary>
        public static bool WaitAvailable(TimeSpan timeout)
        {
            try { startup.Wait(timeout); } catch { }
            return Available;
        }

        public static void Stop()
        {
            lock (channel)
            {
                if (pipe?.IsConnected == true)
                    try { WriteLine(pipe, Command.EXIT.ToString()); } catch { }
                try { pipe?.Dispose(); } catch { }
                pipe = null;
                Available = false;
            }
        }

        /// <summary>
        /// Open a file/program elevated without a UAC prompt (backs the 'A' key)
        /// </summary>
        public static Process OpenElevated(string file, string args = "", string workDir = "")
        {
            var pid = 0;
            Run(Command.OPEN_FILE,
                s => { WriteLine(s, file); WriteLine(s, args); WriteLine(s, workDir); },
                s =>
                {
                    var line = ReadLine(s);
                    if (!int.TryParse(line, out pid)) throw new Exception(line ?? "Broker pipe closed.");
                });
            try
            {
                // 0 means no new process opened (happens when the shell delegates to an existing one)
                return pid == 0 ? null : Process.GetProcessById(pid);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Pull a file only readable with admin rights from the broker
        /// </summary>
        public static void CopyFromElevated(string source, string dest, bool overwrite, bool move = false)
        {
            if (source == dest) return;
            $"CopyFromElevated '{source}' => '{dest}'".Debug();
            if (overwrite) dest.DeletePathIfExists();
            Run(Command.GET_FILE, s => WriteLine(s, source), s => ReceivePayload(s, dest));
            if (move) DeleteElevated(source);
        }

        /// <summary>
        /// Push a file to a destination writable only with admin rights
        /// </summary>
        public static void CopyToElevated(string source, string dest, bool overwrite, bool move = false)
        {
            if (source == dest) return;
            $"CopyToElevated '{source}' => '{dest}'".Debug();
            Run(Command.SAVE_FILE, s =>
            {
                WriteLine(s, dest);
                using var file = File.OpenRead(source);
                WriteLine(s, file.Length.ToString());
                file.CopyTo(s);
            });
            if (move) File.Delete(source);
        }

        /// <summary>
        /// Delete a path that requires admin rights
        /// </summary>
        public static void DeleteElevated(string path)
            => Run(Command.DELETE_FILE, s => WriteLine(s, path));

        /// <summary>
        /// Put a file drop list on the clipboard from the elevated side
        /// </summary>
        public static void FilesToClipboardElevated(IEnumerable<string> files)
            => Run(Command.FILES_TO_CLIPBOARD, s =>
            {
                foreach (var f in files) WriteLine(s, f);
                WriteLine(s, "");
            });

        /// <summary>
        /// Read the raw $MFT of a volume through the broker (used when the service is not
        /// installed) and parse it while it streams in - the payload is consumed exactly,
        /// so the status line follows and the channel stays balanced.
        /// </summary>
        internal static IEnumerable<Models.INode> ReadMftNodes(string volumeMountPoint,
            CancellationToken cancellationToken = default)
        {
            IEnumerable<Models.INode> nodes = null;
            Run(Command.READ_MFT, s => WriteLine(s, volumeMountPoint), s =>
            {
                var header = ReadLine(s);
                var parts = header?.Split(' ');
                if (parts?.Length != 2 || !int.TryParse(parts[0], out var bytesPerRecord) || !long.TryParse(parts[1], out var length))
                    throw new Exception(header ?? "Broker pipe closed.");
                nodes = Models.MftDriveReader.GetNodes(s, bytesPerRecord, length, volumeMountPoint,
                    cancellationToken: cancellationToken, drainOnCancellation: true);
            });
            return nodes;
        }

        static void ReceivePayload(Stream s, string dest)
        {
            var line = ReadLine(s);
            if (!long.TryParse(line, out var length)) throw new Exception(line ?? "Pipe closed.");

            FileStream file;
            try
            {
                file = File.Create(dest);
            }
            catch
            {
                Drain(s, length); // The payload is already on the wire - keep the channel in sync
                ReadLine(s);      // Consume the success status line too
                throw;
            }
            using (file) s.CopyStream(file, length);
        }

        static void Run(Command cmd, Action<Stream> writeArgs = null, Action<Stream> readResult = null)
        {
            lock (channel)
            {
                var s = pipe;
                if (s?.IsConnected != true) throw new IOException("The elevated broker is not connected.");

                $"broker command {cmd}".Debug();
                try
                {
                    WriteLine(s, cmd.ToString());
                    writeArgs?.Invoke(s);
                    s.Flush();

                    readResult?.Invoke(s);
                    var status = ReadLine(s) ?? throw new IOException("The broker pipe closed.");
                    $"broker command {cmd} returned '{status}'".Debug();
                    if (status.Length != 0) throw new Exception(status);
                }
                catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is EndOfStreamException)
                {
                    // The pipe is dead => do not try to use it again
                    $"broker pipe broken: {e.Message}".Debug();
                    try { pipe?.Dispose(); } catch { }
                    pipe = null;
                    Available = false;
                    throw;
                }
            }
        }

        // ------------------------------------------------------------------
        // Server side - runs in the elevated --broker process (headless)
        // ------------------------------------------------------------------

        public static void RunServer(string pipeName)
        {
            "broker starting".Debug();
            using var server = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            try
            {
                server.Connect(30000); // The UI created the pipe before spawning us
            }
            catch (Exception e)
            {
                $"broker connect failed: {e.Message}".Debug();
                return;
            }

            while (true)
            {
                string line;
                try { line = ReadLine(server); } catch { return; }
                if (line == null)
                {
                    "broker pipe closed".Debug(); // UI exited
                    return;
                }
                if (!Enum.TryParse<Command>(line, out var cmd)) continue; // Skip until a valid command

                $"broker processing {cmd}".Debug();
                try
                {
                    switch (cmd)
                    {
                        case Command.READY:
                            break;

                        case Command.OPEN_FILE:
                        {
                            var file = ReadLine(server);
                            var args = ReadLine(server);
                            var dir = ReadLine(server);
                            // This process is elevated => a plain shell open runs elevated, no prompt
                            WriteLine(server, $"{file.Open(args, dir)?.Id ?? 0}");
                            break;
                        }

                        case Command.GET_FILE:
                            SendFile(server, ReadLine(server));
                            break;

                        case Command.SAVE_FILE:
                            ReceiveFile(server, ReadLine(server));
                            break;

                        case Command.DELETE_FILE:
                            ReadLine(server).DeletePathIfExists();
                            break;

                        case Command.FILES_TO_CLIPBOARD:
                            ReadLinesUntilEmpty(server).FilesToClipBoard(); // Main is [STAThread]
                            break;

                        case Command.READ_MFT:
                            SendMft(server, ReadLine(server));
                            break;

                        case Command.EXIT:
                            return;

                        default:
                            continue;
                    }
                    WriteLine(server, ""); // Empty status line = success
                    $"broker command {cmd} done".Debug();
                }
                catch (MidStreamException e)
                {
                    // A payload was already partially sent/received => the channel can not be
                    // resynced; exiting closes the pipe, which cleanly unblocks the client
                    $"broker channel broken during {cmd}: {e.Message}".Debug();
                    return;
                }
                catch (Exception e)
                {
                    $"broker command {cmd} failed: {e.Message}".Debug();
                    try { WriteLine(server, OneLine(e.Message)); } catch { return; }
                }
            }
        }

        static void SendFile(Stream s, string path)
        {
            using var file = File.OpenRead(path);
            WriteLine(s, file.Length.ToString());
            try
            {
                file.CopyTo(s);
                s.Flush();
            }
            catch (Exception e)
            {
                throw new MidStreamException(e);
            }
        }

        static void ReceiveFile(Stream s, string dest)
        {
            var line = ReadLine(s);
            if (!long.TryParse(line, out var length)) throw new Exception(line ?? "Pipe closed.");

            FileStream file;
            try
            {
                file = File.Create(dest);
            }
            catch
            {
                Drain(s, length); // The payload is already on the wire - keep the channel in sync
                throw;
            }
            try
            {
                using (file) s.CopyStream(file, length);
            }
            catch (Exception e)
            {
                throw new MidStreamException(e);
            }
        }

        static void SendMft(Stream s, string volume)
        {
            if (!ServicePipe.IsValidVolume(volume))
                throw new ArgumentException($"'{volume}' is not a volume mount point.");

            using var raw = RawMft.Open(volume);
            WriteLine(s, $"{raw.BytesPerMftRecord} {raw.Length}");
            try
            {
                raw.CopyTo((chunk, count) => s.Write(chunk, 0, count));
                s.Flush();
            }
            catch (Exception e)
            {
                throw new MidStreamException(e);
            }
        }

        static IEnumerable<string> ReadLinesUntilEmpty(Stream s)
        {
            var lines = new List<string>();
            string line;
            while (!string.IsNullOrEmpty(line = ReadLine(s))) lines.Add(line);
            return lines;
        }

        static void Drain(Stream s, long bytes)
        {
            var buffer = new byte[32768];
            while (bytes > 0)
            {
                var read = s.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes));
                if (read <= 0) return;
                bytes -= read;
            }
        }

        // ------------------------------------------------------------------
        // Framing - text lines written directly on the pipe stream. No buffering
        // reader exists, so raw payload bytes can safely follow any line.
        // ------------------------------------------------------------------

        static void WriteLine(Stream s, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }

        static string ReadLine(Stream s)
        {
            var bytes = new List<byte>(64);
            while (true)
            {
                var b = s.ReadByte();
                if (b < 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
                if (b == '\n') return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add((byte)b);
            }
        }

        static string OneLine(string text) => text?.Replace('\r', ' ').Replace('\n', ' ') ?? "";

        sealed class MidStreamException : Exception
        {
            public MidStreamException(Exception inner) : base(inner.Message, inner) { }
        }
    }
}
