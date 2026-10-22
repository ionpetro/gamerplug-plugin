using System.Windows.Forms;
using GamerPlug.Recorder;
using GamerPlug.Recorder.Core.Ipc;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int port = ParsePort(args);
        if (port <= 0)
        {
            Console.Error.WriteLine("usage: GamerPlug.Recorder --electronPort <port> [--wsComms]");
            return 2;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        // Hidden message-only window so WinRT capture + the hotkey hook have an STA pump.
        using var ctx = new ApplicationContext();
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Application.Exit(); };

        var rpc = new JsonRpcClient();
        var host = new RecorderHost(rpc);

        _ = Task.Run(async () =>
        {
            try
            {
                await rpc.ConnectAsync(port, cts.Token);
                await host.RunAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("recorder failed: " + ex);
            }
            finally
            {
                cts.Cancel();
                Application.Exit();
            }
        });

        Application.Run(ctx);
        host.Dispose();
        return 0;
    }

    private static int ParsePort(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--electronPort" or "--port")
            {
                if (int.TryParse(args[i + 1], out int p)) return p;
            }
        }
        string? env = Environment.GetEnvironmentVariable("GAMERPLUG_ELECTRON_PORT");
        return int.TryParse(env, out int fromEnv) ? fromEnv : 0;
    }
}
