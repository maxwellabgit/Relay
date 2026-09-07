using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Relay.Core.Attention;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Tasks;
using Windows.UI;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Desktop;

/// <summary>
/// Attention (the arbiter's cards at their levels), Tasks (every task with its process tag and cost),
/// Relay (compiled preferences, grants, change sets) and the per-task diagnostics drawer.
/// Pure rendering over the snapshot; every button forwards one coordinator call.
/// </summary>
public sealed partial class MainWindow
{
    // ------------------------------------------------------------------------------------
    // ATTENTION: what RELAY0 decided you should see, at the level the arbiter chose
    // ------------------------------------------------------------------------------------

    private void RenderAttention(RelaySnapshot s)
    {
        var items = s.Attention;
        var carded = items.Where(i => i.Level != Presentation.Ambient).ToList();
        var ambient = items.Where(i => i.Level == Presentation.Ambient).ToList();
        var signature = string.Join("|", items.Select(i => $"{i.ItemId}:{i.Level}:{i.Occurrences}:{i.LastAt.Ticks}:{i.Title.Length}:{i.Detail.Length}:{TaskSignature(s, i.TaskId)}"));
        var needing = carded.Count(i => i.NeedsAction);
        var alerts = carded.Count(i => i.Level == Presentation.Alert);
        AttentionCount.Text = items.Count == 0 ? "" : string.Join(" · ", new[]
        {
            needing > 0 ? $"{needing} awaiting approval" : null,
            alerts > 0 ? $"{alerts} alert(s)" : null,
            $"{items.Count} card(s)",
        }.Where(p => p is not null));
        AttentionEmpty.Visibility = Vis(items.Count == 0);
        if (signature == _attentionSignature) return;
        _attentionSignature = signature;

        AttentionItems.Children.Clear();
        foreach (var item in carded) AttentionItems.Children.Add(AttentionCard(item, s));

        AmbientItems.Children.Clear();
        if (ambient.Count > 0)
        {
            AmbientItems.Children.Add(new TextBlock { Text = "AMBIENT", Style = (Style)RootGrid.Resources["RegionHeader"], Margin = new Thickness(0, 4, 0, 2) });
            foreach (var item in ambient) AmbientItems.Children.Add(AmbientRow(item));
        }
    }

    private static string TaskSignature(RelaySnapshot s, string? taskId)
    {
        var task = taskId is null ? null : s.Tasks.FirstOrDefault(t => t.TaskId == taskId);
        return task is null ? "" : $"{task.Status}:{string.Join(",", task.Proposals.Select(p => p.ProposalId + p.Status + p.BlockedBy))}";
    }

