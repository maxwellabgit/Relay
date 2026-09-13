using System.Net;
using Relay.Core.Config;
using Relay.Core.Mind;
using Relay.Core.Model;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.State;
using Relay.Gateway;
using Relay.Tests.Support;
using static Relay.Core.Mind.ScriptedMind;

namespace Relay.Tests;

/// <summary>
/// Step 3 — real search: one brokered web_search tool behind ISearchClient, gated by standing grant or
/// per-task approval, with hits stored as citable artifacts. No live network.
/// </summary>
public sealed class SearchTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private static void WithSearch(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
        s.Search.Enabled = true;
        s.Search.Endpoint = "https://search.test/v1/web/search";
        s.Search.SecretName = "search";
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Respond(request));
    }

    [Fact]
    public void WebSearchWithoutGrantFails()
    {
        var search = new FakeSearchClient().Reply(new SearchHitResult("A", "https://example.test/a", "snip"));
        using var h = new Harness(_tmp.Root, WithSearch, searchClient: search).Start();
        var denied = h.TurnWorld().Tools.Call("web_search", new Dictionary<string, string> { ["query"] = "x" });
        Assert.False(denied.Ok);
        Assert.Contains("not granted", denied.Error);
        Assert.Empty(search.Requests);
    }

    [Fact]
    public void WebSearchWithStandingGrantStoresArtifactsAndReadArtifactOpensThem()
    {
        var search = new FakeSearchClient().Reply(
            new SearchHitResult("Deputy", "https://example.test/deputy", "Shift scheduling for restaurants."),
            new SearchHitResult("7shifts", "https://example.test/7shifts", "Restaurant workforce platform."));
        using var h = new Harness(_tmp.Root, WithSearch, searchClient: search).Start();
        Assert.True(h.Coordinator.UpdatePreference("sources.allowOnlineSearch", "true"));

        var tools = h.TurnWorld().Tools;
        var ok = tools.Call("web_search", new Dictionary<string, string> { ["query"] = "shift scheduling", ["limit"] = "2" });
        Assert.True(ok.Ok, ok.Error);
        Assert.Contains("2 hit(s)", ok.Summary);
        Assert.Equal("shift scheduling", Assert.Single(search.Requests).Query);
        Assert.Equal(2, ok.Hits!.Count);
        Assert.All(ok.Hits, hit => Assert.Equal(SearchIndex.ArtifactKind, hit.Kind));

        var firstId = ok.Hits[0].Id;
        var read = tools.Call("read_artifact", new Dictionary<string, string> { ["artifactId"] = firstId });
        Assert.True(read.Ok, read.Error);
        Assert.Contains("Deputy", read.Hits![0].Text);
        Assert.Contains(h.Index.Search("Deputy", null, 5), hit => hit.Id == firstId);
        Assert.Equal("Deputy\nhttps://example.test/deputy\nShift scheduling for restaurants.", h.SearchArtifacts!.Read(firstId));
    }

    [Fact]
    public void WebSearchWithoutConfiguredClientFailsClearly()
    {
        using var h = new Harness(_tmp.Root, s =>
        {
            s.Orchestrator.Mode = OrchestratorSettings.Mind;
            s.Model.Enabled = true;
        }).Start();
        Assert.True(h.Coordinator.UpdatePreference("sources.allowOnlineSearch", "true"));
        var fail = h.TurnWorld().Tools.Call("web_search", new Dictionary<string, string> { ["query"] = "x" });
        Assert.False(fail.Ok);
        Assert.Contains("not configured", fail.Error);
    }

    [Fact]
    public void PerTaskApprovalGrantsWebSearchAfterAllowSearchIsApproved()
    {
        var search = new FakeSearchClient().Reply(
            new SearchHitResult("Homebase", "https://example.test/homebase", "Free-tier scheduling."));
        string? hitId = null;
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Look up Homebase.", 2_000, search: true), "Asking to search", Read(0.7, MindRead.NeedExternalReasoning))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Returned } => MindStep.Of(Tool("web_search", ("query", "Homebase")), "Searching now that search is granted"),
                ToolObserved { Tool: "web_search", Ok: true } t => MindStep.Of(Tool("read_artifact", ("artifactId", hitId = Assert.Single(t.Ids))), "Opening the hit"),
                ToolObserved { Tool: "read_artifact", Ok: true } => MindStep.Of(Say("Homebase has a free tier."), "Answered"),
                ApprovalObserved { Granted: true } => MindStep.Of(Wait("waiting for the reply"), "Approved; waiting"),
                DelegateObserved => MindStep.Of(Wait("streaming"), "Delegate is working"),
                _ => MindStep.Of(Wait("…"), "Waiting"),
            });

        using var s = Scenario.New(_tmp, cfg =>
            {
                WithSearch(cfg);
                cfg.ExternalModels.Add(new ExternalModelProfile
                {
                    Name = "research",
                    Endpoint = "https://api.example.test/v1/chat/completions",
                    Model = "gpt-5-nano",
                    SecretName = "external-research",
                    SupportsSearch = true,
                });
            },
            externalClients: _ => new ScriptedModelClient().Reply("Delegate answered without searching."),
            inlinePost: false,
            mind: mind,
            searchClient: search);
        s.Ask("research Homebase")
            .ExpectState(RelayState.Ready /* awaiting approval */)
            .Approve()
            .PumpUntil("answer", () => s.ForegroundSettled)
            .ExpectState(RelayState.Ready)
            .ExpectOutcome("executed")
            .ExpectAnswerContains("Homebase");

        Assert.Single(search.Requests);
        Assert.NotNull(hitId);
        Assert.Contains(s.Response.Citations, c => c.Id == hitId && c.Kind == SearchIndex.ArtifactKind);
    }

    [Fact]
    public void MindWithStandingGrantSearchesAndCitesViaReadArtifact()
    {
        var search = new FakeSearchClient().Reply(
            new SearchHitResult("When I Work", "https://example.test/wiw", "Scheduling for small teams."));
        string? hitId = null;
        var mind = new ScriptedMind()
            .Step(Tool("web_search", ("query", "When I Work")), "Searching the web", Read(0.5, MindRead.NeedExternalReasoning))
            .Then(req =>
            {
                var tool = Assert.IsType<ToolObserved>(req.Transcript[^1]);
                Assert.True(tool.Ok, tool.Summary);
                hitId = Assert.Single(tool.Ids);
                return MindStep.Of(Tool("read_artifact", ("artifactId", hitId!)), "Opening the hit");
            })
            .Then(_ => MindStep.Of(Say("When I Work suits small teams."), "Answered"));

        using var s = Scenario.New(_tmp, WithSearch, mind: mind, searchClient: search)
            .Do("allow online search", c => Assert.True(c.UpdatePreference("sources.allowOnlineSearch", "true")))
            .Ask("what is When I Work?")
            .ExpectState(RelayState.Ready)
            .ExpectOutcome("answered")
            .ExpectAnswerContains("When I Work");

        Assert.Single(search.Requests);
        Assert.Contains(s.Response.Citations, c => c.Id == hitId);
    }

    [Fact]
    public async Task HttpSearchClientParsesBraveShapeAndReportsErrorsAsData()
    {
        var secrets = new MemorySecretStore();
        secrets.Set("search", "tok");
        var handler = new FakeHandler
        {
            Respond = req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.Contains("q=shift", req.RequestUri!.Query);
                Assert.Equal("tok", req.Headers.GetValues("X-Subscription-Token").Single());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"web":{"results":[{"title":"A","url":"https://a.test","description":"one"},{"title":"B","url":"https://b.test","description":"two"}]}}""",
                        System.Text.Encoding.UTF8, "application/json"),
                };
            },
        };
        using var client = new HttpSearchClient(new SearchSettings { Enabled = true, Endpoint = "https://search.test/v1/web/search", SecretName = "search", TimeoutMs = 5_000 }, secrets, handler);
        var ok = await client.SearchAsync(new SearchRequest("shift", 5), CancellationToken.None);
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal(2, ok.Hits.Count);
        Assert.Equal("A", ok.Hits[0].Title);

        secrets.Remove("search");
        var noKey = await client.SearchAsync(new SearchRequest("x"), CancellationToken.None);
        Assert.False(noKey.Ok);
        Assert.Contains("No API key", noKey.Error);

        Assert.Throws<ArgumentException>(() => new HttpSearchClient(new SearchSettings { Endpoint = "http://search.test/v1" }, secrets));
    }

    [Fact]
    public void SupportsSearchWithoutSearchClientKeepsAllowSearchCorrection()
    {
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Look this up.", 2_000, search: true), "Delegating", Read(0.8, MindRead.NeedExternalReasoning))
            .Then(req =>
            {
                Assert.Contains(req.Transcript.OfType<SystemObserved>(), o => o.Text.Contains("cannot search online"));
                return MindStep.Of(Say("I cannot search."), "Answered");
            });
        using var s = Scenario.New(_tmp, cfg =>
            {
                cfg.Orchestrator.Mode = OrchestratorSettings.Mind;
                cfg.Model.Enabled = true;
                cfg.ExternalModels.Add(new ExternalModelProfile
                {
                    Name = "research",
                    Endpoint = "https://api.example.test/v1/chat/completions",
                    Model = "m",
                    SecretName = "external-research",
                    SupportsSearch = true,
                });
            }, externalClients: _ => new ScriptedModelClient().Reply("ok"), mind: mind)
            .Ask("research this");

        var proposal = s.Snap.PendingProposals.FirstOrDefault()
            ?? s.Response.Proposals.FirstOrDefault(p => p.Action == Actions.ModelRequest);
        Assert.NotNull(proposal);
        Assert.Equal("false", proposal!.Target["allowSearch"]);
    }
}
