using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Relay.Core.Attention;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Tasks;
using TaskStatus = Relay.Core.Tasks.TaskStatus;
using FontStyle = Windows.UI.Text.FontStyle;

namespace Relay.Desktop;

/// <summary>
/// The chronological feed: the mind's sentences, inline approvals, and expandable evidence —
/// built from Tasks, Attention and Activity over the unchanged snapshot.
/// </summary>
public sealed partial class MainWindow
{
    private string _feedSignature = "";
    private string? _openDrawer;

    private void RenderFeed(RelaySnapshot s)
    {
        var signature = BuildFeedSignature(s);
        FeedEmpty.Visibility = Vis(s.Tasks.Count == 0 && s.Attention.Count == 0 && !s.TurnActive && s.Response is null);
        if (signature == _feedSignature) return;
        _feedSignature = signature;

        FeedItems.Children.Clear();
        var shownTaskIds = new HashSet<string>(StringComparer.Ordinal);

        // Foreground response / live turn first when active, then attention cards, then recent finished tasks.
        if (s.Response is { } foreground)
        {
            FeedItems.Children.Add(TaskFeedCard(foreground, s, highlight: true));
            shownTaskIds.Add(foreground.TaskId);
        }

        foreach (var item in s.Attention.Where(i => i.Level != Presentation.Ambient))
        {
            if (item.TaskId is { } tid && shownTaskIds.Contains(tid))
            {
                // Already rendered as the task card; skip duplicate attention shell unless it needs action buttons beyond proposals.
                continue;
            }
            FeedItems.Children.Add(AttentionCard(item, s));
            if (item.TaskId is { } id) shownTaskIds.Add(id);
        }

        foreach (var task in s.Tasks
                     .Where(t => !shownTaskIds.Contains(t.TaskId))
                     .OrderByDescending(t => t.Live)
                     .ThenByDescending(t => t.StartedAt)
                     .Take(12))
        {
            // Skip quiet completed tasks that the arbiter chose not to surface and that have no answer.
            if (!task.Live && task.Presentation == Presentation.None && string.IsNullOrWhiteSpace(task.Answer) && task.Proposals.Count == 0)
                continue;
            FeedItems.Children.Add(TaskFeedCard(task, s, highlight: false));
            shownTaskIds.Add(task.TaskId);
        }

        var ambient = s.Attention.Where(i => i.Level == Presentation.Ambient).ToList();
        if (ambient.Count > 0)
        {
            foreach (var item in ambient.Take(8))
                FeedItems.Children.Add(AmbientRow(item));
        }

        AnimateFeedEntrance();
        if (FeedItems.Children.Count > 0)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (FeedItems.Children.Count > 0)
                    FeedItems.Children[^1].StartBringIntoView();
            });
    }

    private static string BuildFeedSignature(RelaySnapshot s)
    {
        var tasks = string.Join("|", s.Tasks.Select(t =>
            $"{t.TaskId}:{t.Status}:{t.Steps.Count}:{t.Answer?.Length}:{t.Summary}:{t.Consistent}:{string.Join(",", t.Proposals.Select(p => p.ProposalId + p.Status + p.BlockedBy))}:{t.Citations.Count}:{t.Presentation}:{t.UserResponse}"));
        var attention = string.Join("|", s.Attention.Select(i => $"{i.ItemId}:{i.Level}:{i.Occurrences}:{i.Title.Length}:{i.Detail.Length}:{i.NeedsAction}"));
        return tasks + "#" + attention + "#" + s.State + "#" + (s.Response?.TaskId ?? "");
    }

    private Border TaskFeedCard(TaskView t, RelaySnapshot s, bool highlight)
    {
        var panel = new StackPanel { Spacing = 8 };

        var header = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };
        if (t.Presentation is not Presentation.None and not Presentation.Ambient)
            header.Children.Add(LevelChip(t.Presentation));
        else if (t.Live)
            header.Children.Add(Chip(t.Tag.ToUpperInvariant(), "Mono", status: t.Status == TaskStatus.AwaitingApproval ? "pending" : "pending"));
        var title = Trim(t.Title ?? t.Instruction, 120);
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true });
        header.Children.Add(Chip($"{t.Kind.Wire()} · {OriginText(t.Origin)}", "Mono"));
        if (t.Consistent is { } consistent)
            header.Children.Add(Chip(consistent ? "consistent with stored facts" : "conflicts with stored facts", status: consistent ? "executed" : "denied"));
        if (t.Cost.ModelCalls > 0 || t.Cost.ToolCalls > 0)
            header.Children.Add(Chip(CostText(t.Cost), "Mono"));
        panel.Children.Add(header);

        if (!string.Equals(t.Instruction, title, StringComparison.Ordinal) && t.Origin == TaskOrigin.Direct)
            panel.Children.Add(new TextBlock { Text = "“" + Trim(t.Instruction, 240) + "”", Style = (Style)RootGrid.Resources["Secondary"], FontStyle = FontStyle.Italic });

        if (!string.IsNullOrWhiteSpace(t.Summary) && t.Summary != title)
            panel.Children.Add(new TextBlock { Text = t.Summary, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        // Mind feed sentences (turn progress).
        foreach (var step in t.Steps)
            panel.Children.Add(new TextBlock
            {
                Text = step,
                FontSize = 13,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

        if (!string.IsNullOrWhiteSpace(t.Answer))
        {
            panel.Children.Add(new Border
            {
                Background = Res("SubtleFillColorSecondaryBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Child = new TextBlock { Text = t.Answer, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13, LineHeight = 20 },
            });
        }

        if (!t.Knowledge.IsEmpty)
            panel.Children.Add(new TextBlock { Text = KnowledgeText(t.Knowledge), Style = (Style)RootGrid.Resources["Secondary"], IsTextSelectionEnabled = true });

        if (t.Citations.Count > 0)
            panel.Children.Add(EvidenceExpander(t));

        // Inline approvals where they occur.
        var pendingOrDenied = t.Proposals.Where(p => p.Status is "pending" or "denied" || (t.Live && p.Status is "executed" or "failed")).ToList();
        if (t.Proposals.Count > 0 && (t.Live || t.Status == TaskStatus.AwaitingApproval || pendingOrDenied.Count > 0))
        {
            foreach (var p in t.Proposals.Where(p => p.Status is "pending" or "denied" || t.Live))
                panel.Children.Add(ProposalCard(p, t, compact: !highlight));
        }
        else if (!t.Live && t.Proposals.Count > 0)
        {
            var ran = t.Proposals.Count(p => p.Status == "executed");
            panel.Children.Add(new TextBlock { Text = $"{ran}/{t.Proposals.Count} proposal(s) ran", FontSize = 11, Foreground = Secondary() });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var approvable = t.Proposals.Count(p => p.Status == "pending" && p.BlockedBy is null);
        if (approvable > 1 && t.Status == TaskStatus.AwaitingApproval)
            buttons.Children.Add(Button($"Approve all ({approvable})", () => _coordinator!.ApproveAll(t.TaskId), accent: true));
        if (t.Status == TaskStatus.Executing)
            buttons.Children.Add(Button("Stop", () => _coordinator!.CancelTask(t.TaskId)));
        else if (t.Live && t.Status is TaskStatus.Planning or TaskStatus.AwaitingApproval)
            buttons.Children.Add(Button("Cancel task", () => _coordinator!.CancelTask(t.TaskId)));
        buttons.Children.Add(Button("Details", () => ShowTaskDiagnostics(t.TaskId), small: true));
        panel.Children.Add(buttons);

        var stripe = t.Status switch
        {
            TaskStatus.AwaitingApproval => Res("AccentFillColorDefaultBrush"),
            TaskStatus.Failed => Res("SystemFillColorCriticalBrush"),
            TaskStatus.Executing => new SolidColorBrush(Palette.Good),
            _ when t.Consistent == false => Res("SystemFillColorCriticalBrush"),
            _ => highlight ? Res("AccentFillColorDefaultBrush") : null,
        };
        return SubCard(panel, stripe);
    }

    private Expander EvidenceExpander(TaskView t)
    {
        var body = new StackPanel { Spacing = 6 };
        var n = 1;
        foreach (var c in t.Citations)
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
            if (c.Kind == Relay.Core.Search.SearchIndex.NoteKind && NotePath(c.ProjectId, c.Id) is { } path)
            {
                var open = Button("Open", () => OpenInExplorer(path), small: true);
                Grid.SetColumn(open, 1);
                row.Children.Add(open);
            }
            body.Children.Add(row);
        }

        return new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = new TextBlock { Text = $"SOURCES ({t.Citations.Count})", Style = (Style)RootGrid.Resources["RegionHeader"] },
            Content = body,
        };
    }

    private void AnimateFeedEntrance()
    {
        // Soft fade on the feed stack when it rebuilds — one intentional motion, not per-card noise.
        var anim = new DoubleAnimation
        {
            From = 0.88,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, FeedItems);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    // ------------------------------------------------------------------------------------
    // Drawers
    // ------------------------------------------------------------------------------------

    private void DrawerTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tab || tab.Tag is not string id) return;
        if (_openDrawer == id && tab.IsChecked == true)
        {
            // already open — keep it
            SyncDrawerTabs(id);
            return;
        }
        if (tab.IsChecked != true)
        {
            if (_openDrawer == id) CloseDrawer();
            return;
        }
        OpenDrawer(id);
    }

    private void CloseDrawer_Click(object sender, RoutedEventArgs e) => CloseDrawer();

    private void OpenDrawer(string id)
    {
        _openDrawer = id;
        DrawerHost.Visibility = Visibility.Visible;
        DrawerColumn.Width = new GridLength(320);
        SyncDrawerTabs(id);
        ShowDrawerContent(id);
        if (id == "diagnostics" && _snapshot is not null) RenderDiagnostics();
        if (id == "ledger") ScrollActivityToEnd();

        // Slide-in feel via opacity.
        DrawerHost.Opacity = 0;
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, DrawerHost);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void CloseDrawer()
    {
        _openDrawer = null;
        DrawerHost.Visibility = Visibility.Collapsed;
        DrawerColumn.Width = new GridLength(0);
        SyncDrawerTabs(null);
        TaskDiagnosticsPanel.Visibility = Visibility.Collapsed;
    }

    private void SyncDrawerTabs(string? id)
    {
        ProjectsDrawerTab.IsChecked = id == "projects";
        ReviewDrawerTab.IsChecked = id == "review";
        TasksDrawerTab.IsChecked = id == "tasks";
        InboxDrawerTab.IsChecked = id == "inbox";
        LedgerDrawerTab.IsChecked = id == "ledger";
        PrefsDrawerTab.IsChecked = id == "prefs";
        DiagnosticsDrawerTab.IsChecked = id == "diagnostics";
    }

    private void ShowDrawerContent(string id)
    {
        ProjectsDrawer.Visibility = Vis(id == "projects");
        ReviewDrawer.Visibility = Vis(id == "review");
        TasksDrawer.Visibility = Vis(id == "tasks");
        InboxDrawer.Visibility = Vis(id == "inbox");
        LedgerDrawer.Visibility = Vis(id == "ledger");
        PrefsDrawer.Visibility = Vis(id == "prefs");
        DiagnosticsDrawer.Visibility = Vis(id == "diagnostics");

        DrawerTitle.Text = id switch
        {
            "projects" => "PROJECTS",
            "review" => "REVIEW",
            "tasks" => "TASKS",
            "inbox" => "INBOX",
            "ledger" => "LEDGER",
            "prefs" => "PREFERENCES",
            "diagnostics" => "DIAGNOSTICS",
            _ => "DRAWER",
        };
        RefreshDrawerMeta(_snapshot);
    }

    private void RefreshDrawerMeta(RelaySnapshot? s)
    {
        if (s is null || _openDrawer is null) { DrawerMeta.Text = ""; return; }
        DrawerMeta.Text = _openDrawer switch
        {
            "projects" => ProjectsCountText(s),
            "review" => s.Review.Count == 0 ? "" : $"{s.Review.Count} item(s)",
            "tasks" => TasksCountText(s),
            "inbox" => InboxCountText(s),
            "ledger" => $"{s.LedgerRecords} records · chain {Short(s.LedgerLastHash)}",
            "prefs" => s.ChangeSets.Count == 0 ? "defaults · no change sets yet" : $"{s.ChangeSets.Count} change set(s) · {s.ChangeSets.Count(c => !c.Reverted)} in effect",
            "diagnostics" => DiagnosticsMetaText(s),
            _ => "",
        };
    }

    private void UpdateDrawerTabLabels(RelaySnapshot s)
    {
        ProjectsDrawerTab.Content = string.IsNullOrEmpty(ProjectsCountText(s)) ? "Projects" : $"Projects · {ProjectsCountText(s)}";
        ReviewDrawerTab.Content = s.Review.Count == 0 ? "Review" : $"Review · {s.Review.Count}";
        TasksDrawerTab.Content = s.Tasks.Count == 0 ? "Tasks" : $"Tasks · {TasksCountText(s)}";
        InboxDrawerTab.Content = s.Inbox.Count == 0 ? "Inbox" : $"Inbox · {InboxCountText(s)}";
        LedgerDrawerTab.Content = "Ledger";
        PrefsDrawerTab.Content = "Preferences";
        RefreshDrawerMeta(s);
    }

    private static string ProjectsCountText(RelaySnapshot s)
    {
        if (s.Projects.Count == 0) return "";
        var active = s.Projects.Count(p => p.Status == "active");
        var archived = s.Projects.Count - active;
        return $"{active} active" + (archived > 0 ? $" · {archived} archived" : "");
    }

    private static string TasksCountText(RelaySnapshot s)
    {
        if (s.Tasks.Count == 0) return "";
        var live = s.Tasks.Count(t => t.Live);
        var cost = s.SessionCost;
        return $"{live} running · {s.Tasks.Count - live} finished" + (cost.TotalTokens > 0 ? $" · {cost.TotalTokens} tokens" : "");
    }

    private static string InboxCountText(RelaySnapshot s)
    {
        if (s.Inbox.Count == 0) return "";
        var asking = s.Inbox.Count(i => i.HasSuggestions);
        return $"{s.Inbox.Count} unrouted" + (asking > 0 ? $" · {asking} with suggestions" : "");
    }

    private string DiagnosticsMetaText(RelaySnapshot s)
    {
        var cost = s.SessionCost;
        return $"{s.Tasks.Count} task(s) · {cost.TotalTokens} tokens · {cost.ModelCalls} model call(s) · {cost.ToolCalls} tool call(s)";
    }
}
