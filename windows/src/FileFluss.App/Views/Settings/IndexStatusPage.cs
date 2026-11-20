// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FileFluss.App.Services;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Models;
using FileFluss.Core.Search;
using static FileFluss.App.Views.Settings.SettingsKit;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Settings;

/// <summary>
/// Settings → Index Status (macOS <c>IndexStatusSettingsView</c>): every indexed drive and
/// account with when it was indexed and how much, a re-index button per source, Refresh All
/// and Erase All Indexes. Updates itself while indexing runs.
/// </summary>
internal sealed class IndexStatusPage : ContentControl
{
    private readonly AppServices _services;
    private readonly StackPanel _rows = new();
    private readonly Button _refreshAll;
    private readonly Button _eraseAll;

    /// <summary>The running jobs' rows listen to them; every reload (and leaving the page) lets go.</summary>
    private readonly List<Action> _jobSubscriptions = [];

    public IndexStatusPage(AppServices services)
    {
        _services = services;
        _refreshAll = SettingsKit.Button(L("Refresh All"), RefreshAll);
        _eraseAll = SettingsKit.Button(L("Erase All Indexes"), EraseAll);
        _eraseAll.SetResourceReference(ForegroundProperty, "DangerBrush");

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_eraseAll, Dock.Right);
        footer.Children.Add(_eraseAll);
        footer.Children.Add(new Border { Child = _refreshAll, HorizontalAlignment = HorizontalAlignment.Left });

        Content = new StackPanel { Children = { _rows, footer } };

        var indexing = services.Search.Indexing;
        EventHandler<IndexingFinishedEventArgs> finished = (_, _) => Load();
        System.Collections.Specialized.NotifyCollectionChangedEventHandler jobs = (_, _) => Load();
        Loaded += (_, _) =>
        {
            indexing.IndexingFinished += finished;
            indexing.Jobs.CollectionChanged += jobs;
            Load();
        };
        Unloaded += (_, _) =>
        {
            indexing.IndexingFinished -= finished;
            indexing.Jobs.CollectionChanged -= jobs;
            ReleaseJobs();
        };
    }

    private void ReleaseJobs()
    {
        foreach (var unsubscribe in _jobSubscriptions)
        {
            unsubscribe();
        }

        _jobSubscriptions.Clear();
    }

    private void Load()
    {
        var search = _services.Search;
        ReleaseJobs();
        var entries = search.Safe(search.Index.GetIndexStatus) ?? [];
        _rows.Children.Clear();
        if (entries.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 40, 0, 40), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 380 };
            var icon = Glyph(Glyphs.Search, 34);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 0, 12);
            icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            empty.Children.Add(icon);
            empty.Children.Add(new TextBlock { Text = L("No Indexed Sources"), FontSize = 16, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center });
            var caption = Caption(L("Index a drive or cloud account from its sidebar context menu to make its contents searchable while offline."));
            caption.TextAlignment = TextAlignment.Center;
            empty.Children.Add(caption);
            _rows.Children.Add(empty);
        }

        foreach (var entry in entries)
        {
            _rows.Children.Add(Row(entry));
        }

        _refreshAll.IsEnabled = entries.Any(CanRefresh);
        _eraseAll.IsEnabled = entries.Count > 0;
    }

    private Border Row(IndexStatusEntry entry)
    {
        var account = entry.AccountId is { } id ? _services.Accounts.Find(id) : null;

        // A renamed account shows its current name; a removed one keeps the name it was indexed under.
        var name = account?.DisplayName ?? entry.DisplayName;
        var kind = entry.IsCloud
            ? account?.ProviderType.DisplayName() ?? L("Cloud")
            : (entry.DriveKind ?? DriveKind.External).DisplayName();
        var when = entry.LastIndexed is { } date
            ? L("Indexed {0}", date.LocalDateTime.ToString("g", Culture))
            : L("Never indexed");

        FrameworkElement icon = account is not null && IconCache.Logo(account.ProviderType) is { } logo
            ? new Image { Source = logo, Width = 20, Height = 20 }
            : Glyph(entry.IsCloud ? Glyphs.Cloud : entry.DriveKind == DriveKind.Network ? Glyphs.NetworkDrive : Glyphs.HardDrive, 18);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = Caption($"{kind} · {when}");
        detail.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(detail);

        var counts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        counts.Children.Add(new TextBlock { Text = L("{0} files", entry.TotalFiles.ToString("N0", Culture)), HorizontalAlignment = HorizontalAlignment.Right });
        var folders = Caption(L("{0} folders", entry.TotalFolders.ToString("N0", Culture)));
        folders.HorizontalAlignment = HorizontalAlignment.Right;
        folders.Margin = new Thickness(0);
        counts.Children.Add(folders);

        FrameworkElement action;
        if (_services.Search.Indexing.ActiveJob(entry.SourceId) is { } job)
        {
            var progress = new StackPanel { Width = 96, VerticalAlignment = VerticalAlignment.Center };
            progress.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4 });
            var processed = Caption(job.FilesProcessed.ToString("N0", Culture));
            processed.HorizontalAlignment = HorizontalAlignment.Center;
            System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => processed.Text = job.FilesProcessed.ToString("N0", Culture);
            job.PropertyChanged += changed;
            _jobSubscriptions.Add(() => job.PropertyChanged -= changed);
            progress.Children.Add(processed);
            action = progress;
        }
        else
        {
            var refresh = new Button
            {
                Content = Glyph(Glyphs.Refresh, 14),
                ToolTip = L("Re-index this source"),
                IsEnabled = CanRefresh(entry),
                Width = 36,
                Height = 32,
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(refresh, L("Re-index this source") + " — " + name);
            refresh.Click += (_, _) => Refresh(entry);
            action = refresh;
        }

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(action, Dock.Right);
        DockPanel.SetDock(counts, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(action);
        row.Children.Add(counts);
        row.Children.Add(text);
        return Wrap(row);
    }

    private bool CanRefresh(IndexStatusEntry entry)
    {
        if (entry.AccountId is { } id)
        {
            return _services.Accounts.Find(id) is { IsConnected: true, IsOfflineMode: false } && _services.Hub.Get(id) is not null;
        }

        return _services.Search.Drives.IsOnline(entry.SourceId);
    }

    private void Refresh(IndexStatusEntry entry)
    {
        var indexing = _services.Search.Indexing;
        if (entry.AccountId is { } id && _services.Accounts.Find(id) is { } account)
        {
            indexing.IndexCloudAccount(account);
        }
        else
        {
            indexing.IndexDrive(entry.SourceId);
        }
    }

    private void RefreshAll()
    {
        foreach (var entry in (_services.Search.Safe(_services.Search.Index.GetIndexStatus) ?? []).Where(CanRefresh))
        {
            Refresh(entry);
        }
    }

    private void EraseAll()
    {
        if (!MessageDialog.Confirm(Window.GetWindow(this), L("Erase all offline indexes?"),
                L("This removes the offline search index for every drive and cloud account. Indexing can be redone later."),
                L("Erase Everything"), destructive: true))
        {
            return;
        }

        var search = _services.Search;
        foreach (var job in search.Indexing.Jobs.Where(j => !j.IsComplete).ToList())
        {
            job.Cancel();
        }

        search.Safe(() =>
        {
            search.Index.WipeAll();
            search.Drives.ClearIndexedState();
        });

        // Accounts browsed offline have nothing left to show.
        foreach (var account in _services.Accounts.Accounts.Where(a => a.IsOfflineMode).ToList())
        {
            _services.Accounts.SetOfflineMode(account.Id, false);
        }

        Load();
    }
}
