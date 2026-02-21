using System.ComponentModel;
using Agent.Core.Filters;
using Agent.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Agent.Tests.Filters;

public sealed class ToolInvocationFilterTests
{
    private readonly ListLogger<ToolInvocationFilter> _logger = new();
    private readonly CountingPlugin _plugin = new();
    private readonly Kernel _kernel;

    public ToolInvocationFilterTests()
    {
        _kernel = new Kernel();
        _kernel.Plugins.AddFromObject(_plugin, "ops");
        _kernel.FunctionInvocationFilters.Add(new ToolInvocationFilter(new ToolAllowList(["ops.status", "ops.crash"]), _logger));
    }

    [Fact]
    public async Task Allowed_functions_run_and_are_logged()
    {
        var result = await _kernel.InvokeAsync("ops", "status", new KernelArguments { ["service"] = "orders-api" });

        Assert.Equal("orders-api is up", result.GetValue<string>());
        Assert.Equal(1, _plugin.StatusCalls);
        Assert.Contains(_logger.Entries, e => e is { Level: LogLevel.Information, Message: "Tool call ops.status(service=orders-api)" });
        Assert.Contains(_logger.Entries, e => e.Message.StartsWith("Tool call ops.status completed in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Functions_outside_the_allow_list_are_blocked_before_they_run()
    {
        var error = await Assert.ThrowsAsync<ToolNotAllowedException>(() =>
            _kernel.InvokeAsync("ops", "restart", new KernelArguments { ["service"] = "orders-api" }));

        Assert.Equal("ops.restart", error.ToolName);
        Assert.Equal(0, _plugin.RestartCalls);
        Assert.Contains(_logger.Entries, e => e is { Level: LogLevel.Warning, Message: "Tool call ops.restart blocked: not on the allow-list" });
    }

    [Fact]
    public async Task Failures_are_logged_and_rethrown()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _kernel.InvokeAsync("ops", "crash"));

        Assert.Contains(_logger.Entries, e => e is { Level: LogLevel.Warning } && e.Message.StartsWith("Tool call ops.crash failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Long_arguments_are_truncated_in_the_log()
    {
        await _kernel.InvokeAsync("ops", "status", new KernelArguments { ["service"] = new string('a', 500) });

        var entry = Assert.Single(_logger.Entries, e => e.Message.StartsWith("Tool call ops.status(", StringComparison.Ordinal));
        Assert.EndsWith("... (500 chars))", entry.Message);
    }

    private sealed class CountingPlugin
    {
        public int StatusCalls { get; private set; }

        public int RestartCalls { get; private set; }

        [KernelFunction("status"), Description("Reports service status.")]
        public string Status(string service)
        {
            StatusCalls++;
            return $"{service} is up";
        }

        [KernelFunction("crash"), Description("Always fails.")]
        public string Crash() => throw new InvalidOperationException("boom");

        [KernelFunction("restart"), Description("Restarts a service.")]
        public string Restart(string service)
        {
            RestartCalls++;
            return $"{service} restarted";
        }
    }
}
