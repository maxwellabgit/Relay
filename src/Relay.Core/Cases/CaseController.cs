using System.Text.Json;
using Relay.Core.Policy;

namespace Relay.Core.Cases;

/// <summary>
/// Production-facing controller shell. Concrete workflow controllers plug in later;
/// today the runtime uses <see cref="LegacyMindBridgeController"/> for historical harnesses.
/// </summary>
public sealed class CaseController : ICaseController
{
    private readonly Func<CaseSnapshot, CaseInput, CaseTransition> _handle;

    public CaseController(string controllerId, string controllerVersion, Func<CaseSnapshot, CaseInput, CaseTransition> handle)
    {
        ControllerId = controllerId;
        ControllerVersion = controllerVersion;
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
    }

    public string ControllerId { get; }
    public string ControllerVersion { get; }

    public CaseTransition Handle(CaseSnapshot snapshot, CaseInput input) => _handle(snapshot, input);
}

/// <summary>
/// Bridges historical <see cref="ICaseMind"/> scripted minds into the controller/outbox path.
/// Deprecated for production: scripted minds only; no live model inference inside Handle.
/// </summary>
public sealed class LegacyMindBridgeController : ICaseController
{
    private readonly ICaseMind _mind;

    public LegacyMindBridgeController(ICaseMind mind)
    {
        _mind = mind ?? throw new ArgumentNullException(nameof(mind));
    }

    public string ControllerId => "legacy-mind-bridge:" + _mind.Name;
    public string ControllerVersion => "1";

    public CaseTransition Handle(CaseSnapshot snapshot, CaseInput input)
    {
        if (snapshot.Status is CaseStatus.Completed or CaseStatus.Cancelled)
            return new CaseTransition { Skip = true, SkipReason = "terminal" };

        // Waiting cases make no mind calls until a relevant wake/result/retry arrives.
        if (snapshot.Status == CaseStatus.Waiting
            && input.Kind is CaseInput.Wake or CaseInput.ManualStep
            && snapshot.PendingOperations.Any(o => o.Status == OperationStatus.AwaitingApproval))
        {
            return new CaseTransition { Skip = true, SkipReason = "waiting_approval" };
        }

        var request = new CaseMindRequest(
            snapshot.CaseId,
            snapshot.Origin,
            snapshot.Kind,
            snapshot.ApprovedObjective,
            snapshot.Version,
            snapshot.Status,
            snapshot.RecentEvents,
            snapshot.PendingOperationIds.ToList(),
            snapshot.PendingOperations.ToList(),
            input.At == default ? snapshot.At : input.At,
            snapshot.StepsUsed,
            snapshot.RecentSegments,
            snapshot.ParentCaseId,
            snapshot.PresentationPolicy,
            snapshot.AvailableTools);

        // Scripted minds complete synchronously; production must not place live inference here.
        var step = _mind.StepAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        return MindStepToTransition(snapshot, step, input);
    }

