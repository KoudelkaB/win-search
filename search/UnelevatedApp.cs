using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Linq;
using System.Text;
using System.IO;
using System.IO.Pipes;
using System.Windows.Documents;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;

namespace search
{
    public static class UnelevatedApp
    {
        // Public functions
        public static Process OpenFromUnelevated(this string file, string args = "", string workDir = "")
        {
            $"OpenFromUnevevated '{file}' '{args}'".Debug();
            int pid = 0;
            Run(Command.OPEN_FILE, w => w.WriteLine($"{file}\n{args}\n{workDir}"), r => pid = int.Parse(r.ReadLine()));
            try
            {
                // 0 means no new process opened (happens when shell delegates to existing VS)
                return pid == 0 ? null : Process.GetProcessById(pid);
            }
            catch
            {
                return null;
            }
        }

        public static void CopyFromUnevevated(this string source, string dest, bool overwrite, bool move = false)
        {
            if (source != dest) // Not possible to have two files with the same name even elevated x unelevated!
            {
                // TODO: Implement copy directories (not seen by elevated) as well
                $"CopyFromUnevevated '{source}' => '{dest}'".Debug();
                if (overwrite) dest.DeletePathIfExists();
                //if (move) dest += ">";
                Run(Command.GET_FILE, w => w.WriteLine(source), r => r.SaveStream(dest));
            }
        }

        public static void CopyToUnevevated(this string source, string dest, bool overwrite, bool move = false) => Run(Command.SAVE_FILE, w =>
        {
            if (source != dest)
            {
                $"CopyToUnevevated '{source}' => '{dest}'".Debug();
                w.WriteLine(dest);
                w.SendFile(source);
            }
        });

        enum Command : byte { READY, FILES_TO_CLIPBOARD, GET_FILE, SAVE_FILE, DELETE_FILE, OPEN_FILE, EXIT };

        /// <summary>
        /// Save the stream to file - internal protocol
        /// </summary>
        /// <param name="r"></param>
        /// <param name="path"></param>
        static void SaveStream(this StreamReader r, string path)
        {
            $"saves stream to '{path}'".Debug();
            // Length of the stream to save
            var line = r.ReadLine();
            if (!long.TryParse(line, out var len)) throw new Exception(line); // or exception if error reading the file
            // Copy the length of the stream 
            using var file = File.Create(path);
            r.BaseStream.CopyStream(file, len);
        }

        /// <summary>
        /// Get the stream from file - internal protocol
        /// </summary>
        /// <param name="r"></param>
        /// <param name="path"></param>
        static void SendFile(this StreamWriter w, string path)
        {
            $"sends file '{path}'".Debug();
            using var file = File.OpenRead(path);
            w.WriteLine(file.Length);
            w.Flush(); // Ensure writing length before file content
            file.CopyTo(w.BaseStream);
        }

        /// <summary>
        /// Is current instance elevated?
        /// </summary>
        public static bool IsElevated { get; private set; } = false;

        static NamedPipeClientStream pipe;
        static StreamWriter request;
        static StreamReader response;

        static void Run(Command cmd, Action<StreamWriter> args = null, Action<StreamReader> getResult = null)
        {
            if (request == null) throw new Exception("UnelevatedApp - unelevated partner is not connected!");

            $"runs command {cmd}".Debug();
            try
            {
                //Send request
                request.WriteLine(cmd.ToString());
                args?.Invoke(request);
                request.Flush();

                //Receive response
                getResult?.Invoke(response);
                var ex = response.ReadLine(); // return value
                if (ex == null) throw new IOException("Pipe to the unelevated partner closed");
                $"command {cmd} returned '{ex}'".Debug();
                if (!string.IsNullOrEmpty(ex)) throw new Exception(ex);
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is ArgumentNullException)
            {
                // The pipe is dead => do not try to use it again
                $"pipe to unelevated partner broken: {e.Message}".Debug();
                try { pipe?.Dispose(); } catch { }
                request = null;
                response = null;
                throw;
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length == 1)
            {
                if (args[0].ToUpper() == "UI")
                {
                    // Run only UI
                    App.Main();
                    return;
                }

                // Elevated App
                IsElevated = true;
                "starting".Debug(/*true*/);
                try
                {
                    pipe = new NamedPipeClientStream(args[0]);
                    pipe.Connect();
                    request = new StreamWriter(pipe);
                    response = new StreamReader(pipe);
                    Run(Command.READY);
                }
                catch (Exception e)
                {
                    $"connection exception: {e}".Debug();
                }

                //Run the search app
                try
                {
                    "starting UI".Debug();
                    App.Main();
                    "closed UI".Debug();
                    Run(Command.EXIT);
                }
                catch (Exception e)
                {
                    $"UI exception: {e}".Debug();
                }
                "exiting".Debug();
                return;
            }

            // Unelevated App
            "starting".Debug();
            var pipeName = $"{Process.GetCurrentProcess().ProcessName}-{Process.GetCurrentProcess().Id}";
            using var cmdPipe = new NamedPipeServerStream(pipeName);

            try
            {
                //Start elevated instance
                var elevated = Process.Start(new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    Arguments = pipeName,
                    Verb = "runas", //Run the process elevated (when UseShellExecute)
                    UseShellExecute = true,
                    CreateNoWindow = true
                });

                //Run the loop waiting for commands

                "waiting for Admin connection".Debug();
                cmdPipe.WaitForConnection();
                "Admin connected".Debug();
                using var cmd = new StreamReader(cmdPipe);
                using var result = new StreamWriter(cmdPipe);
                IEnumerable<string> ReadLines()
                {
                    string line;
                    while (!string.IsNullOrEmpty(line = cmd.ReadLine())) yield return line;
                };
                while (!elevated.HasExited)
                {
                    try
                    {
                        var cmdLine = cmd.ReadLine();
                        if (cmdLine == null)
                        {
                            "Pipe closed".Debug();
                            break;
                        }
                        var c = Enum.Parse(typeof(Command), cmdLine);
                        $"processing command {c}".Debug();
                        switch (c)
                        {
                            case Command.READY:
                                break;
                            case Command.FILES_TO_CLIPBOARD:
                                ReadLines().FilesToClipBoard(); // @TODO - is it needed?
                                break;
                            case Command.DELETE_FILE:
                                File.Delete(cmd.ReadLine());
                                break;
                            case Command.OPEN_FILE:
                                result.WriteLine(cmd.ReadLine().Open(cmd.ReadLine(), cmd.ReadLine())?.Id ?? 0);
                                break;
                            case Command.GET_FILE:
                                result.SendFile(cmd.ReadLine());
                                break;
                            case Command.SAVE_FILE:
                                cmd.SaveStream(cmd.ReadLine());
                                break;
                            case Command.EXIT:
                                return;        // The only command not sending response
                            default: continue; // Skip until valid command is received
                        }
                        result.WriteLine(); //Empty line denotes successful finish
                        $"command {c} done".Debug();
                    }
                    catch (Exception e)
                    {
                        result.WriteLine(e.Message);
                        $"command exception: {e}".Debug();
                    }
                    result.Flush();
                }
            }
            catch (Exception e)
            {
                $"Unelevated commander exception: {e}".Debug();
            }
            finally
            {
                "exiting".Debug();
            }
        }
    }
}
