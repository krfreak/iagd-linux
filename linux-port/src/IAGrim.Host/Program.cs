using IAGrim.Host;
using IAGrim.Platform;

// Headless entry point. The desktop app (IAGrim.App) runs the same HostServer in-process and
// puts a window in front of it; this one is for running without a UI — over SSH, or alongside
// a browser pointed at the port.

var port = 5680;
for (var i = 0; i < args.Length - 1; i++) {
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed)) port = parsed;
}

// Which collection to serve, before anything opens it.
if (!Startup.SelectDatabase(args, AppSettings.Load(), Console.Out)) return 1;

await using var server = new HostServer(port);
if (server.DiscoveryWarning is not null) {
    Console.WriteLine($"warning: {server.DiscoveryWarning}");
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

server.Start();

Console.WriteLine($"iagd-host listening on {server.Url}");
Console.WriteLine($"  database {LinuxPaths.DatabaseFile}");
Console.WriteLine($"  bridge   {server.Bridge?.Root ?? "(not found)"}");
Console.WriteLine("Ctrl+C to stop.");

try {
    await Task.Delay(Timeout.Infinite, shutdown.Token);
}
catch (OperationCanceledException) { }

Console.WriteLine("stopped.");
return 0;
