using Agent.Core;
using Agent.Core.Review;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Agent;

public sealed class AnswerReviewTests
{
    private const string LintCall = "Lint kubernetes/bad-deployment.yaml";

    private static AgentHarness Harness(bool review = true) =>
        new(new Dictionary<string, string?> { ["Agent:ReviewLintAnswers"] = review ? "true" : "false" });

    /// <summary>The primary agent lints the bad manifest, then drafts <paramref name="draft"/>.</summary>
    private static void PrimaryAgentDrafts(AgentHarness harness, string draft) =>
        harness.Model
            .ThenCallTool("kubernetes", "lint_manifest_file", new { path = "kubernetes/bad-deployment.yaml" })
            .ThenAnswer(draft);

    [Fact]
    public async Task Is_off_by_default()
    {
        var harness = Harness(review: false);
        PrimaryAgentDrafts(harness, "K8S006 on line 26.");

        var reply = await harness.Agent.ChatAsync("s1", LintCall);

        Assert.Null(reply.Review);
        Assert.Equal(2, harness.Model.Requests.Count);
    }

    [Fact]
    public async Task An_approved_draft_is_returned_unchanged_after_a_tool_less_review()
    {
        var harness = Harness();
        PrimaryAgentDrafts(harness, "The log-shipper container is privileged (K8S006, line 26).");
        harness.Model.ThenAnswer("""{"verdict": "approve", "issues": []}""");

        var reply = await harness.Agent.ChatAsync("s1", LintCall);

        Assert.Equal("The log-shipper container is privileged (K8S006, line 26).", reply.Reply);
        Assert.Equal(new AnswerReview(AnswerReview.Approved, []).Verdict, reply.Review!.Verdict);
        Assert.Empty(reply.Review.Issues);

        // The reviewer saw the question, the draft and the raw result, and was offered no tools.
        var reviewRequest = harness.Model.Requests[2];
        Assert.Equal([AuthorRole.System, AuthorRole.User], reviewRequest.Select(m => m.Role));
        Assert.Contains("You review a DevOps assistant's answer", reviewRequest[0].Content);
        Assert.Contains(LintCall, reviewRequest[1].Content);
        Assert.Contains("(K8S006, line 26)", reviewRequest[1].Content);
        Assert.Contains("[kubernetes.lint_manifest_file]", reviewRequest[1].Content);
        Assert.Contains("\"ruleId\":\"K8S006\"", reviewRequest[1].Content);
        Assert.Null(harness.Model.Settings[2]!.FunctionChoiceBehavior);
    }

    [Fact]
    public async Task A_revised_answer_replaces_the_draft_in_the_reply_and_the_history()
    {
        var harness = Harness();
        PrimaryAgentDrafts(harness, "No problems found.");
        harness.Model.ThenAnswer("""
            ```json
            {"verdict": "revise", "issues": ["The report has 1 error: K8S006."], "revised_answer": "One error: log-shipper is privileged (K8S006, line 26)."}
            ```
            """);

        var reply = await harness.Agent.ChatAsync("s1", LintCall);

        Assert.Equal("One error: log-shipper is privileged (K8S006, line 26).", reply.Reply);
        Assert.Equal(AnswerReview.Revised, reply.Review!.Verdict);
        Assert.Equal(["The report has 1 error: K8S006."], reply.Review.Issues);
        Assert.Equal(reply.Reply, harness.History("s1")[^1].Content);
    }

    [Fact]
    public async Task Invented_rule_ids_are_flagged_even_when_the_reviewer_approves()
    {
        var harness = Harness();
        PrimaryAgentDrafts(harness, "Fix K8S006 and K8S042.");
        harness.Model.ThenAnswer("""{"verdict": "approve", "issues": []}""");

        var reply = await harness.Agent.ChatAsync("s1", LintCall);

        Assert.Equal("Fix K8S006 and K8S042.", reply.Reply);
        Assert.Equal(AnswerReview.Flagged, reply.Review!.Verdict);
        Assert.Contains("K8S042", Assert.Single(reply.Review.Issues));
        Assert.Contains("Automatic checks found", harness.Model.Requests[2][1].Content);
    }

    [Fact]
    public async Task An_unusable_review_keeps_the_draft()
    {
        var harness = Harness();
        PrimaryAgentDrafts(harness, "K8S006 on line 26.");
        harness.Model.ThenAnswer("Looks fine to me!");

        var reply = await harness.Agent.ChatAsync("s1", LintCall);

        Assert.Equal("K8S006 on line 26.", reply.Reply);
        Assert.Equal(AnswerReview.Unavailable, reply.Review!.Verdict);
    }

    [Fact]
    public async Task A_failed_reviewer_call_keeps_the_draft_and_completes_the_turn()
    {
        var harness = Harness();
        PrimaryAgentDrafts(harness, "K8S006 on line 26.");
        harness.Model.Then(_ => throw new HttpRequestException("reviewer unreachable"));

        var reply = await harness.Agent.ChatAsync("s1", LintCall);

        Assert.Equal("K8S006 on line 26.", reply.Reply);
        Assert.Equal(AnswerReview.Unavailable, reply.Review!.Verdict);
        Assert.Contains("reviewer unreachable", reply.Review.Issues[0]);
        Assert.Equal(5, harness.History("s1").Count);
    }

    [Fact]
    public async Task Turns_without_lint_results_are_not_reviewed()
    {
        var harness = Harness();
        harness.Model.ThenCallTool("time", "utc_now").ThenAnswer("It is noon.");

        var reply = await harness.Agent.ChatAsync("s1", "what time is it?");

        Assert.Null(reply.Review);
        Assert.Equal(2, harness.Model.Requests.Count);
    }

    [Fact]
    public async Task Streaming_reports_the_review_before_the_final_reply()
    {
        var harness = Harness();
        PrimaryAgentDrafts(harness, "No problems found.");
        harness.Model.ThenAnswer("""{"verdict": "revise", "issues": ["missed K8S006"], "revised_answer": "K8S006 on line 26."}""");

        var events = new List<AgentStreamEvent>();
        await foreach (var streamEvent in harness.Agent.StreamChatAsync("s1", LintCall))
        {
            events.Add(streamEvent);
        }

        var review = Assert.IsType<ReviewCompleted>(events[^2]);
        Assert.Equal(AnswerReview.Revised, review.Review.Verdict);
        var done = Assert.IsType<ReplyCompleted>(events[^1]);
        Assert.Equal("K8S006 on line 26.", done.Reply.Reply);
        Assert.Equal("No problems found.", string.Concat(events.OfType<TextDelta>().Select(d => d.Text)));
    }

    [Fact]
    public void Rule_id_check_only_reports_ids_missing_from_every_result()
    {
        LintEvidence[] evidence = [new("helm.lint_helm_chart", """{"findings":[{"ruleId":"HELM002"}]}"""), new("terraform.lint_terraform", """{"findings":[{"ruleId":"TF010"}]}""")];

        Assert.Equal(["GHA001"], LintAnswerReviewer.UnsupportedRuleIds("HELM002, TF010 and GHA001; not XK8S002", evidence));
    }
}
