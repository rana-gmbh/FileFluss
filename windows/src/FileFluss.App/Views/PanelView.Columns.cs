// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// The file list's columns (macOS <c>FileListColumnPrefs</c> and the header menus in
/// <c>NativeFileList</c> / <c>NativeCloudFileList</c>): a right-click on the header toggles
/// Date Modified / Date Created / Size / Kind, separately for local and cloud panels, and
/// "Auto Resize" on the Name column. Widths and order (columns can be dragged) persist.
/// </summary>
public partial class PanelView
{
    private static readonly Dictionary<FileListColumn, double> DefaultWidths = new()
    {
        [FileListColumn.Name] = 280,
        [FileListColumn.DateModified] = 128,
        [FileListColumn.DateCreated] = 128,
        [FileListColumn.Size] = 78,
        [FileListColumn.Kind] = 120,
    };

    private bool _applyingColumns;
    private bool? _columnsForCloud;
    private bool _settingsHooked;

    /// <summary>A cloud account or an offline source uses the cloud column set.</summary>
    private bool ColumnsForCloud => Vm is { IsLocal: false };

    private IEnumerable<(FileListColumn Id, GridViewColumn Column)> AllColumns =>
    [
        (FileListColumn.Name, NameColumn),
        (FileListColumn.DateModified, DateColumn),
        (FileListColumn.DateCreated, CreatedColumn),
        (FileListColumn.Size, SizeColumn),
        (FileListColumn.Kind, KindColumn),
    ];

    private GridViewColumn ColumnFor(FileListColumn id) => AllColumns.First(c => c.Id == id).Column;

    private FileListColumn IdOf(GridViewColumn column) => AllColumns.First(c => c.Column == column).Id;

