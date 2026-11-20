// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.Core.Models;
using FileFluss.Core.Storage;
using static FileFluss.Core.Localization;

using Glyphs = FileFluss.App.Services.Glyphs;

namespace FileFluss.App.Views.Storage;

/// <summary>The rows of the Storage Analysis lists: the folder tree with share bars, and the largest files.</summary>
public sealed partial class StorageAnalysisWindow
{
    /// <summary>
    /// Compact rows with Explorer's hover and accent selection (the file list's look), instead
    /// of the Fluent theme's touch-sized list items.
    /// </summary>
    private static readonly Lazy<Style> RowStyle = new(() => (Style)XamlReader.Parse(
        """
        <Style TargetType="ListBoxItem"
               xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Setter Property="Padding" Value="0" />
            <Setter Property="Margin" Value="4,0" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                        <Border x:Name="Chrome" Background="Transparent" CornerRadius="4" BorderThickness="1" BorderBrush="Transparent" SnapsToDevicePixels="True">
                            <ContentPresenter VerticalAlignment="Center" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource RowSelectedInactiveBrush}" />
                            </Trigger>
                            <MultiTrigger>
                                <MultiTrigger.Conditions>
                                    <Condition Property="IsSelected" Value="True" />
                                    <Condition Property="Selector.IsSelectionActive" Value="True" />
                                </MultiTrigger.Conditions>
                                <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource RowSelectedBrush}" />
                            </MultiTrigger>
                            <Trigger Property="IsKeyboardFocused" Value="True">
                                <Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource RowFocusBrush}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """));

    /// <summary>What both lists hold: something with a node behind it.</summary>
    private interface IStorageListItem
    {
        StorageNode Node { get; }
    }

    /// <summary>A folder-tree row. The icon is looked up only when the row is realized on screen.</summary>
    private sealed class TreeRowItem(StorageRow row, ImageSource? rootIcon) : IStorageListItem
    {
        public StorageRow Row { get; } = row;

        public StorageNode Node => Row.Node;

        public ImageSource? Icon => rootIcon ?? (Node.IsDirectory ? IconCache.Folder() : FileIcon(Node));

        /// <summary>What a screen reader announces: "Videos, 1.90 GB, 42.0% of the total".</summary>
        public override string ToString() => $"{Node.Name}, {Row.SizeText}, {Row.ShareText}";
    }

    private sealed class LargestRowItem(StorageNode node, string folder, string sizeText, string shareText) : IStorageListItem
    {
        public StorageNode Node { get; } = node;

        public string Folder { get; } = folder;

        public string SizeText { get; } = sizeText;

        public string ShareText { get; } = shareText;

        public ImageSource? Icon => FileIcon(Node);

        public override string ToString() => $"{Node.Name}, {SizeText}, {Folder}";
    }

    /// <summary>By extension only — no disk access per row, even for local files.</summary>
    private static ImageSource? FileIcon(StorageNode node)
        => IconCache.For(new CloudFileItem(node.Path, node.Name, node.Path, false, node.Size, CloudFileItem.UnknownDate), isLocal: false);

