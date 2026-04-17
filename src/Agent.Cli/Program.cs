using Agent.Cli;
using Agent.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 'devops-agent lint ...' runs the linters non-interactively and needs no model or configuration.
if (args is ["lint", .. var lintArgs])
{
    using var interrupted = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        interrupted.Cancel();
    };

    try
    {
        return await LintCommand.RunAsync(lintArgs, Console.Out, Console.Error, interrupted.Token);
    }
    catch (OperationCanceledException) when (interrupted.IsCancellationRequested)
    {
        return 130;
    }
}

// appsettings.json ships next to the executable; the workspace root stays relative to the current directory.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddDevOpsAgent(builder.Configuration);
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<Repl>(sp, Console.In, Console.Out));

using var host = builder.Build();
var repl = host.Services.GetRequiredService<Repl>();

Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C stops the reply being streamed; with nothing running it exits as usual.
    e.Cancel = repl.CancelCurrentTurn();
};

await repl.RunAsync();
return 0;