    private void InitializeColumns()
    {
        // The Name column fills what the others leave, so it follows their widths.
        var widthProperty = DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
        foreach (var (id, column) in AllColumns)
        {
            if (id != FileListColumn.Name)
            {
                widthProperty.AddValueChanged(column, (_, _) => FitNameColumn());
            }
        }

        Grid.Columns.CollectionChanged += OnColumnsReordered;
        List.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnColumnResized), handledEventsToo: true);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PanelViewModel old)
            {
                old.PropertyChanged -= OnPanelKindChanged;
            }

            if (e.NewValue is PanelViewModel vm)
            {
                vm.PropertyChanged += OnPanelKindChanged;
            }

            ApplyColumns();
        };
        Loaded += (_, _) =>
        {
            if (!_settingsHooked && Main is not null)
            {
                _settingsHooked = true;
                Main.Services.Settings.PropertyChanged += OnColumnSettingsChanged;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            ThumbnailCache.PixelSize = Math.Max(32, (int)Math.Ceiling(16 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
            ApplyColumns();
        };
    }

    private void OnPanelKindChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.IsLocal))
        {
            ApplyColumns();
        }
    }

    private void OnColumnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Core.Settings.AppSettings.LocalColumns) or nameof(Core.Settings.AppSettings.CloudColumns)
            or nameof(Core.Settings.AppSettings.NameAutoResize))
        {
            Dispatcher.BeginInvoke(ApplyColumns);
        }
    }

    /// <summary>Shows the stored column set for this panel's kind; widths are restored when the kind changes.</summary>
    private void ApplyColumns()
    {
        if (Main is null || Vm is null)
        {
            return;
        }

        var settings = Main.Services.Settings;
        var cloud = ColumnsForCloud;
        var wanted = FileListColumns.Visible(cloud ? settings.CloudColumns : settings.LocalColumns, cloud);
        if (_columnsForCloud != cloud)
        {
            _columnsForCloud = cloud;
            foreach (var (id, column) in AllColumns)
            {
                column.Width = settings.ColumnWidths.TryGetValue(FileListColumns.WidthKey(cloud, id), out var width) && width >= 24
                    ? width
                    : DefaultWidths[id];
            }
        }

        if (!Grid.Columns.Select(IdOf).SequenceEqual(wanted))
        {
            _applyingColumns = true;
            try
            {
                // Remove, move and insert rather than rebuild, so the headers stay put.
                foreach (var column in Grid.Columns.Where(c => !wanted.Contains(IdOf(c))).ToList())
                {
                    Grid.Columns.Remove(column);
                }

                for (var i = 0; i < wanted.Count; i++)
                {
                    var column = ColumnFor(wanted[i]);
                    var index = Grid.Columns.IndexOf(column);
                    if (index < 0)
                    {
                        Grid.Columns.Insert(i, column);
                    }
                    else if (index != i)
                    {
                        Grid.Columns.Move(index, i);
                    }
                }
            }
            finally
            {
                _applyingColumns = false;
            }
        }

        // Without Auto Resize the Name column keeps the user's width, which can overflow the panel.
        ScrollViewer.SetHorizontalScrollBarVisibility(List, settings.NameAutoResize ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        FitNameColumn();
        UpdateHeaders();
    }

    /// <summary>
    /// With "Auto Resize" on (the default) the Name column takes whatever the visible columns
    /// leave, so everything fits without a horizontal scroll bar.
    /// </summary>
    private void FitNameColumn()
    {
        if (Main?.Services.Settings.NameAutoResize == false || List.ActualWidth <= 0)
        {
            return;
        }

        var others = Grid.Columns.Where(c => c != NameColumn).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        var available = List.ActualWidth - others - SystemParameters.VerticalScrollBarWidth - 26;
        NameColumn.Width = Math.Max(140, available);
    }

    // MARK: - Header menu

    /// <summary>The header's context menu, as on macOS: the panel kind's columns, plus "Auto Resize" on Name.</summary>
    private void BuildHeaderMenu(GridViewColumnHeader header)
    {
        HeaderMenu.Items.Clear();
        if (Main is null || Vm is null)
        {
            return;
        }

        var settings = Main.Services.Settings;
        var cloud = ColumnsForCloud;
        var visible = FileListColumns.Visible(cloud ? settings.CloudColumns : settings.LocalColumns, cloud);
        foreach (var id in FileListColumns.Toggleable(cloud))
        {
            var item = new MenuItem { Header = FileListColumns.Title(id), IsCheckable = true, IsChecked = visible.Contains(id) };
            item.Click += (_, _) => ToggleColumn(id);
            HeaderMenu.Items.Add(item);
        }

        if (header.Column == NameColumn)
        {
            HeaderMenu.Items.Add(new Separator());
            var auto = new MenuItem { Header = L("Auto Resize"), IsCheckable = true, IsChecked = settings.NameAutoResize };
            auto.Click += (_, _) =>
            {
                settings.NameAutoResize = !settings.NameAutoResize;
                if (!settings.NameAutoResize)
                {
                    SaveColumnWidths();
                }
            };
            HeaderMenu.Items.Add(auto);
        }
    }

    private void ToggleColumn(FileListColumn id)
    {
        var settings = Main.Services.Settings;
        var cloud = ColumnsForCloud;
        if (cloud)
        {
            settings.CloudColumns = FileListColumns.Toggle(settings.CloudColumns, id, cloud);
        }
        else
        {
            settings.LocalColumns = FileListColumns.Toggle(settings.LocalColumns, id, cloud);
        }
    }

    // MARK: - Widths and order

    private void OnColumnResized(object sender, DragCompletedEventArgs e)
    {
        if (e.OriginalSource is not Thumb { TemplatedParent: GridViewColumnHeader { Column: { } column } })
        {
            return;
        }

        SaveColumnWidths();
        if (column == NameColumn)
        {
            FitNameColumn();
        }
    }

    /// <summary>Remembers the visible columns' widths for this panel kind (Name only when it isn't auto-sized).</summary>
    private void SaveColumnWidths()
    {
        if (Main is null || Vm is null)
        {
            return;
        }

        var settings = Main.Services.Settings;
        var cloud = ColumnsForCloud;
        var widths = new Dictionary<string, double>(settings.ColumnWidths);
        foreach (var column in Grid.Columns)
        {
            var id = IdOf(column);
            if (id == FileListColumn.Name && settings.NameAutoResize)
            {
                continue;
            }

            widths[FileListColumns.WidthKey(cloud, id)] = Math.Round(double.IsNaN(column.Width) ? column.ActualWidth : column.Width);
        }

        if (!widths.OrderBy(p => p.Key).SequenceEqual(settings.ColumnWidths.OrderBy(p => p.Key)))
        {
            settings.ColumnWidths = widths;
        }
    }

    /// <summary>A column dragged to a new place: remember the order for this panel kind.</summary>
    private void OnColumnsReordered(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_applyingColumns || e.Action != NotifyCollectionChangedAction.Move || Main is null || Vm is null)
        {
            return;
        }

        var order = Grid.Columns.Select(IdOf).ToList();
        if (ColumnsForCloud)
        {
            Main.Services.Settings.CloudColumns = order;
        }
        else
        {
            Main.Services.Settings.LocalColumns = order;
        }
    }

    // MARK: - Quick filter

    private void OnClearFilter(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.FilterText = string.Empty;
            FilterBox.Focus();
        }
    }

    private void OnCloseFilter(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.IsFilterVisible = false;
            FocusList();
        }
    }
}
