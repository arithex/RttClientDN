using System;
using System.Threading;
using System.Windows;

namespace RttClientDN
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            // One instance per user session.
            using (var mutex = new Mutex(true, "RttClientDN_SingleInstance", out bool createdNew))
            {
                if (!createdNew) return 0;

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var controller = new AppController(app);
                app.Startup += (s, e) => controller.Start();
                return app.Run();
            }
        }
    }
}
