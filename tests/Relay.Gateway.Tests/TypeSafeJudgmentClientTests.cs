using System.Net;
using System.Text;
using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Judgments;
using Relay.Core.Model;
using Relay.Gateway;

namespace Relay.Gateway.Tests;

public sealed class TypeSafeJudgmentClientTests
{
    private const string SecretTranscript = "Move Atlas beta to October 21 and Max will send the draft Friday";

    [Fact]
    public async Task Posts_correct_url_method_headers_and_request_json()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var handler = new ScriptedHandler(async (req, _) =>
        {
            seen = req;
            body = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return JsonResponse(200, """
                {
                  "model": "jev-1.13.0",
                  "answers": { "is_urgent": { "type": "noul", "noul": 0.9 } },
                  "usage": { "input_tokens": 40, "output_tokens": 0 }
                }
                """);
        });

        using var client = CreateClient(handler, enabled: true);
        var result = await client.JudgeAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", seen.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer test-key", seen.Headers.Authorization?.ToString());
        Assert.Contains("\"type\":\"noul\"", body, StringComparison.Ordinal);
        Assert.Contains("\"instructions\":\"Does this convey urgency?\"", body, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"jev-1.13.0\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretTranscript, body!, StringComparison.Ordinal); // state is structured; still assert error surfaces stay clean
        Assert.Equal(0.9, Assert.IsType<NoulAnswer>(result.Success!.Answers["is_urgent"]).ProbabilityYes);
        Assert.Equal(40, result.Success.InputTokens);
    }

    [Fact]
    public async Task Http_401_maps_to_authentication()
    {
        using var client = CreateClient(new ScriptedHandler((_, _) =>
            Task.FromResult(JsonResponse(401, """{"error":"unauthorized"}"""))), enabled: true);
        var result = await client.JudgeAsync(BuildRequest(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(JudgmentFailureCategories.Authentication, result.Failure!.Category);
        Assert.DoesNotContain(SecretTranscript, result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_422_maps_to_validation_without_retry()
    {
        var calls = 0;
        using var client = CreateClient(new ScriptedHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse(422, """{"error":"bad question"}"""));
        }), enabled: true, maxAttempts: 3);

        var result = await client.JudgeAsync(BuildRequest(), CancellationToken.None);
        Assert.Equal(JudgmentFailureCategories.Validation, result.Failure!.Category);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Http_429_retries_then_succeeds()
    {
        var calls = 0;
        using var client = CreateClient(new ScriptedHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
                return Task.FromResult(JsonResponse(429, """{"error":"slow down"}"""));
            return Task.FromResult(JsonResponse(200, """
                {
                  "model": "jev-1.13.0",
                  "answers": { "is_urgent": { "type": "noul", "noul": 0.8 } },
                  "usage": { "input_tokens": 10, "output_tokens": 0 }
                }
                """));
        }), enabled: true, maxAttempts: 3);

        var result = await client.JudgeAsync(BuildRequest(), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Http_529_exhausts_retries_and_returns_overloaded()
    {
        var calls = 0;
        using var client = CreateClient(new ScriptedHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse(529, """{"error":"busy"}"""));
        }), enabled: true, maxAttempts: 3);

        var result = await client.JudgeAsync(BuildRequest(), CancellationToken.None);
        Assert.Equal(JudgmentFailureCategories.Overloaded, result.Failure!.Category);
        Assert.Equal(3, calls);
        Assert.DoesNotContain(SecretTranscript, JudgmentJson.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_returns_timeout_category()
    {
        using var client = CreateClient(new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return JsonResponse(200, "{}");
        }), enabled: true, timeoutMs: 50);

        var result = await client.JudgeAsync(BuildRequest(), CancellationToken.None);
        Assert.Equal(JudgmentFailureCategories.Timeout, result.Failure!.Category);
    }

    [Fact]
    public async Task Redirect_is_not_followed()
    {
        var calls = 0;
        using var client = CreateClient(new ScriptedHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/capture");
            return Task.FromResult(response);
        }), enabled: true);

        var result = await client.JudgeAsync(BuildRequest(), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.False(result.Ok);
        Assert.Equal(JudgmentFailureCategories.Network, result.Failure!.Category);
    }

    [Fact]
    public async Task Malformed_probabilities_return_invalid_response()
    {
        using var client = CreateClient(new ScriptedHandler((_, _) =>
            Task.FromResult(JsonResponse(200, """
                {
                  "model": "jev-1.13.0",
                  "answers": {
                    "route": {
                      "type": "choice",
                      "choice": "answer",
                      "probabilities": { "answer": 1.5 },
                      "confidence": 0.9
                    }
                  },
                  "usage": { "input_tokens": 5, "output_tokens": 0 }
                }
                """))), enabled: true);

        var result = await client.JudgeAsync(BuildChoiceRequest(), CancellationToken.None);
        Assert.Equal(JudgmentFailureCategories.InvalidResponse, result.Failure!.Category);
        Assert.DoesNotContain(SecretTranscript, result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_client_does_not_call_network()
    {
        var calls = 0;
        using var client = CreateClient(new ScriptedHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse(200, "{}"));
        }), enabled: false);

        var result = await client.JudgeAsync(BuildRequest(), CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(JudgmentFailureCategories.Disabled, result.Failure!.Category);
    }

    [Fact]
    public async Task Missing_secret_does_not_call_network()
    {
        var calls = 0;
        var settings = new JevSettings { Enabled = true, MaxAttempts = 1 };
        var secrets = new MemorySecretStore();
        using var client = new TypeSafeJudgmentClient(settings, secrets, new ScriptedHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse(200, "{}"));
        }));

        var result = await client.JudgeAsync(BuildRequest(), CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(JudgmentFailureCategories.MissingSecret, result.Failure!.Category);
    }

    [Fact]
    public void EstimateCostUsd_uses_documented_rate()
    {
        Assert.Equal(0.042m, TypeSafeJudgmentClient.EstimateCostUsd(1_000_000));
        Assert.Equal(0m, TypeSafeJudgmentClient.EstimateCostUsd(0));
    }

    [Fact]
    public void Parser_maps_wire_noul_to_probability_yes()
    {
        Assert.True(TypeSafeResponseParser.TryParse("""
            {
              "model": "jev-1.13.0",
              "answers": { "q": { "type": "noul", "noul": 0.42 } },
              "usage": { "input_tokens": 3, "output_tokens": 0 }
            }
            """, 12, out var success, out _));
        Assert.Equal(0.42, Assert.IsType<NoulAnswer>(success!.Answers["q"]).ProbabilityYes);
    }

    private static TypeSafeJudgmentClient CreateClient(
        HttpMessageHandler handler,
        bool enabled,
        int maxAttempts = 3,
        int timeoutMs = 10_000)
    {
        var settings = new JevSettings
        {
            Enabled = enabled,
            Endpoint = TypeSafeJudgmentClient.DefaultEndpoint,
            Model = TypeSafeJudgmentClient.DefaultModel,
            SecretName = "typesafe-jev",
            TimeoutMs = timeoutMs,
            MaxAttempts = maxAttempts,
        };
        var secrets = new MemorySecretStore();
        secrets.Set("typesafe-jev", "test-key");
        return new TypeSafeJudgmentClient(settings, secrets, handler);
    }

    private static JudgmentRequest BuildRequest() => new()
    {
        QuestionSetId = "transport.smoke",
        QuestionSetVersion = "1",
        Model = "jev-1.13.0",
        State = JudgmentState.Parse("""{"text":"Help! My payouts have been failing for 3 days."}"""),
        Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            ["is_urgent"] = new NoulQuestion
            {
                Instructions = "Does this convey urgency?",
            },
        },
    };

    private static JudgmentRequest BuildChoiceRequest() => new()
    {
        QuestionSetId = "direct.route.v1",
        QuestionSetVersion = "1",
        Model = "jev-1.13.0",
        State = JudgmentState.Parse("""{"text":"what is BESS"}"""),
        Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            ["route"] = new ChoiceQuestion
            {
                Instructions = "Which route fits?",
                Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["answer"] = "Answer",
                    ["no_match"] = "No match",
                },
            },
        },
    };

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

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