    internal static CaseTransition MindStepToTransition(CaseSnapshot snapshot, CaseMindStep step, CaseInput input)
    {
        var events = new List<CaseDomainEvent>();
        var commands = new List<RuntimeCommand>();
        var at = input.At == default ? snapshot.At : input.At;
        var causation = snapshot.ProcessedEventIds.LastOrDefault();

        events.Add(new CaseDomainEvent
        {
            Type = CaseDomainEventTypes.MindStepped,
            CausationId = causation,
            Payload = JsonSerializer.SerializeToElement(new
            {
                move = step.Move,
                feed = step.Feed,
                read = step.Read,
            }),
        });

        if (!IsMoveAllowed(snapshot, step.Move, out var denyReason))
        {
            events.Add(new CaseDomainEvent
            {
                Type = CaseDomainEventTypes.MoveRejected,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    move = step.Move.Type,
                    name = step.Move.Name,
                    reason = denyReason,
                }),
            });
            if (snapshot.Origin == CaseOrigin.Observed)
            {
                events.Add(StatusEvent(CaseStatus.Active, "move_rejected"));
            }
            return new CaseTransition
            {
                Events = events,
                Commands = commands,
                Feed = step.Feed,
                FeedLevel = "alert",
            };
        }

        switch (step.Move.Type)
        {
            case CaseMove.Propose:
                commands.Add(NewCommand(snapshot, RuntimeCommandKinds.ProposeOperation, at, causation, new Dictionary<string, JsonElement>(step.Move.Args, StringComparer.Ordinal)
                {
                    ["capability"] = JsonSerializer.SerializeToElement(
                        step.Move.Args.TryGetValue("capability", out var cap) && cap.ValueKind == JsonValueKind.String
                            ? cap.GetString() ?? step.Move.Name
                            : step.Move.Name),
                    ["label"] = JsonSerializer.SerializeToElement(step.Move.Text),
                    ["moveName"] = JsonSerializer.SerializeToElement(step.Move.Name),
                }));
                events.Add(StatusEvent(CaseStatus.Waiting, "propose_pending"));
                break;

            case CaseMove.UseTool:
            case CaseMove.Build:
            case CaseMove.RunWorkflow:
                commands.Add(NewCommand(snapshot, RuntimeCommandKinds.RequestLocalJob, at, causation, new Dictionary<string, JsonElement>(step.Move.Args, StringComparer.Ordinal)
                {
                    ["jobType"] = JsonSerializer.SerializeToElement(step.Move.Type),
                    ["name"] = JsonSerializer.SerializeToElement(step.Move.Name),
                    ["text"] = JsonSerializer.SerializeToElement(step.Move.Text),
                }));
                break;

            case CaseMove.RaiseTask:
                commands.Add(NewCommand(snapshot, RuntimeCommandKinds.CreateOrLinkCase, at, causation, new Dictionary<string, JsonElement>(step.Move.Args, StringComparer.Ordinal)
                {
                    ["objective"] = JsonSerializer.SerializeToElement(step.Move.Text),
                    ["text"] = JsonSerializer.SerializeToElement(step.Move.Text),
                }));
                break;

            case CaseMove.Wait:
            {
                var keepListening = snapshot.PresentationPolicy == StreamIntake.PresentationListening;
                events.Add(new CaseDomainEvent
                {
                    Type = CaseDomainEventTypes.WaitEntered,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        reason = step.Move.Text,
                        keepListening,
                    }),
                });
                events.Add(StatusEvent(keepListening ? CaseStatus.Active : CaseStatus.Waiting, step.Move.Text));
                break;
            }

            case CaseMove.Stop:
            case CaseMove.Say when step.Move.Done && snapshot.PresentationPolicy != StreamIntake.PresentationListening:
                if (step.Move.Args.Count > 0)
                {
                    events.Add(new CaseDomainEvent
                    {
                        Type = CaseDomainEventTypes.CitationsApplied,
                        Payload = JsonSerializer.SerializeToElement(new { args = step.Move.Args }),
                    });
                }
                // Publish feed before CompleteCase so the finding is not skipped by terminal guards.
                commands.Add(NewCommand(snapshot, RuntimeCommandKinds.PublishFeedItem, at, causation, new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["text"] = JsonSerializer.SerializeToElement(step.Feed),
                    ["level"] = JsonSerializer.SerializeToElement(ResolveAttention(snapshot, step.Move, step.Feed)),
                }));
                commands.Add(NewCommand(snapshot, RuntimeCommandKinds.CompleteCase, at, causation, new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["result"] = JsonSerializer.SerializeToElement(step.Move.Text),
                }));
                break;

            case CaseMove.Say:
                if (step.Move.Args.Count > 0)
                {
                    events.Add(new CaseDomainEvent
                    {
                        Type = CaseDomainEventTypes.CitationsApplied,
                        Payload = JsonSerializer.SerializeToElement(new { args = step.Move.Args }),
                    });
                }
                if (snapshot.PresentationPolicy == StreamIntake.PresentationListening)
                    events.Add(StatusEvent(CaseStatus.Active, "say"));
                commands.Add(NewCommand(snapshot, RuntimeCommandKinds.PublishFeedItem, at, causation, new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["text"] = JsonSerializer.SerializeToElement(step.Feed),
                    ["level"] = JsonSerializer.SerializeToElement(ResolveAttention(snapshot, step.Move, step.Feed)),
                }));
                break;
        }

        // Segment coverage for listening cases (explicit segmentId preferred).
        if (snapshot.Origin == CaseOrigin.Observed)
        {
            events.Add(new CaseDomainEvent
            {
                Type = CaseDomainEventTypes.SegmentHandled,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    segmentId = step.Move.Args.TryGetValue("segmentId", out var seg) && seg.ValueKind == JsonValueKind.String
                        ? seg.GetString()
                        : null,
                }),
            });
        }

        // Default feed publish for moves that did not already enqueue one (tools, propose, wait, raise).
        if (step.Move.Type is not CaseMove.Say and not CaseMove.Stop
            && !(step.Move.Type == CaseMove.Say && step.Move.Done))
        {
            commands.Add(NewCommand(snapshot, RuntimeCommandKinds.PublishFeedItem, at, causation, new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["text"] = JsonSerializer.SerializeToElement(step.Feed),
                ["level"] = JsonSerializer.SerializeToElement(ResolveAttention(snapshot, step.Move, step.Feed)),
            }));
        }

        return new CaseTransition
        {
            Events = events,
            Commands = commands,
            Feed = step.Feed,
            FeedLevel = ResolveAttention(snapshot, step.Move, step.Feed),
        };
    }

    private static RuntimeCommand NewCommand(
        CaseSnapshot snapshot,
        string kind,
        DateTimeOffset at,
        string? causation,
        Dictionary<string, JsonElement> payload)
    {
        var idSeed = kind + ":" + snapshot.CaseId + ":" + snapshot.Version + ":" + snapshot.StepsUsed + ":" + payload.Count;
        var commandId = StableId(idSeed);
        return new RuntimeCommand
        {
            CommandId = commandId,
            CaseId = snapshot.CaseId,
            Kind = kind,
            ObjectiveRevision = snapshot.ObjectiveRevision,
            LogicalKey = kind + ":" + snapshot.CaseId + ":" + snapshot.Version + ":" + snapshot.StepsUsed,
            CausedByEventId = causation,
            CreatedAt = at,
            Payload = payload,
            Status = RuntimeCommandStatus.Pending,
        };
    }

    private static string StableId(string seed)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return "cmd_" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private static CaseDomainEvent StatusEvent(string status, string reason) => new()
    {
        Type = CaseDomainEventTypes.StatusChanged,
        Payload = JsonSerializer.SerializeToElement(new { status, reason }),
    };

    private static bool IsMoveAllowed(CaseSnapshot snapshot, CaseMove move, out string? reason)
    {
        reason = null;
        if (snapshot.Origin != CaseOrigin.Observed) return true;

        switch (move.Type)
        {
            case CaseMove.Say:
            case CaseMove.Wait:
            case CaseMove.Stop:
            case CaseMove.RaiseTask:
                return true;
            case CaseMove.UseTool:
            {
                var tool = string.IsNullOrWhiteSpace(move.Name)
                    ? (move.Args.TryGetValue("tool", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
                    : move.Name;
                if (tool is CaseTools.LocalSearch or CaseTools.ReadNote) return true;
                reason = $"Observed cases may only use read-only tools (got '{tool}').";
                return false;
            }
            case CaseMove.Propose:
            {
                var cap = move.Name;
                if (move.Args.TryGetValue("capability", out var c) && c.ValueKind == JsonValueKind.String)
                    cap = c.GetString() ?? cap;
                if (cap is Actions.ModifyNote or Actions.CreateDraftNote or "file_note") return true;
                reason = $"Observed cases may not propose '{cap}'.";
                return false;
            }
            default:
                reason = $"Move '{move.Type}' is not allowed on observed cases.";
                return false;
        }
    }

    private static string ResolveAttention(CaseSnapshot snapshot, CaseMove move, string feed)
    {
        if (move.Args.TryGetValue("attention", out var att) && att.ValueKind == JsonValueKind.String)
            return att.GetString() ?? "ambient";
        if (move.Type == CaseMove.Say && move.Done && snapshot.PresentationPolicy != StreamIntake.PresentationListening)
            return "finding";
        if (move.Type == CaseMove.Propose) return "proposal";
        if (move.Type == CaseMove.RaiseTask) return "persistent";
        if (feed.Contains(" means ", StringComparison.OrdinalIgnoreCase)) return "persistent";
        return "ambient";
    }
}
