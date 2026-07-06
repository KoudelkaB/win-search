using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using search.Core;

namespace search.Service
{
    static class Program
    {
        static void Main(string[] args)
        {
            if (args.Contains("--console") || Environment.UserInteractive)
            {
                // Debug mode: run the pipe loop in the console (needs an elevated prompt to be useful)
                Console.WriteLine($"Win Search MFT service - console mode, pipe '{ServicePipe.PipeName}'. Ctrl+C to quit.");
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (o, e) => { e.Cancel = true; cts.Cancel(); };
                PipeServer.Run(cts.Token);
                return;
            }

            ServiceBase.Run(new MftService());
        }
    }
}
