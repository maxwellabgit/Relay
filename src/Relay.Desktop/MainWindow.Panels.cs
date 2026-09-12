using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Relay.Core.Config;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Tasks;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Desktop;

/// <summary>Response, Review, Inbox and Projects regions plus the dialogs they open. Pure rendering over the snapshot.</summary>
public sealed partial class MainWindow
{
    // ------------------------------------------------------------------------------------
    // RESPONSE: the orchestrator's visible reasoning and its proposals
    // ------------------------------------------------------------------------------------

    private void RenderResponse(RelaySnapshot s)
    {
        var r = s.Response;
        ResponseCard.Visibility = Vis(r is not null);
        if (r is null) { _responseSignature = ""; return; }

        var signature = $"{r.TaskId}|{r.Status}|{r.Tag}|{r.Outcome}|{r.Live}|{r.Steps.Count}|{r.Summary}|{r.Answer?.Length}|{r.Consistent}|{r.Knowledge.Summary}|{r.Presentation}|{r.Cost.ToolCalls}|{r.Cost.ModelCalls}|{string.Join(",", r.Proposals.Select(p => p.ProposalId + p.Status + p.BlockedBy))}|{s.State}";
        if (signature == _responseSignature) return;
        _responseSignature = signature;

        ResponseMeta.Text = $"{r.Producer} · {r.Outcome ?? r.Tag.ToLowerInvariant()} · {r.StartedAt.ToLocalTime():HH:mm:ss}";
        ResponseInstruction.Text = "“" + Trim(r.Instruction, 240) + "”";
        ResponseSummary.Text = r.Summary;

        // Process tags: the lane, the kind, what it cost, and how the arbiter ranked it once finished.
        ResponseTags.Children.Clear();
        ResponseTags.Children.Add(Chip(r.Tag, status: r.Status == TaskStatus.Failed ? "failed" : r.Live ? "pending" : "executed"));
        ResponseTags.Children.Add(Chip($"{r.Kind.Wire()} · {r.Lane.Replace('_', ' ')}", "Mono"));
        if (r.Consistent is { } consistent) ResponseTags.Children.Add(Chip(consistent ? "consistent with stored facts" : "conflicts with stored facts", status: consistent ? "executed" : "denied"));
        if (r.Cost.ModelCalls > 0 || r.Cost.ToolCalls > 0) ResponseTags.Children.Add(Chip(CostText(r.Cost), "Mono"));
        if (!r.Live && r.Presentation != Presentation.None) ResponseTags.Children.Add(Chip($"ranked {r.Presentation.Wire()}", "Mono"));
        var details = Button("Details", () => ShowTaskDiagnostics(r.TaskId), small: true);
        details.Padding = new Thickness(10, 2, 10, 2);
        details.FontSize = 11;
        ResponseTags.Children.Add(details);

        ResponseSteps.Children.Clear();
        foreach (var step in r.Steps)
            ResponseSteps.Children.Add(new TextBlock { Text = "›  " + step, FontSize = 12, Foreground = Secondary(), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas") });

        ResponseAnswerBorder.Visibility = Vis(!string.IsNullOrWhiteSpace(r.Answer));
        ResponseAnswer.Text = r.Answer ?? "";

        ResponseKnowledge.Visibility = Vis(!r.Knowledge.IsEmpty);
        ResponseKnowledge.Text = KnowledgeText(r.Knowledge);

        ResponseCitations.Children.Clear();
        if (r.Citations.Count > 0)
        {
            ResponseCitations.Children.Add(new TextBlock { Text = $"SOURCES ({r.Citations.Count})", Style = (Style)RootGrid.Resources["RegionHeader"] });
            var n = 1;
            foreach (var c in r.Citations)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var where = CitationWhere(c);
                var span = c.Span is { } sp ? $" · ledger {Short(sp.EventId)} [{sp.Start}–{sp.End}]" : "";
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true };
                text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{n++}. {where}{span}\n", Foreground = Secondary() });
                text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Trim(c.Excerpt, 300) });
                row.Children.Add(text);
                if (c.Kind == SearchIndex.NoteKind && NotePath(c.ProjectId, c.Id) is { } path)
                {
                    var open = Button("Open", () => OpenInExplorer(path), small: true);
                    Grid.SetColumn(open, 1);
                    row.Children.Add(open);
                }
                ResponseCitations.Children.Add(row);
            }
        }

        ResponseProposals.Children.Clear();
        if (r.Proposals.Count > 0)
        {
            ResponseProposals.Children.Add(new TextBlock { Text = $"PROPOSALS ({r.Proposals.Count})", Style = (Style)RootGrid.Resources["RegionHeader"] });
            foreach (var p in r.Proposals) ResponseProposals.Children.Add(ProposalCard(p, r));
        }

        ResponseButtons.Children.Clear();
        var pending = r.Proposals.Count(p => p.Status == "pending" && p.BlockedBy is null);
        if (pending > 1 && r.Status == TaskStatus.AwaitingApproval) ResponseButtons.Children.Add(Button($"Approve all ({pending})", () => _coordinator!.ApproveAll(r.TaskId), accent: true));
        if (r.Status == TaskStatus.Executing) ResponseButtons.Children.Add(Button("Stop", () => _coordinator!.CancelTask(r.TaskId)));
        else if (r.Live && r.Status is TaskStatus.Planning or TaskStatus.AwaitingApproval) ResponseButtons.Children.Add(Button("Cancel task", () => _coordinator!.CancelTask(r.TaskId)));
    }

    /// <summary>
    /// One proposal as a card: what it would do, the tier, what policy said, its dependencies, and — while
    /// its task awaits approval — the decision buttons. Decisions are per task, so a background task's
    /// proposals can be approved from Attention while the foreground is busy with something else.
    /// </summary>
    private Border ProposalCard(ProposalView p, TaskView task, bool compact = false)
    {
        var panel = new StackPanel { Spacing = 6 };
        var header = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };
        header.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(Chip(p.Action, "Mono"));
        header.Children.Add(Chip(p.Tier switch { Tier.Automatic => "tier A · automatic", Tier.RequiresApproval => "tier B · needs approval", _ => "tier C · prohibited" }));
        header.Children.Add(Chip(p.Status.ToUpperInvariant(), status: p.Status));
        if (p.GrantedBy is not null) header.Children.Add(Chip("standing grant", status: "executed"));
        header.Children.Add(Chip($"proposed by {p.ProposedBy}"));
        panel.Children.Add(header);
        // Detail already ends with the proposal's own "Why:" line (ProposalText), so it is not repeated here.
        panel.Children.Add(new TextBlock { Text = p.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true });
        if (p.DependsOn.Count > 0)
        {
            var names = p.DependsOn.Select(id => task.Proposals.FirstOrDefault(d => d.ProposalId == id) is { } dep ? $"{dep.Title} ({dep.Status})" : Short(id));
            panel.Children.Add(new TextBlock { Text = "After: " + string.Join("; ", names), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Secondary() });
        }
        if (p.BlockedBy is not null)
            panel.Children.Add(new TextBlock { Text = "Blocked: " + p.BlockedBy, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res("SystemFillColorCautionBrush") });
        if (!compact && p.Target.Count > 0)
            panel.Children.Add(new TextBlock { Text = string.Join("\n", p.Target.Select(kv => $"{kv.Key} = {Trim(kv.Value, 120)}")), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Foreground = Secondary(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (p.Reasons.Count > 0)
            panel.Children.Add(new TextBlock { Text = "Policy: " + string.Join(" ", p.Reasons), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = p.Status == "denied" ? Res("SystemFillColorCriticalBrush") : Secondary() });
        if (p.ResultSummary is not null)
            panel.Children.Add(new TextBlock { Text = p.ResultSummary, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res("SystemFillColorSuccessBrush") });
        if (p.Error is not null)
            panel.Children.Add(new TextBlock { Text = p.Error, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res("SystemFillColorCriticalBrush") });

        if (p.Status == "pending" && task.Status == TaskStatus.AwaitingApproval)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Button("Approve", () => _coordinator!.Approve(p.ProposalId), accent: true, enabled: p.BlockedBy is null));
            if (p.Editable) buttons.Children.Add(Button("Edit…", async () => await ShowEditProposalDialogAsync(p)));
            // A delegation is offered, not imposed: the user can send the mind back to local means instead (the words reach it as the rejection reason).
            if (p.Action == Actions.ModelRequest) buttons.Children.Add(Button("Retry locally", () => _coordinator!.Reject(p.ProposalId, "retry locally: try again without an external model")));
            buttons.Children.Add(Button("Reject", () => _coordinator!.Reject(p.ProposalId, "rejected by user")));
            panel.Children.Add(buttons);
        }

        return SubCard(panel, p.Status switch { "pending" when p.BlockedBy is not null => "SystemFillColorCautionBrush", "pending" => "AccentFillColorDefaultBrush", "denied" or "failed" => "SystemFillColorCriticalBrush", _ => null });
    }

    private static string CitationWhere(Relay.Core.Orchestration.Citation c) => c.Kind switch
    {
        SearchIndex.NoteKind => $"{c.ProjectSlug} · note {Short(c.Id)}",
        SearchIndex.DraftKind => $"staging · draft {Short(c.Id)}",
        SearchIndex.ExcerptKind => $"heard · excerpt {Short(c.Id)}",
        SearchIndex.ArtifactKind => $"external result · {Short(c.Id)}",
        _ => $"capture {Short(c.Id)}",
    };

    private static string KnowledgeText(KnowledgeState k)
    {
        var parts = new List<string>();
        if (k.Summary.Length > 0) parts.Add(k.Summary);
        if (k.Known.Count > 0) parts.Add("Known locally: " + string.Join("; ", k.Known));
        if (k.Missing.Count > 0) parts.Add("Missing: " + string.Join("; ", k.Missing));
        parts.Add(k.CapabilityGap ? "Capability: beyond the local model — an external task would be needed and is only proposed, never run, without approval." : "Capability: within the local model.");
        return "Knowledge state — " + string.Join("  ·  ", parts);
    }

    private static string CostText(TaskCost c)
    {
        var parts = new List<string>();
        if (c.TotalTokens > 0) parts.Add($"{c.TotalTokens} tok");
        if (c.ModelCalls > 0) parts.Add($"{c.ModelCalls} model call{(c.ModelCalls == 1 ? "" : "s")}");
        if (c.ToolCalls > 0) parts.Add($"{c.ToolCalls} tool call{(c.ToolCalls == 1 ? "" : "s")}");
        parts.Add(c.WallMs < 1000 ? $"{c.WallMs} ms" : $"{c.WallMs / 1000.0:0.0} s");
        return string.Join(" · ", parts);
    }

    /// <summary>A flat inner card: subtle fill, no outline, an optional 2 px accent stripe on the left edge.</summary>
    private static Border SubCard(UIElement child, string? stripeBrush) => SubCard(child, stripeBrush is null ? null : Res(stripeBrush));

    private static Border SubCard(UIElement child, Brush? stripe) => new()
    {
        Background = Res("SubtleFillColorSecondaryBrush"),
        BorderBrush = stripe,
        BorderThickness = stripe is null ? new Thickness(0) : new Thickness(2, 0, 0, 0),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 10, 14, 10),
        Child = child,
    };

    // ------------------------------------------------------------------------------------
    // REVIEW: everything that needs a human decision
    // ------------------------------------------------------------------------------------

    private void RenderReview(RelaySnapshot s)
    {
        var signature = string.Join("|", s.Review.Select(r => $"{r.Kind}:{r.Title}:{r.Detail.Length}:{r.Payload?.Length}")) + $"|{s.State}|{s.CanRetry}|{s.Projects.Count}";
        ReviewCount.Text = s.Review.Count == 0 ? "" : $"{s.Review.Count} item(s)";
        ReviewEmpty.Visibility = Vis(s.Review.Count == 0);
        if (signature == _reviewSignature) return;
        _reviewSignature = signature;

        ReviewItems.Children.Clear();
        foreach (var item in s.Review)
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = item.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Secondary(), IsTextSelectionEnabled = true });
            if (!string.IsNullOrEmpty(item.Payload) && item.Kind is ReviewItemKind.InterruptedCapture or ReviewItemKind.CancelledDraft or ReviewItemKind.RecordedInstruction)
            {
                panel.Children.Add(new Border
                {
                    Background = Res("ControlFillColorDefaultBrush"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 6, 10, 6),
                    Child = new TextBlock { Text = item.Payload, TextWrapping = TextWrapping.Wrap, MaxLines = 6, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13, IsTextSelectionEnabled = true },
                });
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var wrap = new StackPanel { Spacing = 6 };
            switch (item.Kind)
            {
                case ReviewItemKind.InterruptedCapture:
                    buttons.Children.Add(Button("Commit as captured", () => _coordinator!.CommitInterrupted(), accent: true, enabled: s.State == RelayState.Idle));
                    buttons.Children.Add(Button("Discard to staging", () => _coordinator!.DiscardInterrupted()));
                    break;
                case ReviewItemKind.CancelledDraft:
                    buttons.Children.Add(Button("Recover draft", () => _coordinator!.RecoverCancelledDraft(), accent: true, enabled: s.State == RelayState.Idle));
                    buttons.Children.Add(Button("Forget", () => _coordinator!.ForgetCancelledDraft()));
                    break;
                case ReviewItemKind.Incident:
                    if (s.State == RelayState.Locked) buttons.Children.Add(Button("Unlock", () => _coordinator!.Unlock(), accent: true));
                    if (s.State == RelayState.Failed)
                    {
                        if (s.CanRetry) buttons.Children.Add(Button("Retry", () => _coordinator!.Retry(), accent: true));
                        buttons.Children.Add(Button("Return to Idle", () => _coordinator!.Dismiss()));
                    }
                    if (item.Payload is { } incidentPath && File.Exists(incidentPath)) buttons.Children.Add(Button("Open incident file", () => OpenInExplorer(incidentPath)));
                    break;
                case ReviewItemKind.SettingsProblem:
                case ReviewItemKind.HotkeyProblem:
                    buttons.Children.Add(Button("Settings…", async () => await ShowSettingsDialogAsync()));
                    buttons.Children.Add(Button("Open settings.json", () => OpenInExplorer(_runtime!.Root.SettingsPath)));
                    break;
                case ReviewItemKind.ExecutionInterrupted:
                    buttons.Children.Add(Button("Open executions folder", () => OpenInExplorer(_runtime!.Root.ExecutionsDirectory)));
                    break;
                case ReviewItemKind.DisputedNotes:
                    if (item.Payload is { } newNoteId)
                    {
                        buttons.Children.Add(Button("New supersedes old", () => _coordinator!.ResolveDispute(newNoteId, true), accent: true));
                        buttons.Children.Add(Button("Keep both", () => _coordinator!.ResolveDispute(newNoteId, false)));
                    }
                    break;
                case ReviewItemKind.IndexProblem:
                    if (item.Payload is { } problemPath && (File.Exists(problemPath) || Directory.Exists(problemPath))) buttons.Children.Add(Button("Open", () => OpenInExplorer(problemPath)));
                    break;
            }
            if (buttons.Children.Count > 0) { wrap.Children.Add(buttons); panel.Children.Add(wrap); }

            ReviewItems.Children.Add(SubCard(panel, item.Kind == ReviewItemKind.Incident ? "SystemFillColorCriticalBrush" : null));
        }
    }

    // ------------------------------------------------------------------------------------
    // INBOX: unrouted notes, each shown once, with the router's candidates when it asked
    // ------------------------------------------------------------------------------------

    private void RenderInbox(RelaySnapshot s)
    {
        var active = s.Projects.Where(p => p.Status == "active").ToList();
        var signature = string.Join("|", s.Inbox.Select(i => $"{i.NoteId}:{i.Candidates.Count}")) + $"#{s.TurnActive}#{active.Count}";
        var asking = s.Inbox.Count(i => i.HasSuggestions);
        InboxCount.Text = s.Inbox.Count == 0 ? "" : $"{s.Inbox.Count} unrouted" + (asking > 0 ? $" · {asking} with suggestions" : "");
        InboxEmpty.Visibility = Vis(s.Inbox.Count == 0);
        if (signature == _inboxSignature) return;
        _inboxSignature = signature;

        InboxItems.Children.Clear();
        foreach (var item in s.Inbox.Take(20))
        {
            var panel = new StackPanel { Spacing = 6 };
            var header = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            header.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{item.Type} · {item.CreatedAt.ToLocalTime():MM-dd HH:mm}  ", Foreground = Secondary(), FontSize = 12 });
            header.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Trim(item.Text, 240), FontSize = 13 });
            panel.Children.Add(header);
            if (item.HasSuggestions)
            {
                var why = item.Candidates.Select(c => $"{c.Name} {c.Confidence:0.00} ({string.Join(", ", c.Reasons)})");
                panel.Children.Add(new TextBlock { Text = $"{item.Summary}  ·  " + string.Join("  ·  ", why), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Secondary() });
            }

            var noteId = item.NoteId;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var first = true;
            foreach (var c in item.Candidates.Take(3))
            {
                var projectId = c.ProjectId;
                buttons.Children.Add(Button($"File under {c.Name}", () => _coordinator!.RouteDraftNote(noteId, projectId), accent: first, small: true, enabled: !s.TurnActive));
                first = false;
            }
            buttons.Children.Add(Button(item.HasSuggestions ? "Other project…" : "File under…", async () => await ShowChooseProjectDialogAsync("File this note under", id => _coordinator!.RouteDraftNote(noteId, id)), small: true, enabled: !s.TurnActive && active.Count > 0));
            if (item.HasSuggestions) buttons.Children.Add(Button("Keep here", () => _coordinator!.KeepUnrouted(noteId), small: true, enabled: !s.TurnActive));
            panel.Children.Add(buttons);

            InboxItems.Children.Add(SubCard(panel, item.HasSuggestions ? "AccentFillColorDefaultBrush" : null));
        }
        if (s.Inbox.Count > 20) InboxItems.Children.Add(new TextBlock { Text = $"… and {s.Inbox.Count - 20} more in staging\\notes", FontSize = 12, Foreground = Secondary() });
    }

    // ------------------------------------------------------------------------------------
    // PROJECTS
    // ------------------------------------------------------------------------------------

    private void RenderProjects(RelaySnapshot s)
    {
        var signature = string.Join("|", s.Projects.Select(p => $"{p.Id}:{p.Status}:{p.Name}:{p.FolderPresent}")) + $"#{s.State}";
        var active = s.Projects.Where(p => p.Status == "active").ToList();
        var archived = s.Projects.Count - active.Count;
        ProjectsCount.Text = s.Projects.Count == 0 ? "" : $"{active.Count} active" + (archived > 0 ? $" · {archived} archived" : "");
        ProjectsEmpty.Visibility = Vis(s.Projects.Count == 0);
        NewProjectButton.IsEnabled = !s.TurnActive;
        BackupButton.IsEnabled = !s.TurnActive;
        if (signature == _projectsSignature) return;
        _projectsSignature = signature;

        ProjectItems.Children.Clear();
        foreach (var p in s.Projects.OrderBy(p => p.Status == "active" ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = p.Name, FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"  {p.Slug} · {p.Status}" + (p.FolderPresent ? "" : " · FOLDER MISSING"), Foreground = p.FolderPresent ? Secondary() : Res("SystemFillColorCriticalBrush"), FontSize = 12 });
            row.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (p.FolderPresent) buttons.Children.Add(Button("Open", () => OpenInExplorer(p.RootPath), small: true));
            if (p.Status == "active")
            {
                buttons.Children.Add(Button("Rename…", async () => await ShowRenameProjectDialogAsync(p), small: true, enabled: !s.TurnActive));
                buttons.Children.Add(Button("Archive", () => _coordinator!.ArchiveProject(p.Id), small: true, enabled: !s.TurnActive));
            }
            else buttons.Children.Add(Button("Restore", () => _coordinator!.RestoreProject(p.Id), small: true, enabled: !s.TurnActive));
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            ProjectItems.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------------------------
    // Dialogs
    // ------------------------------------------------------------------------------------

    private ContentDialog Dialog(string title, UIElement content, string primary, string secondary = "Cancel")
        => new()
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = secondary,
            DefaultButton = ContentDialogButton.Primary,
        };

    private async Task ShowNewProjectDialogAsync()
    {
        if (_coordinator is null) return;
        var name = new TextBox { PlaceholderText = "Project name", Header = "Name" };
        var slug = new TextBox { PlaceholderText = "derived from the name when empty", Header = "Folder slug (optional)" };
        var known = (_snapshot?.Workspaces ?? []).Where(w => w.Present).Select(w => w.Path).ToList();
        var folder = new TextBox { Header = "Create in folder", Text = known.FirstOrDefault() ?? "", PlaceholderText = @"C:\Users\you\Projects", HorizontalAlignment = HorizontalAlignment.Stretch };
        var browse = Button("Browse…", async () =>
        {
            var picker = new global::Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add("*");
            var picked = await picker.PickSingleFolderAsync();
            if (picked is not null) folder.Text = picked.Path;
        });
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(folder);
        browse.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(name);
        panel.Children.Add(slug);
        panel.Children.Add(row);
        if (known.Count > 1)
        {
            var others = new ComboBox { Header = "Folders already in use", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "pick one to reuse" };
            foreach (var path in known) others.Items.Add(new ComboBoxItem { Content = path, Tag = path });
            others.SelectionChanged += (_, _) => { if ((others.SelectedItem as ComboBoxItem)?.Tag is string path) folder.Text = path; };
            panel.Children.Add(others);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "The project folder is created inside the folder you choose. A folder Relay has not used before is registered as a project folder first (recorded in the ledger); Relay never writes outside registered folders and its own data root. Creating the project is a controlled write: it becomes a proposal you approve in Response.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Secondary(),
        });
        var dialog = Dialog("New project", panel, "Propose");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
        _coordinator.CreateProjectIn(folder.Text.Trim(), name.Text.Trim(), string.IsNullOrWhiteSpace(slug.Text) ? null : slug.Text.Trim());
    }

    private async Task ShowRenameProjectDialogAsync(ProjectView project)
    {
        if (_coordinator is null) return;
        var name = new TextBox { Text = project.Name, Header = "New name" };
        var dialog = Dialog($"Rename “{project.Name}”", name, "Propose");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text) || name.Text.Trim() == project.Name) return;
        _coordinator.RenameProject(project.Id, name.Text.Trim());
    }

    private async Task ShowChooseProjectDialogAsync(string title, Action<string> choose)
    {
        var projects = (_snapshot?.Projects ?? []).Where(p => p.Status == "active").OrderBy(p => p.Name).ToList();
        if (projects.Count == 0) return;
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Project" };
        foreach (var p in projects) combo.Items.Add(new ComboBoxItem { Content = $"{p.Name} ({p.Slug})", Tag = p.Id });
        combo.SelectedIndex = 0;
        var dialog = Dialog(title, combo, "File note");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if ((combo.SelectedItem as ComboBoxItem)?.Tag is string id) choose(id);
    }

    private async Task ShowEditProposalDialogAsync(ProposalView p)
    {
        if (_coordinator is null) return;
        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = "Change the target of this proposal. Policy re-evaluates the edited version and it still needs your approval.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Secondary() });
        var boxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        foreach (var (key, value) in p.Target.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var box = new TextBox { Header = key, Text = value, IsReadOnly = key is "projectId" or "noteId" or "runId" or "captureId" };
            boxes[key] = box;
            panel.Children.Add(box);
        }
        var dialog = Dialog($"Edit {p.Action}", new ScrollViewer { Content = panel, MaxHeight = 480 }, "Save edit");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var edited = boxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text, StringComparer.Ordinal);
        if (edited.Any(kv => p.Target.GetValueOrDefault(kv.Key) != kv.Value)) _coordinator.EditProposal(p.ProposalId, edited);
    }

    private async Task ShowSettingsDialogAsync()
    {
        if (_coordinator is null) return;
        var current = _coordinator.CurrentSettings;
        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };

        var mode = new ComboBox { Header = "Planner", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, label) in new[]
        {
            (OrchestratorSettings.Mind, "Mind — one local model runs every task and reads every conversation (needs the model)"),
            (OrchestratorSettings.Off, "Off — instructions are recorded only"),
            (OrchestratorSettings.Rules, "Rules — deterministic command grammar, no model (being removed)"),
            (OrchestratorSettings.RulesAndModel, "Rules + model — grammar first, RELAY0's model for the rest (being removed)"),
        })
            mode.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        mode.SelectedIndex = current.Orchestrator.Mode switch { OrchestratorSettings.Off => 1, OrchestratorSettings.Rules => 2, OrchestratorSettings.RulesAndModel => 3, _ => 0 };
        panel.Children.Add(mode);

        var judge = new ComboBox { Header = "Judge (what Ctrl+Alt does)", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, label) in new[] { (JudgeSettings.Off, "Off — Ctrl+Alt dictates a silent note, nothing is judged"), (JudgeSettings.Heuristic, "Heuristic — listen with the labeled rule-based judge, no model"), (JudgeSettings.Model, "Model — listen with RELAY0's model; the heuristic takes any pass the model misses") })
            judge.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        judge.SelectedIndex = current.Judge.Mode switch { JudgeSettings.Off => 0, JudgeSettings.Heuristic => 1, _ => 2 };
        panel.Children.Add(judge);
        var minConfidence = new NumberBox { Header = "Judge: act on findings at confidence ≥", Value = current.Judge.MinConfidence, Minimum = 0, Maximum = 1, SmallChange = 0.05, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        panel.Children.Add(minConfidence);

        var modelEnabled = new ToggleSwitch { Header = "RELAY0 model gateway", IsOn = current.Model.Enabled, OnContent = "enabled — one endpoint (loopback http or https), no other network", OffContent = "disabled — no network at all; grammar and heuristic judge only" };
        var endpoint = new TextBox { Header = "Endpoint (OpenAI-compatible chat completions; http only on 127.0.0.1 / localhost)", Text = current.Model.Endpoint };
        var modelName = new TextBox { Header = "Model", Text = current.Model.Model };
        var key = new PasswordBox { Header = _coordinator.ModelKeyStored ? "API key (stored · DPAPI, this Windows account) — enter a new one to replace" : "API key (not stored — a local llama.cpp server needs none)", PlaceholderText = "sk-…" };
        var removeKey = new CheckBox { Content = "Remove the stored key", IsEnabled = _coordinator.ModelKeyStored };
        panel.Children.Add(modelEnabled);
        panel.Children.Add(endpoint);
        panel.Children.Add(modelName);
        panel.Children.Add(key);
        panel.Children.Add(removeKey);

        var workers = new ToggleSwitch { Header = "Worker agents", IsOn = current.Workers.Enabled, OnContent = "enabled — sandboxed child processes, approval per run", OffContent = "disabled — launch_worker is denied" };
        panel.Children.Add(workers);

        var auto = new NumberBox { Header = "Auto-file notes at confidence ≥", Value = current.Orchestrator.AutoRouteThreshold, Minimum = 0.5, Maximum = 1, SmallChange = 0.05, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var review = new NumberBox { Header = "Ask in Review at confidence ≥", Value = current.Orchestrator.ReviewThreshold, Minimum = 0, Maximum = 1, SmallChange = 0.05, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        panel.Children.Add(auto);
        panel.Children.Add(review);
        var scopeText = current.Hotkeys.IsWindowScoped ? "active only while this window is focused" : "registered system-wide";
        panel.Children.Add(new TextBlock { Text = $"Chords: {current.Hotkeys.NoteKey} to listen (or dictate a note when the judge is off), {current.Hotkeys.CommandKey} for an instruction ({scopeText}). Chords, capture timing and the stream window are edited in settings.json and need a restart; preferences (response style, pinned terms, grants, retention) change through approved change sets. Everything here applies to the next task or judge pass.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Secondary() });

        var dialog = Dialog("Settings", new ScrollViewer { Content = panel, MaxHeight = 560 }, "Save");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (removeKey.IsChecked == true) _coordinator.SetModelApiKey(null);
        else if (!string.IsNullOrWhiteSpace(key.Password)) _coordinator.SetModelApiKey(key.Password);

        _coordinator.UpdateSettings(s =>
        {
            s.Orchestrator.Mode = (mode.SelectedItem as ComboBoxItem)?.Tag as string ?? s.Orchestrator.Mode;
            s.Judge.Mode = (judge.SelectedItem as ComboBoxItem)?.Tag as string ?? s.Judge.Mode;
            if (!double.IsNaN(minConfidence.Value)) s.Judge.MinConfidence = Math.Round(minConfidence.Value, 2);
            s.Model.Enabled = modelEnabled.IsOn;
            s.Model.Endpoint = endpoint.Text.Trim();
            s.Model.Model = modelName.Text.Trim();
            s.Workers.Enabled = workers.IsOn;
            if (!double.IsNaN(auto.Value)) s.Orchestrator.AutoRouteThreshold = Math.Round(auto.Value, 2);
            if (!double.IsNaN(review.Value)) s.Orchestrator.ReviewThreshold = Math.Round(review.Value, 2);
        });
    }

    // ------------------------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------------------------

    private Border Chip(string text, string? textStyle = null, string? status = null)
    {
        var chip = new Border { Style = (Style)RootGrid.Resources["Chip"], VerticalAlignment = VerticalAlignment.Center };
        var block = new TextBlock { Text = text, FontSize = 11 };
        if (textStyle is not null) block.Style = (Style)RootGrid.Resources[textStyle];
        if (status is not null)
            block.Foreground = status switch
            {
                "pending" => Res("AccentTextFillColorPrimaryBrush"),
                "executed" or "allowed" => Res("SystemFillColorSuccessBrush"),
                "denied" or "failed" => Res("SystemFillColorCriticalBrush"),
                _ => Secondary(),
            };
        chip.Child = block;
        return chip;
    }

    private string? NotePath(string? projectId, string noteId)
    {
        if (projectId is null || _runtime is null) return null;
        var project = _runtime.Services.Registry.ById(projectId);
        if (project is null || !Directory.Exists(project.RootPath)) return null;
        return ProjectNoteStore.Find(project.RootPath, noteId)?.Path;
    }

    private static string Trim(string text, int max)
    {
        var flat = text.Replace("\r", "").Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
