using Agent.Core.Filters;

namespace Agent.Tests.Filters;

public sealed class ToolAllowListTests
{
    [Theory]
    [InlineData("kubernetes", "lint_manifest", true)]
    [InlineData("KUBERNETES", "LINT_MANIFEST", true)]
    [InlineData("time", "utc_now", true)]
    [InlineData("time", "convert_time", false)]
    [InlineData("logs", "analyze_log_file", false)]
    [InlineData(null, "lint_manifest", false)]
    public void Matches_plugin_wildcards_and_exact_functions(string? plugin, string function, bool allowed)
    {
        var allowList = new ToolAllowList(["kubernetes.*", "time.utc_now"]);

        Assert.Equal(allowed, allowList.IsAllowed(plugin, function));
    }

    [Fact]
    public void Star_allows_everything()
    {
        Assert.True(new ToolAllowList(["*"]).IsAllowed("anything", "at_all"));
    }

    [Fact]
    public void Empty_list_allows_nothing()
    {
        Assert.False(new ToolAllowList([]).IsAllowed("time", "utc_now"));
    }

    [Fact]
    public void Rejects_entries_without_a_plugin()
    {
        Assert.Throws<ArgumentException>(() => new ToolAllowList(["utc_now"]));
    }
}
