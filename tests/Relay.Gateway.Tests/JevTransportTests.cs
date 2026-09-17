using System.Net;
using System.Text;
using System.Text.Json;
using Relay.Core.Judgments;
using Relay.Core.Model;
using Relay.Core.Time;
using Relay.Gateway;

namespace Relay.Gateway.Tests;

/// <summary>§6 Jev transport — fixture/replay/HttpHandler tests. No API key required.</summary>
public class JevTransportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);

    private static JudgmentRequest MixedRequest(string model = JudgmentDefaults.ModelAlias)
    {
        var state = JsonSerializer.SerializeToElement(new { message = "Charged twice. Refund ASAP." });
        return new JudgmentRequest
        {
            Model = model,
            State = state,
            Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
            {
                ["urgent"] = new JudgmentQuestion
                {
                    Type = JudgmentQuestionTypes.Noul,
                    Instructions = "Does the message convey urgency?",
                },
                ["department"] = new JudgmentQuestion
                {
                    Type = JudgmentQuestionTypes.Choice,
                    Instructions = "Which team should handle this?",
                    Criteria = JsonSerializer.SerializeToElement(new Dictionary<string, string>
                    {
                        ["billing"] = "Payment or subscription issues",
                        ["technical"] = "Bugs or integration problems",
                        ["other"] = "Anything else",
                    }),
                },
                ["frustration"] = new JudgmentQuestion
                {
                    Type = JudgmentQuestionTypes.Score,
                    Instructions = "How frustrated does the speaker appear?",
                    Criteria = JsonSerializer.SerializeToElement(new[]
                    {
                        "Calm and neutral",
                        "Concerned but civil",
                        "Very angry or strong language",
                    }),
                },
            },
        };
    }

    private static string ValidMixedResponse(string model = "jev-2026-09-15") =>
        """
        {
          "model": "MODEL",
          "answers": {
            "urgent": { "noul": 0.91 },
            "department": {
              "choice": "billing",
              "probabilities": { "billing": 0.70, "technical": 0.20, "other": 0.10 },
              "confidence": 0.55
            },
            "frustration": {
              "score": 1.25,
              "legend": { "0": "Calm and neutral", "1": "Concerned but civil", "2": "Very angry or strong language" },
              "probabilities": { "0": 0.10, "1": 0.55, "2": 0.35 },
              "confidence": 0.62
            }
          },
          "usage": { "input_tokens": 128, "output_tokens": 0 }
        }
        """.Replace("MODEL", model, StringComparison.Ordinal);

    [Fact]
    public async Task Mixed_question_types_and_fractional_score()
    {
        var request = MixedRequest();
        var fixture = new FixtureJudgmentClient();
        fixture.Register(request, ValidMixedResponse());

        var response = await fixture.JudgeAsync(request);
        Assert.True(response.Ok, response.Error);
        Assert.Equal("jev-2026-09-15", response.Model);
        Assert.Equal(0.91, response.TryGetNoul("urgent")!.Noul, 3);
        Assert.Equal("billing", response.TryGetChoice("department")!.Choice);
        Assert.Equal(1.25, response.TryGetScore("frustration")!.Score, 3);
        Assert.NotNull(response.Usage);
    }

    [Fact]
    public void Malformed_answers_provider_contract_error()
    {
        var request = MixedRequest();
        var bad = """
            { "model": "jev-x", "answers": { "urgent": { "noul": "nope" } }, "usage": { "input_tokens": 1, "output_tokens": 0 } }
            """;
        var result = JudgmentResponseValidator.Validate(request, bad);
        Assert.False(result.Ok);
        Assert.Equal(JudgmentErrorCodes.ProviderContractError, result.ErrorCode);
    }

    [Fact]
    public void Missing_keys_provider_contract_error()
    {
        var request = MixedRequest();
        var missing = """
            {
              "model": "jev-x",
              "answers": { "urgent": { "noul": 0.5 } },
              "usage": { "input_tokens": 1, "output_tokens": 0 }
            }
            """;
        var result = JudgmentResponseValidator.Validate(request, missing);
        Assert.False(result.Ok);
        Assert.Equal(JudgmentErrorCodes.ProviderContractError, result.ErrorCode);
        Assert.Contains("keys", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Noul_rejects_confidence_property()
    {
        var request = new JudgmentRequest
        {
            State = JsonSerializer.SerializeToElement("x"),
            Questions = new Dictionary<string, JudgmentQuestion>
            {
                ["q"] = new() { Type = JudgmentQuestionTypes.Noul, Instructions = "yes?" },
            },
        };
        var body = """
            { "model": "jev-x", "answers": { "q": { "noul": 0.4, "confidence": 0.9 } }, "usage": { "input_tokens": 1, "output_tokens": 0 } }
            """;
        var result = JudgmentResponseValidator.Validate(request, body);
        Assert.False(result.Ok);
        Assert.Contains("confidence", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Choice_outside_options_rejected()
    {
        var request = MixedRequest();
        var body = ValidMixedResponse().Replace("\"billing\"", "\"finance\"", StringComparison.Ordinal);
        // Only replace the choice value carefully — rebuild instead:
        body = """
            {
              "model": "jev-x",
              "answers": {
                "urgent": { "noul": 0.5 },
                "department": {
                  "choice": "finance",
                  "probabilities": { "billing": 0.7, "technical": 0.2, "other": 0.1 },
                  "confidence": 0.5
                },
                "frustration": {
                  "score": 1.0,
                  "probabilities": { "0": 0.2, "1": 0.6, "2": 0.2 },
                  "confidence": 0.5
                }
              },
              "usage": { "input_tokens": 1, "output_tokens": 0 }
            }
            """;
        var result = JudgmentResponseValidator.Validate(request, body);
        Assert.False(result.Ok);
        Assert.Contains("not in the option set", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Distribution_sum_tolerance_enforced()
    {
        var request = MixedRequest();
        var body = """
            {
              "model": "jev-x",
              "answers": {
                "urgent": { "noul": 0.5 },
                "department": {
                  "choice": "billing",
                  "probabilities": { "billing": 0.9, "technical": 0.9, "other": 0.9 },
                  "confidence": 0.5
                },
                "frustration": {
                  "score": 1.0,
                  "probabilities": { "0": 0.2, "1": 0.6, "2": 0.2 },
                  "confidence": 0.5
                }
              },
              "usage": { "input_tokens": 1, "output_tokens": 0 }
            }
            """;
        var result = JudgmentResponseValidator.Validate(request, body);
        Assert.False(result.Ok);
        Assert.Contains("sum", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fixture_unknown_request_fails_loudly()
    {
        var fixture = new FixtureJudgmentClient();
        var request = MixedRequest();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.JudgeAsync(request));
        Assert.Contains("fail loudly", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Atlas", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lightshift", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replay_client_replays_recorded_pair()
    {
        var request = MixedRequest();
        var replay = new ReplayJudgmentClient();
        replay.AddRecording(request, ValidMixedResponse("jev-replay"));
        var response = await replay.JudgeAsync(request);
        Assert.True(response.Ok);
        Assert.Equal("jev-replay", response.Model);
    }

    [Fact]
    public async Task Auth_failure_no_auto_retry()
    {
        var handler = new ScriptedHandler(req =>
        {
            Assert.NotNull(req.Headers.Authorization);
            return JsonResponse(401, """{ "error": "unauthorized" }""");
        });
        var secrets = new MemorySecretStore();
        secrets.Set(JudgmentDefaults.ApiKeySecretName, "sk-test-key-not-for-logs");
        using var http = new HttpClient(handler) { BaseAddress = new Uri(JudgmentDefaults.Endpoint) };
        var client = new TypeSafeJevClient(secrets, http);
        var clock = new FixedClock(T0);
        var delays = 0;
        var dispatcher = new JudgmentDispatcher(client, new JudgmentTransportPolicy(clock), clock,
            delay: (_, _) => { delays++; return Task.CompletedTask; });

        var response = await dispatcher.DispatchAsync(MixedRequest());
        Assert.False(response.Ok);
        Assert.Equal(JudgmentErrorCodes.AuthBlocked, response.ErrorCode);
        Assert.Equal(0, delays);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Throttling_schedules_retries_with_backoff()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(429, """{ "error": "rate limit" }"""));
        var secrets = new MemorySecretStore();
        secrets.Set(JudgmentDefaults.ApiKeySecretName, "sk-test");
        using var http = new HttpClient(handler) { BaseAddress = new Uri(JudgmentDefaults.Endpoint) };
        var client = new TypeSafeJevClient(secrets, http);
        var clock = new FixedClock(T0);
        var delaySpans = new List<TimeSpan>();
        var dispatcher = new JudgmentDispatcher(client, new JudgmentTransportPolicy(clock), clock,
            delay: (d, _) => { delaySpans.Add(d); clock.Advance(d); return Task.CompletedTask; });

        var response = await dispatcher.DispatchAsync(MixedRequest(), maxAttempts: 3);
        Assert.False(response.Ok);
        Assert.Equal(JudgmentErrorCodes.Throttled, response.ErrorCode);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(2, delaySpans.Count);
        Assert.True(delaySpans[0] >= TimeSpan.FromSeconds(1));
        Assert.True(delaySpans[1] >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Overload_529_is_retryable()
    {
        var calls = 0;
        var handler = new ScriptedHandler(_ =>
        {
            calls++;
            if (calls < 2) return JsonResponse(529, """{ "error": "overloaded" }""");
            return JsonResponse(200, ValidMixedResponse());
        });
        var secrets = new MemorySecretStore();
        secrets.Set(JudgmentDefaults.ApiKeySecretName, "sk-test");
        using var http = new HttpClient(handler) { BaseAddress = new Uri(JudgmentDefaults.Endpoint) };
        var client = new TypeSafeJevClient(secrets, http);
        var clock = new FixedClock(T0);
        var dispatcher = new JudgmentDispatcher(client, new JudgmentTransportPolicy(clock), clock,
            delay: (d, _) => { clock.Advance(d); return Task.CompletedTask; });

        var response = await dispatcher.DispatchAsync(MixedRequest());
        Assert.True(response.Ok, response.Error);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancellation_stops_dispatch()
    {
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return JsonResponse(200, ValidMixedResponse());
        });
        var secrets = new MemorySecretStore();
        secrets.Set(JudgmentDefaults.ApiKeySecretName, "sk-test");
        using var http = new HttpClient(handler) { BaseAddress = new Uri(JudgmentDefaults.Endpoint), Timeout = TimeSpan.FromSeconds(30) };
        var client = new TypeSafeJevClient(secrets, http);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var response = await client.JudgeAsync(MixedRequest(), cts.Token);
        Assert.False(response.Ok);
        Assert.Equal(JudgmentErrorCodes.Cancelled, response.ErrorCode);
    }

    [Fact]
    public async Task Restart_during_backoff_resumes_from_persisted_state()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "relay-jev-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var root = new Relay.Core.Storage.DataRoot(tmp);
            var clock = new FixedClock(T0);
            root.EnsureLayout(clock);
            var policy = new JudgmentTransportPolicy(clock, root);
            var hash = FixtureJudgmentClient.CanonicalRequestHash(MixedRequest());
            var state = policy.Begin(hash);
            state.Status = JudgmentRetryStatus.BackingOff;
            state.NextAttemptAt = clock.UtcNow.AddSeconds(2);
            state.Attempt = 1;
            policy.SaveRetry(state);

            clock.Advance(TimeSpan.FromSeconds(1));
            var loaded = policy.TryLoad(state.RetryId);
            Assert.NotNull(loaded);
            Assert.Equal(JudgmentRetryStatus.BackingOff, loaded!.Status);
            Assert.True(loaded.NextAttemptAt > clock.UtcNow);

            // After wait window, resume proceeds.
            clock.Advance(TimeSpan.FromSeconds(2));
            var fixture = new FixtureJudgmentClient();
            fixture.Register(MixedRequest(), ValidMixedResponse());
            var dispatcher = new JudgmentDispatcher(fixture, policy, clock,
                delay: (d, _) => { clock.Advance(d); return Task.CompletedTask; });
            var response = await dispatcher.ResumeAsync(state.RetryId, MixedRequest());
            Assert.True(response.Ok, response.Error);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Key_redaction_strips_bearer_and_sk()
    {
        var raw = "Authorization: Bearer sk-abcDEF123 and token Bearer xyz";
        var redacted = TypeSafeJevClient.Redact(raw);
        Assert.DoesNotContain("sk-abcDEF123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer sk-", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_preflight_without_key_fails()
    {
        var secrets = new MemorySecretStore();
        using var client = new TypeSafeJevClient(secrets);
        var ex = Assert.Throws<InvalidOperationException>(() => client.EnsureLivePreflight());
        Assert.Contains("preflight", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decision_catalog_loads_v1_stubs()
    {
        var catalog = DecisionCatalog.LoadDefault(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory)));
        Assert.True(catalog.Count >= 1, "expected decisions/v1 stubs copied to test output");
        var def = catalog.TryGet("transport.smoke.mixed");
        Assert.NotNull(def);
        Assert.Equal(3, def!.Questions.Count);
    }

    [Fact]
    public async Task Http_client_sends_model_state_questions_bearer()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var handler = new ScriptedHandler(async req =>
        {
            seen = req;
            body = await req.Content!.ReadAsStringAsync();
            return JsonResponse(200, ValidMixedResponse());
        });
        var secrets = new MemorySecretStore();
        secrets.Set(JudgmentDefaults.ApiKeySecretName, "sk-test-live-shape");
        using var http = new HttpClient(handler) { BaseAddress = new Uri(JudgmentDefaults.Endpoint) };
        var client = new TypeSafeJevClient(secrets, http, model: "jev-latest");
        var response = await client.JudgeAsync(MixedRequest());
        Assert.True(response.Ok, response.Error);
        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("Bearer", seen.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test-live-shape", seen.Headers.Authorization.Parameter);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("jev-latest", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.TryGetProperty("state", out _));
        Assert.True(doc.RootElement.TryGetProperty("questions", out var q));
        Assert.True(q.TryGetProperty("urgent", out _));
        Assert.DoesNotContain("sk-test", TypeSafeJevClient.BuildWireBody(MixedRequest()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_contract_422_no_retry()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(422, """{ "error": "bad schema" }"""));
        var secrets = new MemorySecretStore();
        secrets.Set(JudgmentDefaults.ApiKeySecretName, "sk-test");
        using var http = new HttpClient(handler) { BaseAddress = new Uri(JudgmentDefaults.Endpoint) };
        var client = new TypeSafeJevClient(secrets, http);
        var clock = new FixedClock(T0);
        var delays = 0;
        var dispatcher = new JudgmentDispatcher(client, new JudgmentTransportPolicy(clock), clock,
            delay: (_, _) => { delays++; return Task.CompletedTask; });
        var response = await dispatcher.DispatchAsync(MixedRequest());
        Assert.Equal(JudgmentErrorCodes.InvalidContract, response.ErrorCode);
        Assert.Equal(0, delays);
        Assert.Equal(1, handler.Calls);
    }

    private static HttpResponseMessage JsonResponse(int status, string json)
    {
        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public int Calls { get; private set; }

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> sync)
            : this((req, _) => Task.FromResult(sync(req))) { }

        public ScriptedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> async)
            : this((req, _) => async(req)) { }

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return await _handler(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Minimal FixedClock copy so Gateway.Tests does not reference Core.Tests internals awkwardly.</summary>
file sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset start) => UtcNow = start;
    public DateTimeOffset UtcNow { get; set; }
    public void Advance(TimeSpan by) => UtcNow += by;
}
