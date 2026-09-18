using System.Text.Json;
using Relay.Core.Judgments;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

public sealed class JudgmentContractTests
{
    [Fact]
    public void Noul_probability_outside_range_is_rejected()
    {
        var answer = new NoulAnswer { ProbabilityYes = 1.2 };
        var ex = Assert.Throws<JudgmentValidationException>(() => answer.Validate("q1"));
        Assert.Contains("probabilityYes", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret transcript", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Choice_probabilities_missing_an_option_are_rejected()
    {
        var criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["answer"] = "Provide an answer.",
            ["clarify"] = "Ask a clarifying question.",
            ["no_match"] = "None of the options fit.",
        };
        var answer = new ChoiceAnswer
        {
            Choice = "answer",
            Confidence = 0.8,
            Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["answer"] = 0.7,
                ["clarify"] = 0.3,
            },
        };

        var ex = Assert.Throws<JudgmentValidationException>(
            () => answer.ValidateAgainstCriteria("route", criteria));
        Assert.Contains("no_match", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_answer_type_is_rejected_on_deserialize()
    {
        const string json = """
            {"type":"essay","text":"free form"}
            """;

        Assert.ThrowsAny<JsonException>(() => JudgmentJson.Deserialize<JudgmentAnswer>(json));
    }

    [Fact]
    public void Successful_response_round_trips_through_json()
    {
        var original = JudgmentResponse.FromSuccess(new JudgmentSuccess
        {
            Model = "jev-1.13.0",
            InputTokens = 120,
            OutputTokens = 0,
            ElapsedMs = 42,
            ProviderRequestId = "req_test",
            Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
            {
                ["contains_durable_decision"] = new NoulAnswer { ProbabilityYes = 0.91 },
                ["attention"] = new ChoiceAnswer
                {
                    Choice = "persistent",
                    Confidence = 0.74,
                    Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["ambient"] = 0.1,
                        ["persistent"] = 0.7,
                        ["alert"] = 0.2,
                    },
                },
                ["urgency"] = new ScoreAnswer
                {
                    Score = 1.4,
                    Confidence = 0.66,
                    Legend = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["0"] = "Low",
                        ["1"] = "Medium",
                        ["2"] = "High",
                    },
                    Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["0"] = 0.2,
                        ["1"] = 0.5,
                        ["2"] = 0.3,
                    },
                },
            },
        });
        original.Validate();

        var json = JudgmentJson.Serialize(original);
        var roundTripped = JudgmentJson.Deserialize<JudgmentResponse>(json);
        roundTripped.Validate();

