using BlazorApi.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // The first Ctrl+C stops 'generate --watch' cleanly; the process then exits normally.
    e.Cancel = !cancellation.IsCancellationRequested;
    cancellation.Cancel();
};

return new CliApp(Console.Out, Console.Error, Directory.GetCurrentDirectory()) { Cancellation = cancellation.Token }.Run(args);
