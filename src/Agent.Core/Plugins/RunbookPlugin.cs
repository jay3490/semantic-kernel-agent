using System.ComponentModel;
using Agent.Core.Runbooks;
using Microsoft.SemanticKernel;

namespace Agent.Core.Plugins;

/// <summary>Kernel function that searches the team's markdown runbooks.</summary>
public sealed class RunbookPlugin(RunbookSearchService search)
{
    public const string PluginName = "runbooks";

    [KernelFunction("search_runbooks")]
    [Description("Searches the team's markdown runbooks for the sections most relevant to a problem, such as an error message, alert name or rule id. Returns snippets with a citation (file:line and heading) for each; cite them when you use them, and say so when nothing relevant is found.")]
    public Task<RunbookSearchResult> SearchRunbooksAsync(
        [Description("What to look for, for example 'pods in CrashLoopBackOff after deploy' or 'payment gateway timeout'.")] string query,
        [Description("How many sections to return, 1 to 10.")] int top = 3,
        CancellationToken cancellationToken = default) =>
        search.SearchAsync(query, top, cancellationToken);
}