        Assert.True(roundTripped.Ok);
        Assert.Equal("jev-1.13.0", roundTripped.Success!.Model);
        Assert.Equal(120, roundTripped.Success.InputTokens);
        Assert.Equal(3, roundTripped.Success.Answers.Count);
        Assert.Equal(0.91, Assert.IsType<NoulAnswer>(roundTripped.Success.Answers["contains_durable_decision"]).ProbabilityYes);
        Assert.Equal("persistent", Assert.IsType<ChoiceAnswer>(roundTripped.Success.Answers["attention"]).Choice);
        Assert.Equal(1.4, Assert.IsType<ScoreAnswer>(roundTripped.Success.Answers["urgency"]).Score);
    }

    [Fact]
    public void Errors_contain_no_state_text()
    {
        const string secret = "Atlas beta moves to October 21 and Max will email the draft";
        var failure = JudgmentFailure.Create(
            JudgmentFailureCategories.NotAuthorized,
            "Hosted grant missing for required source classification.");

        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        var json = JudgmentJson.Serialize(JudgmentResponse.FromFailure(failure));
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Contains("not_authorized", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_reaches_the_provider()
    {
        var client = new FakeJudgmentClient
        {
            Behavior = FakeJudgmentBehavior.Cancel,
            SimulatedDelay = TimeSpan.FromMilliseconds(50),
        };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = BuildMinimalRequest();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.JudgeAsync(request, cts.Token));
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Fake_client_matches_question_set_and_counts_calls()
    {
        var response = JudgmentResponse.FromSuccess(new JudgmentSuccess
        {
            Model = "jev-1.13.0",
            Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
            {
                ["contains_correction"] = new NoulAnswer { ProbabilityYes = 0.95 },
            },
        });
        var client = new FakeJudgmentClient().Script("conversation.screen.v1", response);

        var result = await client.JudgeAsync(BuildMinimalRequest(), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(1, client.CallCount);
        Assert.Equal("conversation.screen.v1", client.Calls[0].QuestionSetId);
    }

    [Fact]
    public async Task Fake_client_can_simulate_rate_limit()
    {
        var client = new FakeJudgmentClient { Behavior = FakeJudgmentBehavior.RateLimited };
        var result = await client.JudgeAsync(BuildMinimalRequest(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(JudgmentFailureCategories.RateLimited, result.Failure!.Category);
        Assert.True(result.Failure.Retryable);
    }

    [Fact]
    public void Request_rejects_empty_instructions()
    {
        var request = new JudgmentRequest
        {
            QuestionSetId = "direct.route.v1",
            QuestionSetVersion = "1",
            Model = "jev-1.13.0",
            State = JudgmentState.Parse("""{"text":"hello"}"""),
            Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
            {
                ["route"] = new ChoiceQuestion
                {
                    Instructions = " ",
                    Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["answer"] = "Answer",
                        ["no_match"] = "No match",
                    },
                },
            },
        };

        var ex = Assert.Throws<JudgmentValidationException>(() => request.Validate());
        Assert.Contains("instructions", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Question_set_registry_rejects_duplicates()
    {
        var def = new QuestionSetDefinition
        {
            Id = "direct.route.v1",
            Version = "1",
            Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
            {
                ["route"] = new ChoiceQuestion
                {
                    Instructions = "Which route fits the direct request?",
                    Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["answer"] = "Answer",
                        ["no_match"] = "No match",
                    },
                },
            },
        };

        Assert.Throws<JudgmentValidationException>(() => new QuestionSetRegistry([def, def]));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.2)]
    public void Noul_rejects_non_finite_or_out_of_range_probability(double value)
    {
        var answer = new NoulAnswer { ProbabilityYes = value };
        Assert.Throws<JudgmentValidationException>(() => answer.Validate("q"));
    }

    [Fact]
    public void Choice_without_no_match_is_rejected_by_default()
    {
        var question = new ChoiceQuestion
        {
            Instructions = "Pick a route.",
            Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["answer"] = "Answer",
            },
        };
        var ex = Assert.Throws<JudgmentValidationException>(() => question.Validate("route"));
        Assert.Contains(ChoiceQuestion.NoMatch, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Authentication_failure_is_never_retryable()
    {
        var failure = JudgmentFailure.Create(JudgmentFailureCategories.Authentication, "bad key");
        Assert.False(failure.Retryable);
        Assert.True(JudgmentFailure.Create(JudgmentFailureCategories.Timeout, "slow").Retryable);
    }

    [Fact]
    public async Task Invalid_request_returns_validation_failure_not_throw()
    {
        var client = new FakeJudgmentClient();
        var request = new JudgmentRequest
        {
            QuestionSetId = "direct.route.v1",
            QuestionSetVersion = "1",
            Model = "jev-1.13.0",
            State = JudgmentState.Parse("""{"text":"x"}"""),
            Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
            {
                ["route"] = new ChoiceQuestion
                {
                    Instructions = " ",
                    Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["answer"] = "Answer",
                        ["no_match"] = "No match",
                    },
                },
            },
        };

        var result = await client.JudgeAsync(request, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(JudgmentFailureCategories.Validation, result.Failure!.Category);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void Success_validate_against_rejects_wrong_answer_type()
    {
        var request = BuildMinimalRequest();
        var success = new JudgmentSuccess
        {
            Model = "jev-1.13.0",
            Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
            {
                ["contains_correction"] = new ChoiceAnswer
                {
                    Choice = "yes",
                    Confidence = 0.9,
                    Probabilities = new Dictionary<string, double>(StringComparer.Ordinal) { ["yes"] = 1 },
                },
            },
        };

        var ex = Assert.Throws<JudgmentValidationException>(() => success.ValidateAgainst(request));
        Assert.Contains("wrong type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_state_hash_is_key_order_independent()
    {
        var a = JudgmentState.Parse("""{"b":1,"a":2}""");
        var b = JudgmentState.Parse("""{"a":2,"b":1}""");
        Assert.Equal(
            JudgmentRequestHasher.HashJsonElement(a),
            JudgmentRequestHasher.HashJsonElement(b));
    }

    [Fact]
    public void Judgment_record_serialization_omits_state_and_answers()
    {
        const string secret = "secret transcript words must not appear";
        var record = new JudgmentRecord
        {
            JudgmentId = "j1",
            Provider = "fake",
            QuestionSetId = "conversation.screen.v1",
            QuestionSetVersion = "1",
            Model = "jev-1.13.0",
            Status = JudgmentStatuses.Completed,
            RequestHash = "abc",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var json = JudgmentJson.Serialize(record);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("probabilityYes", json, StringComparison.Ordinal);
        Assert.Contains("\"provider\":\"fake\"", json, StringComparison.Ordinal);
    }

    private static JudgmentRequest BuildMinimalRequest() => new()
    {
        QuestionSetId = "conversation.screen.v1",
        QuestionSetVersion = "1",
        Model = "jev-1.13.0",
        State = JudgmentState.Parse("""{"segments":["hello"]}"""),
        CaseId = "case_test",
        CaseVersion = 1,
        Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            ["contains_correction"] = new NoulQuestion
            {
                Instructions = "Does the unread transcript contain a correction of a prior claim?",
            },
        },
    };
}