    /// <summary>Names each row for UI Automation (the record's own ToString would read out its fields).</summary>
    private sealed class StorageList : ListBox
    {
        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (element is ListBoxItem container)
            {
                AutomationProperties.SetName(container, item?.ToString() ?? string.Empty);
            }
        }
    }

    /// <summary>One folder-tree row: indent, disclosure chevron, icon, name, share bar, size (macOS <c>StorageRow</c>).</summary>
    private sealed class TreeRowView : DockPanel
    {
        private readonly Button _chevron = new() { Width = 18, Height = 18, FontSize = 9, Padding = new Thickness(0), Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        private readonly Image _icon = new() { Width = 16, Height = 16, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly ShareBar _bar = new() { Margin = new Thickness(10, 0, 0, 0) };
        private readonly TextBlock _size = SizeText();

        public TreeRowView()
        {
            Height = 26;
            Margin = new Thickness(6, 0, 8, 0);
            Background = Brushes.Transparent;
            _chevron.SetResourceReference(StyleProperty, "GlyphButton");
            _chevron.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
            RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);

            SetDock(_size, Dock.Right);
            Children.Add(_size);
            SetDock(_bar, Dock.Right);
            Children.Add(_bar);
            SetDock(_chevron, Dock.Left);
            Children.Add(_chevron);
            SetDock(_icon, Dock.Left);
            Children.Add(_icon);
            Children.Add(_name);
            DataContextChanged += (_, _) => Update();
        }

        private void Update()
        {
            if (DataContext is not TreeRowItem item)
            {
                return;
            }

            var row = item.Row;
            _chevron.Margin = new Thickness(StorageLayout.Indent(row.Depth), 0, 0, 0);
            _chevron.Visibility = row.IsExpandable ? Visibility.Visible : Visibility.Hidden;
            _chevron.Content = row.IsExpanded ? Glyphs.ChevronDown : Glyphs.ChevronRight;
            AutomationProperties.SetName(_chevron, row.IsExpanded ? L("Collapse") : L("Expand"));
            _icon.Source = item.Icon;
            _name.Text = row.Node.Name;
            _name.ToolTip = row.Node.Path;
            _name.FontWeight = row.Depth == 0 ? FontWeights.SemiBold : FontWeights.Normal;
            _bar.Fraction = row.Fraction;
            _bar.ToolTip = row.ShareText;
            _size.Text = row.SizeText;
        }
    }

    /// <summary>One "Largest files" row: name over its folder, size on the right.</summary>
    private sealed class LargestRowView : DockPanel
    {
        private readonly Image _icon = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _folder = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5, Opacity = 0.8, Margin = new Thickness(0, 1, 0, 0) };
        private readonly TextBlock _size = SizeText();

        public LargestRowView()
        {
            Height = 40;
            Margin = new Thickness(8, 0, 8, 0);
            Background = Brushes.Transparent;
            _folder.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);

            SetDock(_size, Dock.Right);
            Children.Add(_size);
            SetDock(_icon, Dock.Left);
            Children.Add(_icon);
            Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _name, _folder } });
            DataContextChanged += (_, _) => Update();
        }

        private void Update()
        {
            if (DataContext is not LargestRowItem item)
            {
                return;
            }

            _icon.Source = item.Icon;
            _name.Text = item.Node.Name;
            _name.ToolTip = item.Node.Path;
            _folder.Text = item.Folder;
            _size.Text = item.SizeText;
            _size.ToolTip = item.ShareText;
        }
    }

    private static TextBlock SizeText()
    {
        var size = new TextBlock { Width = 84, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        size.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        System.Windows.Documents.Typography.SetNumeralAlignment(size, FontNumeralAlignment.Tabular);
        return size;
    }

    /// <summary>
    /// The share bar: a faint rounded track with the item's share filled in the accent colour,
    /// drawn directly (one element per row, no template). Taller than it looks so its tooltip
    /// ("12.5% of the total") is easy to hover.
    /// </summary>
    private sealed class ShareBar : FrameworkElement
    {
        public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
            nameof(Fraction), typeof(double), typeof(ShareBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(ShareBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
            nameof(Track), typeof(Brush), typeof(ShareBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private const double BarHeight = 6;
        private const double TrackOpacity = 0.15;

        public ShareBar()
        {
            Width = StorageLayout.BarTrackWidth;
            Height = 16;
            VerticalAlignment = VerticalAlignment.Center;
            SnapsToDevicePixels = true;

            // The accent as text colour: dark enough on light backgrounds, light enough on dark ones.
            SetResourceReference(FillProperty, "AccentTextBrush");
            SetResourceReference(TrackProperty, "TextFillColorPrimaryBrush");
        }

        public double Fraction
        {
            get => (double)GetValue(FractionProperty);
            set => SetValue(FractionProperty, value);
        }

        public Brush? Fill
        {
            get => (Brush?)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public Brush? Track
        {
            get => (Brush?)GetValue(TrackProperty);
            set => SetValue(TrackProperty, value);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = RenderSize;

            // Transparent, but hit-testable across the full height for the tooltip.
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(size));
            var top = Math.Round((size.Height - BarHeight) / 2);
            if (Track is { } track)
            {
                drawingContext.PushOpacity(TrackOpacity);
                drawingContext.DrawRoundedRectangle(track, null, new Rect(0, top, size.Width, BarHeight), 2, 2);
                drawingContext.Pop();
            }

            var fill = StorageLayout.BarFillWidth(size.Width, Fraction);
            if (Fill is { } brush && fill > 0)
            {
                drawingContext.DrawRoundedRectangle(brush, null, new Rect(0, top, fill, BarHeight), 2, 2);
            }
        }
    }
}
