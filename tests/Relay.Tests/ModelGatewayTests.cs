using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Model;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Gateway;
using Relay.Tests.Support;
using Xunit;

namespace Relay.Tests;

/// <summary>
/// The model gateway: a scripted model drives the JSON tool loop, citations are verified against tool
/// results, proposals go through policy, and malformed or failed model output ends visibly. The HTTP
/// client is tested against a fake handler; a live test runs only when RELAY_LIVE_MODEL_KEY is set.
/// </summary>
public sealed class ModelGatewayTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private static IOrchestrator RulesThen(IModelClient model) => new CompositeOrchestrator(new RuleBasedOrchestrator(), new ModelOrchestrator(model));
    private static void ModelOn(RelaySettings s) { s.Orchestrator.Mode = OrchestratorSettings.RulesAndModel; s.Model.Enabled = true; }

    [Fact]
    public void ModelSearchesThenAnswersAndOnlyRealSourcesBecomeCitations()
    {
        var model = new ScriptedModelClient();
        using var s = Scenario.New(_tmp, ModelOn, RulesThen(model)).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Note("We decided the Atlas beta ships on October 14.");
        var noteId = s.H.Records().Last(r => r.Type == EventTypes.NoteRouted).DataString("noteId")!;

        model.Reply(ScriptedModelClient.Tool("search", ("query", "Atlas beta ships")))
             .Reply(ScriptedModelClient.Final("Answered from one decision note", "The Atlas beta ships on October 14.", [noteId, "made-up-id"]));

        s.Command("give me the current thinking on when atlas ships")
         .ExpectState(RelayState.Completed)
         .ExpectOutcome("answered")
         .ExpectAnswerContains("October 14")
         .ExpectEvent(EventTypes.ModelRequested, atLeast: 2)
         .ExpectEvent(EventTypes.ModelResponded, atLeast: 2)
         .ExpectEvent(EventTypes.ToolCalled);

        var response = s.Snap.Response!;
        Assert.StartsWith("model:", response.Producer);
        var citation = Assert.Single(response.Citations);
        Assert.Equal(noteId, citation.Id);
        Assert.Equal("atlas", citation.ProjectSlug);
        Assert.Contains(response.Steps, step => step.Contains("made-up-id") && step.Contains("Dropped"));

        // The prompt Relay assembled: the contract, the tools, the actions, and the instruction; the tool result went back as a user message.
        var first = model.Requests[0];
        Assert.Equal("system", first.Messages[0].Role);
        Assert.Contains("\"type\":\"tool\"", first.Messages[0].Content);
        Assert.Contains("list_projects", first.Messages[0].Content);
        Assert.Contains("create_project", first.Messages[0].Content);
        Assert.Contains("give me the current thinking", first.Messages[1].Content);
        var second = model.Requests[1];
        Assert.Equal(4, second.Messages.Count);
        Assert.StartsWith("TOOL RESULT for search: ok", second.Messages[3].Content);
        Assert.Contains(noteId, second.Messages[3].Content);
        Assert.True(second.JsonObject);

        // The ledger shows the model round trips with sizes, never the prompt text.
        var requested = s.H.Records().Where(r => r.Type == EventTypes.ModelRequested).ToList();
        Assert.Equal(2, requested.Count);
        Assert.Equal("model.test", requested[0].DataString("host"));
        Assert.True(requested[1].DataInt64("promptChars") > requested[0].DataInt64("promptChars"));
        Assert.DoesNotContain("current thinking", JsonSerializer.Serialize(requested.Select(r => r.Data)));
    }

    [Fact]
    public void ModelProposalsAreDecidedByPolicyAndWaitForTheUser()
    {
        var model = new ScriptedModelClient()
            .Reply(ScriptedModelClient.Final("Create the project the user described", "I can create Borealis for you.", null,
                ScriptedModelClient.Proposal("create_project", "The user wants a home for the new idea.", ("name", "Borealis"))));

        using var s = Scenario.New(_tmp, ModelOn, RulesThen(model)).WithWorkspace()
            .Command("hey I'd like a fresh home for my borealis idea please")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.CreateProject, "pending");

        var received = s.H.Last(EventTypes.ProposalReceived)!;
        Assert.Equal("model:test-model", received.DataString("proposedBy"));
        Assert.True(received.DataBool("requiresApproval"));

        s.Approve().ExpectState(RelayState.Completed).ExpectOutcome("executed").ExpectProject("borealis");
        var proposal = s.Snap.Response!.Proposals.Single();
        Assert.Equal("model:test-model", proposal.ProposedBy);
    }

    [Fact]
    public void ModelProposingAProhibitedActionIsDeniedOnTheRecord()
    {
        var model = new ScriptedModelClient()
            .Reply(ScriptedModelClient.Final("Clean up with a shell command", null, null,
                ScriptedModelClient.Proposal("run_shell", "Fastest way to tidy the folder.", ("command", "del /s /q *.tmp"))));

        using var s = Scenario.New(_tmp, ModelOn, RulesThen(model)).WithWorkspace()
            .Command("just tidy up the temp junk somehow")
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("denied")
            .ExpectProposal("run_shell", "denied");

        var decided = s.H.Last(EventTypes.ProposalDecided)!;
        Assert.Equal("Deny", decided.DataString("outcome"));
        Assert.Equal("Prohibited", decided.DataString("tier"));
        Assert.DoesNotContain(s.H.Records(), r => r.Type == EventTypes.ApprovalGranted);
    }

    [Fact]
    public void ModelProposingDeletionWaitsForApprovalLikeAnyOtherWrite()
    {
        var model = new ScriptedModelClient();
        using var s = Scenario.New(_tmp, ModelOn, RulesThen(model)).WithWorkspace()
            .Command("create project Atlas").Approve();
        var projectId = s.H.Registry.FindActive("atlas")!.Id;
        model.Reply(ScriptedModelClient.Final("Delete Atlas as asked", null, null,
            ScriptedModelClient.Proposal("delete_project", "The user asked for Atlas to be gone for good.", ("projectId", projectId), ("confirm", "delete"))));

        s.Command("I want atlas gone for good, wipe it out")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.DeleteProject, "pending")
            .ExpectProject("atlas");
        Assert.Equal("RequiresApproval", s.H.Last(EventTypes.ProposalDecided)!.DataString("tier"));
        s.Reject().ExpectState(RelayState.Completed).ExpectProject("atlas");
    }

    [Fact]
    public void MalformedModelOutputEndsTheTurnVisiblyWithoutExecutingAnything()
    {
        var model = new ScriptedModelClient().Reply("Sure! I'll create that project for you right away.");

        using var s = Scenario.New(_tmp, ModelOn, RulesThen(model)).WithWorkspace()
            .Command("do the thing with the stuff")
            .ExpectState(RelayState.Completed)
            .ExpectNoProposals()
            .ExpectAnswerContains("JSON contract");

        var plan = s.H.Last(EventTypes.TaskPlanned)!;
        Assert.False(plan.DataBool("understood"));
        Assert.Contains("Sure!", plan.DataString("raw"));
        Assert.DoesNotContain(s.H.Records(), r => r.Type == EventTypes.ProposalReceived);
    }

    [Fact]
    public void ModelToolBudgetIsEnforced()
    {
        var model = new ScriptedModelClient().Always(_ => ScriptedModelClient.Tool("list_projects").ToJsonString());

        using var s = Scenario.New(_tmp, s => { ModelOn(s); s.Orchestrator.MaxToolCalls = 3; }, RulesThen(model)).WithWorkspace()
            .Command("keep looking forever")
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("tool budget");

        Assert.Equal(4, model.Requests.Count); // three tool calls answered, the fourth tool request is refused
        Assert.Equal(3, s.H.Count(EventTypes.ToolCalled));
    }

    [Fact]
    public void ModelFailureIsReportedAndRulesKeepWorking()
    {
        var model = new ScriptedModelClient().Fail("HTTP 429 from api.example: rate limited", 429);

        using var s = Scenario.New(_tmp, ModelOn, RulesThen(model)).WithWorkspace()
            .Command("something the grammar cannot parse at all")
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("Model unavailable")
            .ExpectAnswerContains("429");

        var responded = s.H.Last(EventTypes.ModelResponded)!;
        Assert.False(responded.DataBool("ok"));
        Assert.Contains("429", responded.DataString("error"));

        s.Command("list projects").ExpectState(RelayState.Completed).ExpectOutcome("answered");
        Assert.StartsWith("rules", s.Snap.Response!.Producer);
        Assert.Single(model.Requests); // the grammar handled it; the model was not asked
    }

    [Fact]
    public void SystemPromptStatesTheContractToolsActionsAndRules()
    {
        var prompt = ModelOrchestrator.SystemPrompt();
        foreach (var tool in ToolBroker.Descriptors) Assert.Contains(tool.Name, prompt);
        foreach (var action in new[] { Actions.CreateProject, Actions.ArchiveProject, Actions.RouteNote, Actions.LaunchWorker, Actions.ApplyPatch, Actions.ExportBackup, Actions.DeleteProject, Actions.MoveNote, Actions.ModelRequest, Actions.UpdatePreference, Actions.UpdatePrompt })
            Assert.Contains(action, prompt);
        Assert.Contains("never invent ids", prompt);
        Assert.Contains("only when the user explicitly asked to delete", prompt);
        Assert.Contains("exactly one JSON object", prompt);
        Assert.Contains("knowledge", prompt);
        Assert.Contains("depends_on", prompt);
        Assert.DoesNotContain(Actions.RunShell, prompt);

        // The compiled preference and the user's approved prompt fragment ride along; external profiles are named.
        var prefs = Relay.Core.Preferences.PreferenceCompiler.Compile(new Relay.Core.Preferences.UserPreferences { Response = { Verbosity = Relay.Core.Preferences.ResponsePreferences.Minimalist } });
        using var h = new Harness(_tmp.Root).Start();
        var context = new TurnContext
        {
            Tools = null!, Sink = null!, Registry = h.Registry, Roots = h.Roots, Drafts = h.Notes, Settings = h.SettingsLoad.Settings.Orchestrator,
            Preferences = prefs, ExternalProfiles = ["research"], PromptFragment = "Never use bullet lists.",
        };
        var withContext = ModelOrchestrator.SystemPrompt(context);
        Assert.Contains("one short sentence", withContext);
        Assert.Contains("profiles: research", withContext);
        Assert.Contains("Never use bullet lists.", withContext);
    }

    [Fact]
    public void StoringTheApiKeyNeverPutsItInTheLedgerOrSettings()
    {
        using var h = new Harness(_tmp.Root, configure: ModelOn).Start();
        Assert.False(h.Snap.ModelKeyStored);
        Assert.True(h.Coordinator.SetModelApiKey("sk-super-secret-value-123"));
        Assert.True(h.Snap.ModelKeyStored);
        Assert.Equal("sk-super-secret-value-123", h.Secrets.Get("model-gateway"));

        var changed = h.Last(EventTypes.SecretChanged)!;
        Assert.Equal("model-gateway", changed.DataString("name"));
        Assert.Equal("stored", changed.DataString("action"));
        Assert.DoesNotContain("super-secret", JsonSerializer.Serialize(h.Records().Select(r => r.Data)));
        Assert.DoesNotContain("super-secret", File.ReadAllText(_tmp.Root.SettingsPath));

        Assert.True(h.Coordinator.SetModelApiKey(null));
        Assert.False(h.Snap.ModelKeyStored);
        Assert.Equal("removed", h.Last(EventTypes.SecretChanged)!.DataString("action"));
    }

    [Fact]
    public void DpapiSecretStoreRoundTripsAndStoresOnlyCiphertext()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new Relay.Windows.DpapiSecretStore(_tmp.Root);
        Assert.False(store.Exists("k"));
        store.Set("k", "sk-plaintext-value");
        Assert.True(store.Exists("k"));
        Assert.Equal("sk-plaintext-value", store.Get("k"));
        var file = Path.Combine(_tmp.Root.SecretsDirectory, "k.bin");
        Assert.True(File.Exists(file));
        Assert.DoesNotContain("sk-plaintext-value", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        Assert.True(store.Remove("k"));
        Assert.Null(store.Get("k"));
        Assert.Throws<ArgumentException>(() => store.Get("../escape"));
    }

    // ----------------------------------------------------------------------------------------
    // HTTP client against a fake handler
    // ----------------------------------------------------------------------------------------

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return Respond(request);
        }
    }

    private static ModelSettings Settings(string endpoint = "https://api.example.test/v1/chat/completions") => new() { Enabled = true, Endpoint = endpoint, Model = "m-1", SecretName = "k", TimeoutMs = 5000 };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ClientSendsTheBearerKeyAndTheExactRequestAndParsesTheReply()
    {
        var secrets = new MemorySecretStore();
        secrets.Set("k", "sk-test");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"{\"type\":\"final\"}"}}],"usage":{"prompt_tokens":12,"completion_tokens":3}}""") };
        using var client = new OpenAiCompatibleClient(Settings(), secrets, handler);

        var response = await client.CompleteAsync(new ModelRequest("m-1", [new("system", "S"), new("user", "U")], 500, true), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("{\"type\":\"final\"}", response.Content);
        Assert.Equal(12, response.PromptTokens);
        Assert.Equal(3, response.CompletionTokens);
        Assert.Equal("api.example.test", client.Host);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", request.Headers.Authorization.Parameter);
        Assert.Equal("https://api.example.test/v1/chat/completions", request.RequestUri!.ToString());
        var body = JsonNode.Parse(handler.Bodies[0])!.AsObject();
        Assert.Equal("m-1", body["model"]!.GetValue<string>());
        Assert.Equal(2, body["messages"]!.AsArray().Count);
        Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal(500, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(new[] { "model", "messages", "max_tokens", "temperature", "response_format" }, body.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public async Task ClientRefusesToSendWithoutAKeyAndReportsHttpErrorsWithoutRetrying()
    {
        var secrets = new MemorySecretStore();
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit reached"}}""") };
        using var client = new OpenAiCompatibleClient(Settings(), secrets, handler);
        var request = new ModelRequest("m-1", [new("user", "U")], 100, true);

        var noKey = await client.CompleteAsync(request, CancellationToken.None);
        Assert.False(noKey.Ok);
        Assert.Contains("No API key", noKey.Error);
        Assert.Empty(handler.Requests);

        secrets.Set("k", "sk");
        var limited = await client.CompleteAsync(request, CancellationToken.None);
        Assert.False(limited.Ok);
        Assert.Equal(429, limited.HttpStatus);
        Assert.Contains("Rate limit reached", limited.Error);
        Assert.Single(handler.Requests);

        handler.Respond = _ => Json(HttpStatusCode.OK, """{"choices":[]}""");
        var empty = await client.CompleteAsync(request, CancellationToken.None);
        Assert.False(empty.Ok);
        Assert.Contains("no choices", empty.Error);
    }

    [Fact]
    public void ClientOnlyAcceptsHttpsEndpoints()
    {
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleClient(Settings("http://api.example.test/v1"), new MemorySecretStore()));
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleClient(Settings("not a url"), new MemorySecretStore()));
    }

    /// <summary>Runs only when RELAY_LIVE_MODEL_KEY is set (optionally RELAY_LIVE_MODEL_ENDPOINT / RELAY_LIVE_MODEL). Never runs in CI.</summary>
    [Fact]
    public void LiveModelAnswersARecallQuestionWithACitation()
    {
        var key = Environment.GetEnvironmentVariable("RELAY_LIVE_MODEL_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;
        var settings = new ModelSettings
        {
            Enabled = true,
            Endpoint = Environment.GetEnvironmentVariable("RELAY_LIVE_MODEL_ENDPOINT") ?? "https://api.openai.com/v1/chat/completions",
            Model = Environment.GetEnvironmentVariable("RELAY_LIVE_MODEL") ?? "gpt-4o-mini",
            SecretName = "live",
            TimeoutMs = 60_000,
        };
        var secrets = new MemorySecretStore();
        secrets.Set("live", key);
        using var client = new OpenAiCompatibleClient(settings, secrets);

        using var s = Scenario.New(_tmp, cfg => { ModelOn(cfg); cfg.Model.Endpoint = settings.Endpoint; cfg.Model.Model = settings.Model; }, RulesThen(client)).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Note("We decided the Atlas beta ships on October 14.")
            .Command("give me the current thinking on when the atlas beta actually ships")
            .ExpectState(RelayState.Completed)
            .ExpectEvent(EventTypes.ModelRequested);
        Assert.StartsWith("model:", s.Snap.Response!.Producer);
        Assert.Contains("October 14", s.Snap.Response.Answer ?? "");
        Assert.NotEmpty(s.Snap.Response.Citations);
    }
}
