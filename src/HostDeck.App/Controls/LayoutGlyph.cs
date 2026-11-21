using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.App.Controls;

/// <summary>
/// Selbst gezeichnetes Layout-Symbol (1 / 2 nebeneinander / 2 untereinander / 4er-Grid).
/// Die Fluent-Symbolschrift von WPF-UI enthält für die Split-Layouts keine passenden Glyphen –
/// dort erschienen fremde Zeichen. Gezeichnet wird in der geerbten Textfarbe (Hell/Dunkel).
/// </summary>
public sealed class LayoutGlyph : FrameworkElement
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(WorkspaceLayout), typeof(LayoutGlyph),
        new FrameworkPropertyMetadata(WorkspaceLayout.Single, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(LayoutGlyph),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public WorkspaceLayout Layout
    {
        get => (WorkspaceLayout)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public LayoutGlyph()
    {
        Width = 16;
        Height = 16;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var pen = new Pen(Foreground, 1.25);
        pen.Freeze();
        var w = ActualWidth;
        var h = ActualHeight;
        var outer = new Rect(1.5, 2.5, w - 3, h - 5);
        dc.DrawRoundedRectangle(null, pen, outer, 2, 2);

        var midX = outer.Left + outer.Width / 2;
        var midY = outer.Top + outer.Height / 2;
        if (Layout is WorkspaceLayout.TwoColumns or WorkspaceLayout.Grid)
            dc.DrawLine(pen, new Point(midX, outer.Top), new Point(midX, outer.Bottom));
        if (Layout is WorkspaceLayout.TwoRows or WorkspaceLayout.Grid)
            dc.DrawLine(pen, new Point(outer.Left, midY), new Point(outer.Right, midY));
    }
}
