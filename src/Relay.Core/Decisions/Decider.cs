using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Mind;
using Relay.Core.Storage;

namespace Relay.Core.Decisions;

/// <summary>
/// One decision between paths, on record: the features it was made from, the weights in force, the
/// score, the outcome, and a one-line rationale. Written to the ledger as <c>decision.made</c> and
/// kept per task so the usage lines can be analysed later.
/// </summary>
public sealed record DecisionRecord(string Name, IReadOnlyDictionary<string, double> Features, IReadOnlyDictionary<string, double> Weights, double Score, string Outcome, string Rationale);

/// <summary>Weights and thresholds of one named decision. Everything tunable lives here, nothing in code.</summary>
public sealed class DecisionSpec
{
    [JsonPropertyName("weights")] public Dictionary<string, double> Weights { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("thresholds")] public Dictionary<string, double> Thresholds { get; set; } = new(StringComparer.Ordinal);

    public double W(string key, double fallback = 0) => Weights.TryGetValue(key, out var v) ? v : fallback;
    public double T(string key, double fallback) => Thresholds.TryGetValue(key, out var v) ? v : fallback;
}

/// <summary>
/// The set of decision specs, stored as <c>config\decisions.json</c>. Missing specs and keys fall back
/// to the defaults so an edited file can be partial. <see cref="Development"/> is the switch that
/// loosens the hard rules of the tool manifest validator while the architecture is being built.
/// </summary>
public sealed class DecisionSet
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("development")] public bool Development { get; set; } = true;
    [JsonPropertyName("decisions")] public Dictionary<string, DecisionSpec> Decisions { get; set; } = new(StringComparer.Ordinal);

    public static DecisionSet Default() => new()
    {
        Decisions = new Dictionary<string, DecisionSpec>(StringComparer.Ordinal)
        {
            [Decider.Route] = new()
            {
                Weights = new() { ["complexity"] = 0.6, ["external_reasoning"] = 0.3, ["world_knowledge"] = 0.15, ["local_notes"] = -0.1, ["new_tool"] = 1.0 },
                Thresholds = new() { ["delegate"] = 0.6, ["build"] = 0.5 },
            },
            [Decider.Fof] = new()
            {
                Weights = new() { ["core"] = 1.0, ["security"] = 1.0, ["loop"] = 0.8, ["destructive"] = 1.0, ["self_directed"] = 0.2 },
                Thresholds = new() { ["approval"] = 0.4, ["refuse"] = 0.8 },
            },
            [Decider.Filing] = new() { Thresholds = new() { ["auto"] = 0.75, ["ask"] = 0.35 } },
            [Decider.Retry] = new() { Thresholds = new() { ["contract"] = 2, ["model"] = 1 } },
            [Decider.Narrate] = new() { Thresholds = new() { ["minChars"] = 400, ["minSeconds"] = 8 } },
        },
    };

    public DecisionSpec Spec(string name)
    {
        if (Decisions.TryGetValue(name, out var spec)) return spec;
        return Default().Decisions.TryGetValue(name, out var fallback) ? fallback : new DecisionSpec();
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        foreach (var (name, spec) in Decisions)
        {
            foreach (var (k, v) in spec.Weights) if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 100) problems.Add($"decisions.{name}.weights.{k} must be a finite number within ±100.");
            foreach (var (k, v) in spec.Thresholds) if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) problems.Add($"decisions.{name}.thresholds.{k} must be a non-negative number.");
        }
        var filing = Spec(Decider.Filing);
        if (filing.T("ask", 0.35) > filing.T("auto", 0.75)) problems.Add("decisions.filing.thresholds.ask must not exceed thresholds.auto.");
        var fof = Spec(Decider.Fof);
        if (fof.T("approval", 0.4) > fof.T("refuse", 0.8)) problems.Add("decisions.fof.thresholds.approval must not exceed thresholds.refuse.");
        return problems;
    }

    public string ToJson() => JsonSerializer.Serialize(this, RelayJson.Indented);

    /// <summary>Loads the file, writing the defaults when it is missing. Invalid content is reported and the defaults are used.</summary>
    public static DecisionSet Load(DataRoot root, out IReadOnlyList<string> problems)
    {
        var list = new List<string>();
        problems = list;
        var text = AtomicFile.ReadAllTextIfExists(root.DecisionsPath);
        if (text is null)
        {
            var defaults = Default();
            Directory.CreateDirectory(root.ConfigDirectory);
            AtomicFile.WriteAllText(root.DecisionsPath, defaults.ToJson());
            return defaults;
        }
        DecisionSet set;
        try { set = JsonSerializer.Deserialize<DecisionSet>(text, RelayJson.Indented) ?? Default(); }
        catch (JsonException ex) { list.Add($"decisions.json could not be parsed ({ex.Message}); defaults are in effect."); return Default(); }
        var validation = set.Validate();
        if (validation.Count > 0) { list.AddRange(validation); return Default(); }
        return set;
    }
}

