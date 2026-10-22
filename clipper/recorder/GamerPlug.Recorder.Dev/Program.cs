using GamerPlug.Recorder.Core.Ipc;
using GamerPlug.Recorder.Dev;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        int port = ParsePort(args);
        if (port <= 0)
        {
            Console.Error.WriteLine("usage: GamerPlug.Recorder.Dev --electronPort <port> [--wsComms]");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Exception? last = null;
        for (int attempt = 0; attempt < 25 && !cts.IsCancellationRequested; attempt++)
        {
            var rpc = new JsonRpcClient();
            var host = new DevRecorderHost(rpc);
            try
            {
                await rpc.ConnectAsync(port, cts.Token);
                Console.Error.WriteLine($"dev recorder connected to 127.0.0.1:{port}");
                await host.RunAsync(cts.Token);
                host.Dispose();
                return 0;
            }
            catch (OperationCanceledException)
            {
                host.Dispose();
                return 0;
            }
            catch (Exception ex)
            {
                last = ex;
                host.Dispose();
                try { await Task.Delay(200, cts.Token); } catch (OperationCanceledException) { return 0; }
            }
        }

        Console.Error.WriteLine("dev recorder failed: " + last);
        return 1;
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
