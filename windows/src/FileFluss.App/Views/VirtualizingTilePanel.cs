// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FileFluss.Core.Models;

namespace FileFluss.App.Views;

/// <summary>
/// The gallery's items panel: equal tiles in rows, and only the rows on screen (plus one
/// above and one below) exist as elements. WPF's WrapPanel builds an element for every item,
/// which a folder of 30,000 photos can't afford; this realizes a few dozen.
///
/// <para>The arithmetic lives in <see cref="GalleryLayout"/>, where it is tested; this class
/// only turns it into realized containers and scroll offsets. It does its own scrolling
/// (<see cref="IScrollInfo"/>), so the ListBox's ScrollViewer must have CanContentScroll on and
/// horizontal scrolling disabled, which gives the panel a finite width to wrap at.</para>
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(136.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(
        nameof(TileHeight), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(172.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Scroll step for the arrow buttons and one wheel line.</summary>
    private const double LineStep = 16;

    private GalleryLayout _layout = new(0, 136, 172, 0);
    private Size _extent;
    private Size _viewport;
    private double _offset;

    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    private int ItemCount => ItemsControl.GetItemsOwner(this) is { } owner ? owner.Items.Count : 0;

    // MARK: - Layout

    protected override Size MeasureOverride(Size availableSize)
    {
        _layout = new GalleryLayout(availableSize.Width, TileWidth, TileHeight, ItemCount);
        UpdateScrollInfo(availableSize);

        var (first, last) = _layout.VisibleRange(_offset, _viewport.Height);

        // InternalChildren has to be touched before the generator is: it is what connects the
        // panel to the ItemsControl's generator in the first place.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        if (generator is not null && first <= last)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                for (var index = first; index <= last; index++, childIndex++)
                {
                    if (generator.GenerateNext(out var isNew) is not UIElement child)
                    {
                        break;
                    }

                    if (isNew)
                    {
                        if (childIndex >= children.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }

                        generator.PrepareItemContainer(child);
                    }

                    child.Measure(new Size(_layout.CellWidth, _layout.TileHeight));
                }
            }
        }

        RemoveTilesOutside(first, last);
        return new Size(
            double.IsInfinity(availableSize.Width) ? _layout.ExtentWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? _layout.ExtentHeight : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return finalSize;
        }

        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0)
            {
                continue;
            }

            var (x, y) = _layout.PositionOf(index);
            children[i].Arrange(new Rect(x, y - _offset, _layout.CellWidth, _layout.TileHeight));
        }

        return finalSize;
    }

    /// <summary>
    /// Gives back the containers of tiles that scrolled out of the realized band — except the
    /// one with keyboard focus: removing it would drop focus out of the list, and the arrow
    /// keys with it.
    /// </summary>
    private void RemoveTilesOutside(int first, int last)
    {
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return;
        }

        var children = InternalChildren;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if ((index >= first && index <= last) || children[i].IsKeyboardFocusWithin)
            {
                continue;
            }

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                // A new listing starts at the top. A refresh of the same folder puts the
                // offset back itself (PanelView restores it), as it does for the list.
                _offset = 0;
                ScrollOwner?.InvalidateScrollInfo();
                break;
        }

        base.OnItemsChanged(sender, args);
    }

    /// <summary>ListBox.ScrollIntoView for a tile that isn't realized yet.</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= ItemCount)
        {
            return;
        }

        SetVerticalOffset(_layout.OffsetToShow(index, _offset, _viewport.Height));
        UpdateLayout();
    }

    private void UpdateScrollInfo(Size availableSize)
    {
        var viewport = new Size(
            double.IsInfinity(availableSize.Width) ? _layout.ExtentWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? _layout.ExtentHeight : availableSize.Height);
        var extent = new Size(viewport.Width, _layout.ExtentHeight);
        var offset = Math.Clamp(_offset, 0, _layout.MaxOffset(viewport.Height));
        if (viewport != _viewport || extent != _extent || !offset.Equals(_offset))
        {
            _viewport = viewport;
            _extent = extent;
            _offset = offset;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    // MARK: - IScrollInfo

    public bool CanVerticallyScroll { get; set; }

    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => 0;

    public double VerticalOffset => _offset;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset - LineStep);

    public void LineDown() => SetVerticalOffset(_offset + LineStep);

    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);

    public void MouseWheelUp() => SetVerticalOffset(_offset - (WheelLines * LineStep));

    public void MouseWheelDown() => SetVerticalOffset(_offset + (WheelLines * LineStep));

    // The gallery wraps instead of scrolling sideways.
    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
    }

    public void SetVerticalOffset(double offset)
    {
        var clamped = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (clamped.Equals(_offset))
        {
            return;
        }

        _offset = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    /// <summary>Scrolls a tile (or something inside it, such as its rename box) fully into view.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return rectangle;
        }

        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            if (ReferenceEquals(children[i], visual) || children[i].IsAncestorOf(visual))
            {
                var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                if (index >= 0)
                {
                    SetVerticalOffset(_layout.OffsetToShow(index, _offset, _viewport.Height));
                }

                break;
            }
        }

        return rectangle;
    }

    private static int WheelLines => Math.Max(1, SystemParameters.WheelScrollLines);
}
