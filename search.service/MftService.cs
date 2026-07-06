using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace search.Service
{
    /// <summary>
    /// The WinSearchService Windows service host - all work happens in PipeServer
    /// </summary>
    sealed class MftService : ServiceBase
    {
        public const string Name = "WinSearchService";

        CancellationTokenSource cts;
        Task server;

        public MftService() => ServiceName = Name;

        protected override void OnStart(string[] args)
        {
            cts = new CancellationTokenSource();
            server = Task.Run(() => PipeServer.Run(cts.Token));
        }

        protected override void OnStop()
        {
            cts.Cancel();
            try { server?.Wait(5000); } catch { }
        }
    }
}