/// <summary>
/// Every choice between two or more paths is made here, from features the mind or the engine
/// produced, with weights from the <see cref="DecisionSet"/>, and reported to the sink. Nothing in
/// the loop compares a number to a constant; it asks the decider and records the answer.
/// </summary>
public sealed class Decider
{
    public const string Route = "route";
    public const string Fof = "fof";
    public const string Filing = "filing";
    public const string Retry = "retry";
    public const string Narrate = "narrate";

    // route outcomes
    public const string Local = "local";
    public const string OfferDelegate = "offer_delegate";
    public const string OfferBuild = "offer_build";
    // fof outcomes
    public const string Allow = "allow";
    public const string Approval = "approval";
    public const string Refuse = "refuse";
    // filing outcomes
    public const string Auto = "auto";
    public const string Ask = "ask";
    public const string Inbox = "inbox";
    // retry / narrate outcomes
    public const string DoRetry = "retry";
    public const string GiveUp = "fail";
    public const string Surface = "surface";
    public const string Hold = "hold";

    private readonly DecisionSet _set;
    private readonly Action<DecisionRecord>? _sink;
    private readonly List<DecisionRecord> _made = new();

    public Decider(DecisionSet? set = null, Action<DecisionRecord>? sink = null)
    {
        _set = set ?? DecisionSet.Default();
        _sink = sink;
    }

    public DecisionSet Set => _set;
    public IReadOnlyList<DecisionRecord> Made => _made;

    /// <summary>Where a task goes after the mind's first read: stay local, offer delegation, or offer to build a tool.</summary>
    public DecisionRecord RouteFor(MindRead read, bool delegatesAvailable, bool canBuild)
    {
        var spec = _set.Spec(Route);
        var features = new Dictionary<string, double>(StringComparer.Ordinal) { ["complexity"] = read.Complexity };
        foreach (var need in MindRead.KnownNeeds) if (need != MindRead.NeedNone) features[need] = read.Has(need) ? 1 : 0;
        features["delegates_available"] = delegatesAvailable ? 1 : 0;
        features["can_build"] = canBuild ? 1 : 0;

        var buildScore = spec.W("new_tool", 1.0) * features[MindRead.NeedNewTool];
        if (canBuild && buildScore >= spec.T("build", 0.5))
            return Record(Route, features, spec, buildScore, OfferBuild, $"needs new_tool × {F(spec.W("new_tool", 1.0))} = {F(buildScore)} ≥ {F(spec.T("build", 0.5))}");

        var score = spec.W("complexity", 0.6) * read.Complexity
                  + spec.W(MindRead.NeedExternalReasoning) * features[MindRead.NeedExternalReasoning]
                  + spec.W(MindRead.NeedWorldKnowledge) * features[MindRead.NeedWorldKnowledge]
                  + spec.W(MindRead.NeedLocalNotes) * features[MindRead.NeedLocalNotes];
        score = Math.Clamp(score, 0, 1);
        var threshold = spec.T("delegate", 0.6);
        var terms = new StringBuilder($"complexity {F(read.Complexity)} × {F(spec.W("complexity", 0.6))}");
        foreach (var need in new[] { MindRead.NeedExternalReasoning, MindRead.NeedWorldKnowledge, MindRead.NeedLocalNotes })
            if (features[need] > 0 && spec.W(need) != 0) terms.Append($" + {need} {F(spec.W(need))}");
        if (score >= threshold && delegatesAvailable) return Record(Route, features, spec, score, OfferDelegate, $"{terms} = {F(score)} ≥ {F(threshold)}");
        var why = score >= threshold ? $"{terms} = {F(score)} ≥ {F(threshold)}, but no delegate profile is configured" : $"{terms} = {F(score)} < {F(threshold)}";
        return Record(Route, features, spec, score, Local, why);
    }

