// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace FileFluss.App.Views;

/// <summary>
/// Floats one element over another — the SwiftUI <c>.overlay(alignment:)</c>. The element is
/// placed inside the adorned element's bounds by its own Horizontal/VerticalAlignment and
/// Margin, stays interactive, and leaves the adorned element's layout untouched, so banners
/// and HUDs can be added to a panel or the window without restructuring them.
/// </summary>
public sealed class OverlayAdorner : Adorner
{
    private readonly FrameworkElement _child;

    private OverlayAdorner(UIElement adorned, FrameworkElement child)
        : base(adorned)
    {
        _child = child;
        AddVisualChild(child);
        AddLogicalChild(child);
    }

    protected override int VisualChildrenCount => 1;

    /// <summary>
    /// Shows <paramref name="child"/> over <paramref name="target"/>. Returns a handle that
    /// removes it again, or null when the target isn't in a window yet.
    /// </summary>
    public static OverlayAdorner? Show(UIElement target, FrameworkElement child)
    {
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer)
        {
            return null;
        }

        var adorner = new OverlayAdorner(target, child);
        layer.Add(adorner);
        return adorner;
    }

    public void Remove()
    {
        if (AdornerLayer.GetAdornerLayer(AdornedElement) is { } layer)
        {
            layer.Remove(this);
        }
    }

    /// <summary>The rounded, shadowed card banners and HUDs are drawn on (the macOS material background).</summary>
    public static Border Card(UIElement content, double cornerRadius = 10)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            BorderThickness = new Thickness(1),
            Child = content,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        return card;
    }

    protected override Visual GetVisualChild(int index) => _child;

    protected override Size MeasureOverride(Size constraint)
    {
        var size = AdornedElement.RenderSize;
        _child.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bounds = AdornedElement.RenderSize;
        var desired = _child.DesiredSize;
        var width = _child.HorizontalAlignment == HorizontalAlignment.Stretch ? bounds.Width : Math.Min(desired.Width, bounds.Width);
        var height = _child.VerticalAlignment == VerticalAlignment.Stretch ? bounds.Height : Math.Min(desired.Height, bounds.Height);
        var x = _child.HorizontalAlignment switch
        {
            HorizontalAlignment.Left or HorizontalAlignment.Stretch => 0,
            HorizontalAlignment.Right => bounds.Width - width,
            _ => (bounds.Width - width) / 2,
        };
        var y = _child.VerticalAlignment switch
        {
            VerticalAlignment.Top or VerticalAlignment.Stretch => 0,
            VerticalAlignment.Bottom => bounds.Height - height,
            _ => (bounds.Height - height) / 2,
        };

        // DesiredSize already includes the margin; the element lays its margin out itself.
        _child.Arrange(new Rect(Math.Max(0, x), Math.Max(0, y), Math.Max(0, width), Math.Max(0, height)));
        return finalSize;
    }
}
