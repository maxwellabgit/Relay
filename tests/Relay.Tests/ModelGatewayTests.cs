using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Model;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Gateway;
using Relay.Tests.Support;

namespace Relay.Tests;

/// <summary>
/// The gateway between Relay and a model: the OpenAI-compatible client (the bearer key, the exact request,
/// token accounting, HTTP errors, streaming) tested against a fake handler, the API key that never reaches
/// the ledger or settings, and the mind riding on that client — the JSON a host answers with becomes the
/// next move, and a host that fails ends the task in the open. A live test runs only when RELAY_LIVE_MODEL_KEY is set.
/// </summary>
public sealed class ModelGatewayTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private static void MindMode(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    // ----------------------------------------------------------------------------------------
    // The mind on the gateway
    // ----------------------------------------------------------------------------------------

    /// <summary>One step in the mind's contract, as the host on the other end of the gateway answers it.</summary>
    private static string Step(string feed, JsonObject move) => new JsonObject
    {
        ["read"] = new JsonObject
        {
            ["intent"] = feed,
            ["complexity"] = 0.2,
            ["needs"] = new JsonArray("local_notes"),
            ["significance"] = 0,
            ["sensitivity"] = 0,
            ["risk"] = new JsonObject { ["core"] = 0, ["security"] = 0, ["loop"] = 0, ["destructive"] = 0 },
        },
        ["move"] = move,
        ["feed"] = feed,
    }.ToJsonString();

    private static JsonObject Tool(string name, params (string Key, string Value)[] args)
    {
        var a = new JsonObject();
        foreach (var (key, value) in args) a[key] = value;
        return new JsonObject { ["type"] = "use_tool", ["text"] = "", ["name"] = name, ["args"] = a, ["done"] = false };
    }

    private static JsonObject Say(string text)
        => new() { ["type"] = "say", ["text"] = text, ["name"] = "", ["args"] = new JsonObject(), ["done"] = true };

    [Fact]
    public void TheMindRunsOnTheGatewayAndTheLedgerRecordsSizesNotWords()
    {
        var model = new ScriptedModelClient()
            .Reply(Step("Looking at your projects.", Tool("list_projects")))
            .Reply(Step("Answering from the list.", Say("You have one project, Atlas.")));

        using var s = Scenario.New(_tmp, MindMode, mind: new ModelMind(model)).WithWorkspace()
            .Project("Atlas")
            .Command("which projects do I have?")
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("answered")
            .ExpectAnswerContains("Atlas")
            .ExpectEvent(EventTypes.ToolCalled)
            .ExpectEvent(EventTypes.MindStepped, 2)
            .ExpectEvent(EventTypes.LoopEnded);
        Assert.Equal(ModelMind.NamePrefix + model.Model, s.Response.Producer);

        // The prompt Relay assembles: the contract, the tools and the actions as the system message, the task's
        // transcript as the user message, and the move schema as the response format the host constrains against.
        var first = model.Requests[0];
        Assert.Equal(2, first.Messages.Count);
        Assert.Equal("system", first.Messages[0].Role);
        Assert.Contains("exactly one JSON object", first.Messages[0].Content);
        Assert.Contains("list_projects", first.Messages[0].Content);
        Assert.Contains("create_project", first.Messages[0].Content);
        Assert.Contains("which projects do I have?", first.Messages[1].Content);
        Assert.True(first.JsonObject);
        Assert.Equal(MoveSchema.Json, first.JsonSchema);
        Assert.Equal(MoveSchema.SchemaName, first.SchemaName);

        // The second step saw the move it made and what the tool returned: the loop is carried in the prompt, not by the host.
        var second = model.Requests[1];
        Assert.Equal(2, second.Messages.Count);
        Assert.Contains("use_tool list_projects", second.Messages[1].Content);
        Assert.Contains("tool list_projects", second.Messages[1].Content);

        // The ledger shows the round trips with their sizes, never the words that were sent.
        var requested = s.H.Records().Where(r => r.Type == EventTypes.ModelRequested).ToList();
        Assert.Equal(2, requested.Count);
        Assert.Equal("mind", requested[0].DataString("host"));
        Assert.Equal(ModelMind.NamePrefix + model.Model, requested[0].DataString("model"));
        Assert.True(requested[1].DataInt64("promptChars") > requested[0].DataInt64("promptChars"));
        Assert.DoesNotContain("which projects", JsonSerializer.Serialize(requested.Select(r => r.Data)));
    }

    [Fact]
    public void AHostThatFailsIsRetriedOnceAndThenEndsTheTaskSayingWhatTheHostSaid()
    {
        var model = new ScriptedModelClient()
            .Fail("HTTP 429 from api.example: rate limited", 429)
            .Fail("HTTP 429 from api.example: rate limited", 429);

        using var s = Scenario.New(_tmp, MindMode, mind: new ModelMind(model)).WithWorkspace()
            .Command("what did we decide about the beta?")
            .ExpectState(RelayState.Failed)
            .ExpectEvent(EventTypes.MindFailed, 2)
            .ExpectEvent(EventTypes.TaskFailed)
            .ExpectNoEvent(EventTypes.ProposalReceived);
        Assert.Equal(2, model.Requests.Count);                                          // one retry for an unreachable host, then it gives up

        var responded = s.H.Last(EventTypes.ModelResponded)!;
        Assert.False(responded.DataBool("ok"));
        Assert.Contains("429", responded.DataString("error"));
        Assert.Equal("mind_failed", s.H.Last(EventTypes.LoopEnded)!.DataString("outcome"));
        Assert.Contains("Model unavailable", s.H.Last(EventTypes.TaskFailed)!.DataString("error"));
        Assert.Contains("429", s.Snap.Incident!.Detail);                                 // the user is told what the host said, not "something went wrong"
    }

    // ----------------------------------------------------------------------------------------
    // The API key
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void StoringTheApiKeyNeverPutsItInTheLedgerOrSettings()
    {
        using var h = new Harness(_tmp.Root, configure: MindMode).Start();
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

    private static HttpResponseMessage Sse(params string[] events)
    {
        var text = string.Join("", events.Select(e => e.Length == 0 ? "\n" : e.StartsWith(':') ? e + "\n" : "data: " + e + "\n\n"));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, System.Text.Encoding.UTF8, "text/event-stream") };
    }

    [Fact]
    public async Task StreamingDeliversEachDeltaAsItArrivesAndAssemblesTheSameReply()
    {
        var secrets = new MemorySecretStore();
        secrets.Set("k", "sk");
        var handler = new FakeHandler
        {
            Respond = _ => Sse(
                """{"choices":[{"delta":{"role":"assistant","content":""},"finish_reason":null}]}""",
                """{"choices":[{"delta":{"content":"Nepal "},"finish_reason":null}]}""",
                ": keep-alive",
                """{"choices":[{"delta":{"content":"uses UTC+05:45."},"finish_reason":"stop"}]}""",
                """{"choices":[],"usage":{"prompt_tokens":40,"completion_tokens":9}}""",
                "[DONE]"),
        };
        using var client = new OpenAiCompatibleClient(Settings(), secrets, handler);
        var deltas = new List<string>();

        var response = await client.StreamAsync(new ModelRequest("m-1", [new("user", "U")], 200, false), (d, _) => { deltas.Add(d); return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("Nepal uses UTC+05:45.", response.Content);
        Assert.Equal(["Nepal ", "uses UTC+05:45."], deltas);
        Assert.Equal(40, response.PromptTokens);
        Assert.Equal(9, response.CompletionTokens);
        var body = JsonNode.Parse(handler.Bodies[0])!.AsObject();
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
        Assert.Equal("m-1", body["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task StreamingHandlesHostsThatAnswerWholeErrorsAndEmptyStreams()
    {
        var secrets = new MemorySecretStore();
        secrets.Set("k", "sk");
        var request = new ModelRequest("m-1", [new("user", "U")], 200, false);

        // A host that ignores stream=true: the whole reply is one delta and the result is the same as CompleteAsync.
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"Whole reply."}}],"usage":{"prompt_tokens":5,"completion_tokens":2}}""") };
        using var whole = new OpenAiCompatibleClient(Settings(), secrets, handler);
        var deltas = new List<string>();
        var response = await whole.StreamAsync(request, (d, _) => { deltas.Add(d); return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(response.Ok);
        Assert.Equal(["Whole reply."], deltas);
        Assert.Equal(5, response.PromptTokens);

        // An HTTP error before any event.
        handler.Respond = _ => Json(HttpStatusCode.BadGateway, """{"error":{"message":"upstream down"}}""");
        var failed = await whole.StreamAsync(request, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.False(failed.Ok);
        Assert.Equal(502, failed.HttpStatus);
        Assert.Contains("upstream down", failed.Error);

        // An error event mid-stream.
        handler.Respond = _ => Sse("""{"choices":[{"delta":{"content":"Par"}}]}""", """{"error":{"message":"context length exceeded"}}""");
        var mid = await whole.StreamAsync(request, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.False(mid.Ok);
        Assert.Contains("context length exceeded", mid.Error);

        // A stream that closes without content or [DONE].
        handler.Respond = _ => Sse("""{"choices":[{"delta":{}}]}""");
        var empty = await whole.StreamAsync(request, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.False(empty.Ok);
        Assert.Contains("without content", empty.Error);

        // The default streaming implementation of any client: complete, then one delta.
        var scripted = new ScriptedModelClient().Reply("scripted");
        var seen = new List<string>();
        var viaDefault = await ((IModelClient)scripted).StreamAsync(request, (d, _) => { seen.Add(d); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("scripted", viaDefault.Content);
        Assert.Equal(["scripted"], seen);
    }

    /// <summary>
    /// Runs only when RELAY_LIVE_MODEL_KEY is set (optionally RELAY_LIVE_MODEL_ENDPOINT / RELAY_LIVE_MODEL): the real
    /// client, the real model as the mind, and a question answered from a note this test filed. Never runs in CI.
    /// </summary>
    [Fact]
    public void LiveModelRecallsAFiledDecisionThroughTheGateway()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        using var s = Scenario.New(_tmp, cfg => { live.Configure(cfg); MindMode(cfg); }, inlinePost: false, mind: new ModelMind(client)).WithWorkspace()
            .Project("Atlas")
            .Note("We decided the Atlas beta ships on October 14.");

        s.Ask("what is the current thinking on when the atlas beta ships?")
         .PumpUntil("the mind to answer", () => s.Snap.State is RelayState.Completed or RelayState.Failed, TimeSpan.FromSeconds(180))
         .ExpectState(RelayState.Completed)
         .ExpectEvent(EventTypes.ModelRequested);
        Assert.Equal(ModelMind.NamePrefix + live.Model, s.Response.Producer);
        Assert.Contains("October 14", s.Response.Answer ?? "");
        Assert.NotEmpty(s.Response.ToolCalls);                                            // the date came from the note, not from the model's own memory
    }
}