    /// <summary>The fundamental-operation flag for a move that changes Relay or builds capability: allow, ask, or refuse from the mind's own risk read. It can only raise the bar the hard rules set.</summary>
    public DecisionRecord FofFor(MindRead? read, string moveType, string? action, bool selfDirected)
    {
        var spec = _set.Spec(Fof);
        var risk = read?.Risk ?? RiskRead.None;
        var features = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["core"] = risk.Core, ["security"] = risk.Security, ["loop"] = risk.Loop, ["destructive"] = risk.Destructive, ["self_directed"] = selfDirected ? 1 : 0,
        };
        var weighted = new[]
        {
            ("core", spec.W("core", 1.0) * risk.Core), ("security", spec.W("security", 1.0) * risk.Security),
            ("loop", spec.W("loop", 0.8) * risk.Loop), ("destructive", spec.W("destructive", 1.0) * risk.Destructive),
        };
        var (axis, top) = weighted.OrderByDescending(w => w.Item2).First();
        var score = Math.Clamp(top + (selfDirected ? spec.W("self_directed", 0.2) : 0), 0, 1);
        var subject = $"{moveType}{(action is null ? "" : " " + action)}";
        if (score >= spec.T("refuse", 0.8)) return Record(Fof, features, spec, score, Refuse, $"{subject}: {axis} risk {F(top)}{(selfDirected ? " + self-directed" : "")} = {F(score)} ≥ {F(spec.T("refuse", 0.8))}");
        if (score >= spec.T("approval", 0.4)) return Record(Fof, features, spec, score, Approval, $"{subject}: {axis} risk {F(top)}{(selfDirected ? " + self-directed" : "")} = {F(score)} ≥ {F(spec.T("approval", 0.4))}");
        return Record(Fof, features, spec, score, Allow, $"{subject}: highest weighted risk {axis} {F(score)} < {F(spec.T("approval", 0.4))}");
    }

    /// <summary>Whether a note the mind wants to file goes in automatically, is put to the user, or waits in the inbox.</summary>
    public DecisionRecord FilingFor(double confidence)
    {
        var spec = _set.Spec(Filing);
        var features = new Dictionary<string, double>(StringComparer.Ordinal) { ["confidence"] = confidence };
        if (confidence >= spec.T("auto", 0.75)) return Record(Filing, features, spec, confidence, Auto, $"confidence {F(confidence)} ≥ {F(spec.T("auto", 0.75))}");
        if (confidence >= spec.T("ask", 0.35)) return Record(Filing, features, spec, confidence, Ask, $"confidence {F(confidence)} ≥ {F(spec.T("ask", 0.35))}");
        return Record(Filing, features, spec, confidence, Inbox, $"confidence {F(confidence)} < {F(spec.T("ask", 0.35))}");
    }

    /// <summary>After a failed step: try again or give up. <paramref name="kind"/> is "contract" (the reply broke the schema) or "model" (unavailable, timed out).</summary>
    public DecisionRecord RetryFor(int failuresSoFar, string kind)
    {
        var spec = _set.Spec(Retry);
        var allowed = spec.T(kind, kind == "contract" ? 2 : 1);
        var features = new Dictionary<string, double>(StringComparer.Ordinal) { ["failures"] = failuresSoFar, ["kind_contract"] = kind == "contract" ? 1 : 0 };
        var outcome = failuresSoFar <= allowed ? DoRetry : GiveUp;
        return Record(Retry, features, spec, failuresSoFar, outcome, $"{kind} failure {failuresSoFar} of {F(allowed)} allowed");
    }

    /// <summary>Whether a streaming delegate's progress is surfaced to the mind now or held a little longer.</summary>
    public DecisionRecord NarrateFor(int charsSinceSurfaced, double secondsSinceSurfaced)
    {
        var spec = _set.Spec(Narrate);
        var features = new Dictionary<string, double>(StringComparer.Ordinal) { ["chars"] = charsSinceSurfaced, ["seconds"] = secondsSinceSurfaced };
        var byChars = charsSinceSurfaced >= spec.T("minChars", 400);
        var bySeconds = charsSinceSurfaced > 0 && secondsSinceSurfaced >= spec.T("minSeconds", 8);
        var outcome = byChars || bySeconds ? Surface : Hold;
        return Record(Narrate, features, spec, charsSinceSurfaced, outcome, byChars ? $"{charsSinceSurfaced} chars ≥ {F(spec.T("minChars", 400))}" : bySeconds ? $"{F(secondsSinceSurfaced)}s ≥ {F(spec.T("minSeconds", 8))}s" : "not enough new text yet");
    }

    private DecisionRecord Record(string name, Dictionary<string, double> features, DecisionSpec spec, double score, string outcome, string rationale)
    {
        var weights = new Dictionary<string, double>(spec.Weights, StringComparer.Ordinal);
        foreach (var (k, v) in spec.Thresholds) weights["threshold." + k] = v;
        var record = new DecisionRecord(name, features, weights, Math.Round(score, 4), outcome, rationale);
        _made.Add(record);
        _sink?.Invoke(record);
        return record;
    }

    private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