    private Border AttentionCard(AttentionItem item, RelaySnapshot s)
    {
        var task = item.TaskId is null ? null : s.Tasks.FirstOrDefault(t => t.TaskId == item.TaskId);
        var panel = new StackPanel { Spacing = 6 };

        var header = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };
        header.Children.Add(LevelChip(item.Level));
        header.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true });
        if (item.Pinned) header.Children.Add(Chip("pinned · refreshed in place"));
        if (item.Occurrences > 1) header.Children.Add(Chip($"×{item.Occurrences} merged"));
        panel.Children.Add(header);

        if (item.Detail.Length > 0 && item.Detail != item.Title)
            panel.Children.Add(new TextBlock { Text = item.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19, IsTextSelectionEnabled = true });

        var meta = new List<string> { $"{item.Kind.Wire()} · {OriginText(item.Origin)}", item.LastAt.ToLocalTime().ToString("HH:mm:ss") };
        if (task is not null)
        {
            if (task.Citations.Count > 0) meta.Add($"{task.Citations.Count} source(s)");
            if (task.Confidence is > 0 and < 1) meta.Add($"confidence {task.Confidence:0.00}");
            if (task.ExcerptId is not null) meta.Add($"excerpt {Short(task.ExcerptId)}");
        }
        meta.Add(item.Reason);
        panel.Children.Add(new TextBlock { Text = string.Join("  ·  ", meta), TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Secondary() });

        if (task is not null && task.Citations.Count > 0 && item.Level is Presentation.Alert or Presentation.Result or Presentation.Findings)
        {
            var n = 1;
            foreach (var c in task.Citations.Take(3))
                panel.Children.Add(new TextBlock { Text = $"{n++}. {CitationWhere(c)} — {Trim(c.Excerpt, 200)}", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Secondary(), IsTextSelectionEnabled = true });
        }

        if (task is not null && item.NeedsAction)
        {
            foreach (var p in task.Proposals.Where(p => p.Status is "pending" or "denied"))
                panel.Children.Add(ProposalCard(p, task, compact: true));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (task is not null && item.NeedsAction)
        {
            var approvable = task.Proposals.Count(p => p.Status == "pending" && p.BlockedBy is null);
            if (approvable > 1 && task.Status == TaskStatus.AwaitingApproval) buttons.Children.Add(Button($"Approve all ({approvable})", () => _coordinator!.ApproveAll(task.TaskId), accent: true, small: true));
            if (task.Status == TaskStatus.AwaitingApproval) buttons.Children.Add(Button("Reject all", () => { foreach (var p in task.Proposals.Where(p => p.Status == "pending").ToList()) _coordinator!.Reject(p.ProposalId, "rejected from the card"); }, small: true));
        }
        if (item.TaskId is { } taskId) buttons.Children.Add(Button("Details", () => ShowTaskDiagnostics(taskId), small: true));
        if (!item.NeedsAction)
        {
            buttons.Children.Add(Button("Dismiss", () =>
            {
                _coordinator!.DismissAttention(item.ItemId);
                foreach (var id in item.TaskIds) _coordinator.RecordUserResponse(id, "dismissed");
            }, small: true));
            if (item.TaskId is { } id2 && task?.UserResponse is null)
                buttons.Children.Add(Button("Not needed", () =>
                {
                    _coordinator!.DismissAttention(item.ItemId);
                    _coordinator.RecordUserResponse(id2, "not_needed");
                }, small: true));
        }
        panel.Children.Add(buttons);

        return SubCard(panel, LevelStripe(item.Level));
    }

    /// <summary>A subtle indicator: one line, no card. A note was filed, something ran under a grant.</summary>
    private Grid AmbientRow(AttentionItem item)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Dot(Palette.Neutral));
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = item.Title });
        if (item.Detail.Length > 0 && item.Detail != item.Title) text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + Trim(item.Detail, 140), Foreground = Secondary() });
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"  {item.LastAt.ToLocalTime():HH:mm:ss}" + (item.Occurrences > 1 ? $" ×{item.Occurrences}" : ""), Foreground = Secondary(), FontSize = 11 });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (item.TaskId is { } taskId) buttons.Children.Add(TinyButton("details", () => ShowTaskDiagnostics(taskId)));
        buttons.Children.Add(TinyButton("dismiss", () => _coordinator!.DismissAttention(item.ItemId)));
        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);
        return row;
    }

    private static Button TinyButton(string text, Action action)
    {
        var button = Button(text, action, small: true);
        button.Padding = new Thickness(8, 1, 8, 1);
        button.FontSize = 11;
        return button;
    }

    private Border LevelChip(Presentation level)
    {
        var chip = Chip(level.ToString().ToUpperInvariant(), "Mono");
        ((TextBlock)chip.Child).Foreground = new SolidColorBrush(LevelColor(level));
        return chip;
    }

    private static Color LevelColor(Presentation level) => level switch
    {
        Presentation.Alert => Palette.Bad,
        Presentation.Proposal => Palette.Warn,
        Presentation.Findings => Palette.Note,
        Presentation.Result => Palette.Command,
        _ => Palette.Neutral,
    };

    private static Brush? LevelStripe(Presentation level) => level == Presentation.Ambient ? null : new SolidColorBrush(LevelColor(level));

    private static string OriginText(TaskOrigin origin) => origin switch
    {
        TaskOrigin.Direct => "you asked",
        TaskOrigin.Observed => "overheard",
        _ => "follow-up",
    };

    // ------------------------------------------------------------------------------------
    // TASKS: every task of the session with the lane it runs in and what it cost
    // ------------------------------------------------------------------------------------

    private void RenderTasks(RelaySnapshot s)
    {
        var shown = s.Tasks.OrderByDescending(t => t.Live).ThenByDescending(t => t.StartedAt).Take(14).ToList();
        var live = s.Tasks.Count(t => t.Live);
        var cost = s.SessionCost;
        TasksCount.Text = s.Tasks.Count == 0 ? "" : $"{live} running · {s.Tasks.Count - live} finished" + (cost.TotalTokens > 0 ? $" · {cost.TotalTokens} tokens" : "");
        TasksEmpty.Visibility = Vis(s.Tasks.Count == 0);
        var signature = string.Join("|", shown.Select(t => $"{t.TaskId}:{t.Status}:{t.Tag}:{t.Presentation}:{t.UserResponse}:{t.Cost.ToolCalls}:{t.Cost.ModelCalls}:{t.Proposals.Count(p => p.Status == "pending")}"));
        if (signature == _tasksSignature) return;
        _tasksSignature = signature;

        TaskItems.Children.Clear();
        foreach (var t in shown) TaskItems.Children.Add(TaskRow(t));
        if (s.Tasks.Count > shown.Count)
            TaskItems.Children.Add(new TextBlock { Text = $"… {s.Tasks.Count - shown.Count} earlier task(s) this session; every record is in tasks\\", FontSize = 11, Foreground = Secondary() });
    }

    private Grid TaskRow(TaskView t)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = Dot(t.Status switch
        {
            TaskStatus.Planning => Palette.Command,
            TaskStatus.AwaitingApproval => Palette.Warn,
            TaskStatus.Executing => Palette.Good,
            TaskStatus.Completed => t.Outcome is "executed" or "answered" ? Palette.Good : Palette.Neutral,
            TaskStatus.Failed => Palette.Bad,
            _ => Palette.Neutral,
        }, 8);
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.Margin = new Thickness(0, 6, 0, 0);
        row.Children.Add(dot);

        var text = new StackPanel { Spacing = 2 };
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Trim(t.Title ?? t.Instruction, 90), FontWeight = FontWeights.SemiBold });
        if (t.Foreground) title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  foreground", Foreground = Secondary(), FontSize = 11 });
        text.Children.Add(title);

        var meta = new List<string>
        {
            t.Tag,
            $"{t.Kind.Wire()} · {t.Lane.Replace('_', ' ')} · {OriginText(t.Origin)}",
            t.Producer,
            t.StartedAt.ToLocalTime().ToString("HH:mm:ss"),
            CostText(t.Cost),
        };
        if (!t.Live) meta.Add(t.Presentation == Presentation.None ? "shown: nothing" : $"shown: {t.Presentation.Wire()}");
        if (t.Proposals.Count > 0) meta.Add($"{t.Proposals.Count(p => p.Status == "executed")}/{t.Proposals.Count} proposal(s) ran");
        if (t.UserResponse is not null) meta.Add($"you: {t.UserResponse.Replace('_', ' ')}");
        text.Children.Add(new TextBlock { Text = string.Join("  ·  ", meta), TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Secondary() });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(Button("Details", () => ShowTaskDiagnostics(t.TaskId), small: true));
        if (t.Live) buttons.Children.Add(Button(t.Status == TaskStatus.Executing ? "Stop" : "Cancel", () => _coordinator!.CancelTask(t.TaskId), small: true));
        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);
        return row;
    }

    // ------------------------------------------------------------------------------------
    // RELAY: the compiled preferences, standing grants, and every self-change with its revert
    // ------------------------------------------------------------------------------------

    private void RenderRelay(RelaySnapshot s)
    {
        var p = s.Preferences;
        var settled = s.State is RelayState.Idle or RelayState.Completed && !s.LiveTasks.Any(t => t.Foreground);
        var signature = $"{p.Verbosity}|{p.MaxAnswerChars}|{p.PromptFragment.Length}|{string.Join(",", p.WatchedTerms)}|{string.Join(",", p.Grants.Select(g => g.GrantId))}|{p.MaxAlertsPer10Minutes}|{p.MaxResultsPer5Minutes}|{p.Cooldown}|{p.Buffer}|{p.ExcerptMaxSeconds}|{p.MaxRetainedFraction}|{p.AllowOnlineSearch}|{string.Join(",", s.ChangeSets.Select(c => c.ChangeSetId + c.Reverted))}|{settled}|{s.Projects.Count}";
        RelayMeta.Text = s.ChangeSets.Count == 0 ? "defaults · no change sets yet" : $"{s.ChangeSets.Count} change set(s) · {s.ChangeSets.Count(c => !c.Reverted)} in effect";
        if (signature == _relaySignature) return;
        _relaySignature = signature;

        PreferencesText.Text =
            $"Responses {p.Verbosity} (≤ {p.MaxAnswerChars} chars, {p.MaxAnswerTokens} tokens)  ·  Online search for observed tasks: {(p.AllowOnlineSearch ? "allowed" : "not granted")}  ·  " +
            $"Alerts ≤ {p.MaxAlertsPer10Minutes} per 10 min, results ≤ {p.MaxResultsPer5Minutes} per 5 min, cool-down {p.Cooldown.TotalSeconds:0}s  ·  " +
            $"Buffer {p.Buffer.TotalSeconds:0}s, excerpts ≤ {p.ExcerptMaxSeconds:0}s, retained ≤ {p.MaxRetainedFraction:P0} of elapsed\n" +
            $"Prompt fragment: “{Trim(p.PromptFragment, 220)}”";

        PinnedPanel.Visibility = Vis(p.WatchedTerms.Count > 0);
        PinnedTerms.Children.Clear();
        foreach (var term in p.WatchedTerms)
        {
            var t = term;
            var unpin = Button($"{t}  ×", () => _coordinator!.UpdatePreference("display.stopShowing", t), small: true, enabled: settled);
            ToolTipService.SetToolTip(unpin, "Stop always showing this term (a reversible change set)");
            PinnedTerms.Children.Add(unpin);
        }

        GrantsPanel.Visibility = Vis(p.Grants.Count > 0);
        GrantItems.Children.Clear();
        foreach (var g in p.Grants)
        {
            var project = g.ProjectId is null ? "any project" : s.Projects.FirstOrDefault(pr => pr.Id == g.ProjectId)?.Name ?? Short(g.ProjectId);
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{g.Action.Replace('_', ' ')} · {project}" + (g.NoteType is null ? "" : $" · {g.NoteType} notes"), FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"  granted {g.GrantedAt.ToLocalTime():MM-dd HH:mm} · {Trim(g.Reason, 120)}", Foreground = Secondary(), FontSize = 11 });
            row.Children.Add(text);
            var id = g.GrantId;
            var revoke = Button("Revoke", () => _coordinator!.UpdatePreference("filing.revoke", id), small: true, enabled: settled);
            Grid.SetColumn(revoke, 1);
            row.Children.Add(revoke);
            GrantItems.Children.Add(row);
        }

        ChangeSetsPanel.Visibility = Vis(s.ChangeSets.Count > 0);
        ChangeSetItems.Children.Clear();
        foreach (var c in s.ChangeSets)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true };
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{c.AppliedAt.ToLocalTime():MM-dd HH:mm:ss} · {c.Kind} · {c.File}", Foreground = Secondary(), FontSize = 11 });
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + Trim(c.Reason, 160), TextDecorations = c.Reverted ? global::Windows.UI.Text.TextDecorations.Strikethrough : global::Windows.UI.Text.TextDecorations.None });
            row.Children.Add(text);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (c.TaskId is { } taskId) right.Children.Add(Button("Task", () => ShowTaskDiagnostics(taskId), small: true));
            if (c.Reverted) right.Children.Add(Chip($"reverted {c.RevertedAt!.Value.ToLocalTime():HH:mm:ss}"));
            else
            {
                var id = c.ChangeSetId;
                right.Children.Add(Button("Revert", () => _coordinator!.RevertChangeSet(id), small: true));
            }
            Grid.SetColumn(right, 1);
            row.Children.Add(right);
            ChangeSetItems.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------------------------
    // DIAGNOSTICS DRAWER: one task in full — prompt, tools, model calls, decisions, presentation, your response
    // ------------------------------------------------------------------------------------

    private void RenderTaskDiagnostics(RelaySnapshot s)
    {
        if (_diagnosticsTaskId is null || _coordinator is null)
        {
            TaskDiagnosticsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TaskDiagnosticsPanel.Visibility = Visibility.Visible;
        var task = s.Tasks.FirstOrDefault(t => t.TaskId == _diagnosticsTaskId);
        if (task is not null)
        {
            var signature = $"{task.TaskId}|{task.Status}|{task.Tag}|{task.Steps.Count}|{task.Answer?.Length}|{task.ToolCalls.Count}|{task.ModelCalls.Count}|{string.Join(",", task.Proposals.Select(p => p.ProposalId + p.Status))}|{task.Presentation}|{task.UserResponse}|{task.CompletedAt}";
            if (signature == _taskDiagnosticsSignature) return;
            _taskDiagnosticsSignature = signature;
            TaskDiagnosticsTitle.Text = $"TASK {Short(task.TaskId)} · {task.Tag.ToUpperInvariant()}";
            FillRows(TaskDiagnosticsGrid, Rows(task));
            return;
        }

        var record = _coordinator.ReadDiagnostics(_diagnosticsTaskId);
        var recordSignature = record is null ? "missing:" + _diagnosticsTaskId : $"record:{record.TaskId}|{record.Status}|{record.UserResponse}";
        if (recordSignature == _taskDiagnosticsSignature) return;
        _taskDiagnosticsSignature = recordSignature;
        if (record is null)
        {
            TaskDiagnosticsTitle.Text = $"TASK {Short(_diagnosticsTaskId)}";
            FillRows(TaskDiagnosticsGrid, new List<(string, string, string?)> { ("Record", "No record for this task on disk. Records are written when a task ends; a task from an earlier run may have been trimmed.", null) });
            return;
        }
        TaskDiagnosticsTitle.Text = $"TASK {Short(record.TaskId)} · {record.Status.ToUpperInvariant()} (from disk)";
        FillRows(TaskDiagnosticsGrid, Rows(record));
    }

    private List<(string Label, string Value, string? OpenPath)> Rows(TaskView t)
    {
        var rows = new List<(string, string, string?)>
        {
            ("Task", $"{t.TaskId}\n{OriginText(t.Origin)} · {t.Kind.Wire()} · lane {t.Lane} · {t.Tag}{(t.Foreground ? " · foreground" : "")}\nstatus {t.Status.Wire()}{(t.Outcome is null ? "" : " · outcome " + t.Outcome)}", null),
            ("Focused prompt", t.Instruction, null),
        };
        if (t.Origin != TaskOrigin.Direct || t.Why is not null)
            rows.Add(("Why it started", string.Join("\n", new[] { t.Title, t.Why, t.Confidence is > 0 and < 1 ? $"judge confidence {t.Confidence:0.00}" : null, t.ExcerptId is null ? null : $"excerpt {t.ExcerptId} (only the selected sentences were kept)", t.ParentTaskId is null ? null : $"follows task {t.ParentTaskId}" }.Where(x => x is not null)), t.ExcerptId is null || _runtime is null ? null : Path.Combine(_runtime.Root.ExcerptsDirectory, t.ExcerptId + ".json")));
        rows.Add(("Planner", $"{t.Producer}\n{t.Summary}", null));
        if (t.Steps.Count > 0) rows.Add(("Steps", string.Join("\n", t.Steps.Select((step, i) => $"{i + 1}. {step}")), null));
        if (!string.IsNullOrWhiteSpace(t.Answer)) rows.Add(("Answer", t.Answer!, null));
        if (t.Citations.Count > 0) rows.Add(("Sources", string.Join("\n", t.Citations.Select((c, i) => $"{i + 1}. {CitationWhere(c)} — {Trim(c.Excerpt, 160)}")), null));
        if (!t.Knowledge.IsEmpty) rows.Add(("Knowledge state", KnowledgeText(t.Knowledge), null));
        rows.Add(("Tool calls", t.ToolCalls.Count == 0 ? "none" : string.Join("\n", t.ToolCalls.Select(c => $"{c.At.ToLocalTime():HH:mm:ss.fff}  {c.Tool}({string.Join(", ", c.Args.Select(kv => $"{kv.Key}={Trim(kv.Value, 60)}"))}) → {(c.Ok ? "ok" : "FAILED")} · {c.Items} item(s) · {Trim(c.Summary, 120)}")), null));
        rows.Add(("Model calls", t.ModelCalls.Count == 0 ? "none — the deterministic grammar handled it" : string.Join("\n", t.ModelCalls.Select(m => $"{m.At.ToLocalTime():HH:mm:ss.fff}  {m.Model} at {m.Host} · {m.PromptChars} prompt chars · {m.PromptTokens}+{m.CompletionTokens} tokens · {m.ElapsedMs} ms · {(m.Ok ? "ok" : "FAILED " + m.Error)}")), null));
        rows.Add(("Proposals", t.Proposals.Count == 0 ? "none" : string.Join("\n", t.Proposals.Select(p =>
            $"{p.Action} · {p.Tier} · {p.Status}{(p.GrantedBy is null ? "" : " · standing grant")}{(p.DependsOn.Count > 0 ? " · after " + string.Join(", ", p.DependsOn.Select(Short)) : "")}\n   {p.Title}"
            + (p.Reasons.Count > 0 ? "\n   policy: " + string.Join(" ", p.Reasons) : "")
            + (p.BlockedBy is null ? "" : "\n   blocked: " + p.BlockedBy)
            + (p.ResultSummary is null ? "" : "\n   result: " + p.ResultSummary)
            + (p.Error is null ? "" : "\n   error: " + p.Error))), null));
        rows.Add(("Presentation", t.Live ? "not decided yet — the arbiter ranks a task when it finishes" : $"{t.Presentation.Wire()} — {t.PresentationReason ?? "no reason recorded"}", null));
        rows.Add(("Your response", t.UserResponse?.Replace('_', ' ') ?? "none yet", null));
        rows.Add(("Cost", $"{t.Cost.PromptTokens} prompt + {t.Cost.CompletionTokens} completion tokens · {t.Cost.ModelCalls} model call(s) · {t.Cost.ToolCalls} tool call(s) · {t.Cost.WallMs} ms wall", null));
        rows.Add(("Timing", $"started {t.StartedAt.ToLocalTime():HH:mm:ss.fff}" + (t.CompletedAt is { } done ? $" · ended {done.ToLocalTime():HH:mm:ss.fff}" : " · running"), _runtime is null ? null : Path.Combine(_runtime.Root.TasksDirectory, t.TaskId + (t.Live ? ".live.json" : ".json"))));
        return rows;
    }

    private List<(string Label, string Value, string? OpenPath)> Rows(TaskDiagnostics d)
    {
        var rows = new List<(string, string, string?)>
        {
            ("Task", $"{d.TaskId}\n{d.Origin} · {d.Kind} · status {d.Status}{(d.Outcome is null ? "" : " · outcome " + d.Outcome)}{(d.ParentTaskId is null ? "" : " · follows " + d.ParentTaskId)}", null),
            ("Focused prompt", d.FocusedPrompt, null),
        };
        if (d.ExcerptId is not null) rows.Add(("Excerpt", d.ExcerptId, _runtime is null ? null : Path.Combine(_runtime.Root.ExcerptsDirectory, d.ExcerptId + ".json")));
        rows.Add(("Planner", $"{d.Planner ?? "?"}\n{d.Summary}", null));
        if (d.Steps.Count > 0) rows.Add(("Steps", string.Join("\n", d.Steps.Select((step, i) => $"{i + 1}. {step}")), null));
        if (!string.IsNullOrWhiteSpace(d.Answer)) rows.Add(("Answer", d.Answer!, null));
        if (d.Citations.Count > 0) rows.Add(("Sources", string.Join("\n", d.Citations), null));
        if (!d.Knowledge.IsEmpty) rows.Add(("Knowledge state", KnowledgeText(d.Knowledge), null));
        rows.Add(("Tool calls", d.ToolCalls.Count == 0 ? "none" : string.Join("\n", d.ToolCalls.Select(c => $"{c.At.ToLocalTime():HH:mm:ss.fff}  {c.Tool}({string.Join(", ", c.Args.Select(kv => $"{kv.Key}={Trim(kv.Value, 60)}"))}) → {(c.Ok ? "ok" : "FAILED")} · {c.Items} item(s) · {Trim(c.Summary, 120)}")), null));
        rows.Add(("Model calls", d.ModelCalls.Count == 0 ? "none — the deterministic grammar handled it" : string.Join("\n", d.ModelCalls.Select(m => $"{m.At.ToLocalTime():HH:mm:ss.fff}  {m.Model} at {m.Host} · {m.PromptChars} prompt chars · {m.PromptTokens}+{m.CompletionTokens} tokens · {m.ElapsedMs} ms · {(m.Ok ? "ok" : "FAILED " + m.Error)}")), null));
        rows.Add(("Proposals", d.Proposals.Count == 0 ? "none" : string.Join("\n", d.Proposals.Select(p =>
            $"{p.Action} · {p.Tier} · {p.Status}{(p.GrantedBy is null ? "" : " · standing grant")}{(p.DependsOn.Count > 0 ? " · after " + string.Join(", ", p.DependsOn.Select(Short)) : "")}\n   {string.Join(", ", p.Target.Select(kv => $"{kv.Key}={Trim(kv.Value, 60)}"))}"
            + (p.Reasons.Count > 0 ? "\n   policy: " + string.Join(" ", p.Reasons) : "")
            + (p.Result is null ? "" : "\n   result: " + p.Result)
            + (p.Error is null ? "" : "\n   error: " + p.Error))), null));
        rows.Add(("Presentation", $"{d.Presentation} — {d.PresentationReason ?? "no reason recorded"}", null));
        rows.Add(("Your response", d.UserResponse?.Replace('_', ' ') ?? "none", null));
        rows.Add(("Cost", $"{d.PromptTokens} prompt + {d.CompletionTokens} completion tokens · {d.ModelCalls.Count} model call(s) · {d.ToolCalls.Count} tool call(s) · {d.WallMs} ms wall", null));
        rows.Add(("Timing", $"started {d.StartedAt.ToLocalTime():HH:mm:ss.fff}" + (d.CompletedAt is { } done ? $" · ended {done.ToLocalTime():HH:mm:ss.fff}" : ""), _runtime is null ? null : Path.Combine(_runtime.Root.TasksDirectory, d.TaskId + ".json")));
        return rows;
    }
}
