using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.App.Behaviors;

/// <summary>
/// Drag &amp; Drop im Session-Tree: <c>behaviors:TreeDragDrop.ViewModel="{Binding SessionTree}"</c>.
/// Oberes/unteres Viertel eines Eintrags ⇒ davor/dahinter, Mitte eines Ordners ⇒ hinein.
/// Ziehen auf einen Workspace-Bereich öffnet die Verbindung dort (siehe PaneView).
/// </summary>
public static class TreeDragDrop
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.RegisterAttached(
        "ViewModel", typeof(SessionTreeViewModel), typeof(TreeDragDrop), new PropertyMetadata(null, OnViewModelChanged));

    public static SessionTreeViewModel? GetViewModel(DependencyObject d) => (SessionTreeViewModel?)d.GetValue(ViewModelProperty);
    public static void SetViewModel(DependencyObject d, SessionTreeViewModel? value) => d.SetValue(ViewModelProperty, value);

    private static Point _dragStart;
    private static TreeItemViewModel? _dragItem;
    private static DropAdorner? _adorner;

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView tree || e.OldValue is not null) return;
        tree.AllowDrop = true;
        tree.PreviewMouseLeftButtonDown += (_, args) =>
        {
            _dragStart = args.GetPosition(tree);
            _dragItem = ItemAt(args.OriginalSource as DependencyObject)?.DataContext as TreeItemViewModel;
        };
        tree.PreviewMouseMove += OnMouseMove;
        tree.DragOver += OnDragOver;
        tree.DragLeave += (_, _) => ClearAdorner();
        tree.Drop += OnDrop;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        var tree = (TreeView)sender;
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;
        var delta = e.GetPosition(tree) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(tree, new DataObject(typeof(TreeItemViewModel), item), DragDropEffects.Move | DragDropEffects.Link);
        ClearAdorner();
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        var tree = (TreeView)sender;
        e.Handled = true;
        if (GetViewModel(tree) is not { } vm || e.Data.GetData(typeof(TreeItemViewModel)) is not TreeItemViewModel source)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var (container, target, position) = HitTest(tree, e);
        var allowed = vm.CanMove(source, target, position);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        AutoScroll(tree, e);

        if (allowed && container is not null) ShowAdorner(container, position);
        else ClearAdorner();
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        var tree = (TreeView)sender;
        ClearAdorner();
        if (GetViewModel(tree) is not { } vm || e.Data.GetData(typeof(TreeItemViewModel)) is not TreeItemViewModel source) return;
        var (_, target, position) = HitTest(tree, e);
        if (vm.Move(source, target, position))
            source.IsSelected = true;
        e.Handled = true;
    }

    private static (TreeViewItem? Container, TreeItemViewModel? Target, DropPosition Position) HitTest(TreeView tree, DragEventArgs e)
    {
        var container = ItemAt(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not TreeItemViewModel target)
            return (null, null, DropPosition.Into); // freie Fläche ⇒ oberste Ebene, ans Ende

        // Nur die Kopfzeile des Eintrags zählt (nicht die aufgeklappten Kinder)
        var header = container.Template.FindName("PART_Header", container) as FrameworkElement ?? container;
        var y = e.GetPosition(header).Y;
        var h = Math.Max(header.ActualHeight, 1);
        var position = target.IsFolder
            ? y < h * 0.25 ? DropPosition.Before : y > h * 0.75 ? DropPosition.After : DropPosition.Into
            : y < h * 0.5 ? DropPosition.Before : DropPosition.After;
        return (container, target, position);
    }

    private static TreeViewItem? ItemAt(DependencyObject? source)
    {
        while (source is not null and not TreeViewItem)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        return source as TreeViewItem;
    }

    private static void AutoScroll(TreeView tree, DragEventArgs e)
    {
        if (FindChild<ScrollViewer>(tree) is not { } sv) return;
        var y = e.GetPosition(tree).Y;
        if (y < 24) sv.LineUp();
        else if (y > tree.ActualHeight - 24) sv.LineDown();
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private static void ShowAdorner(TreeViewItem container, DropPosition position)
    {
        var header = container.Template.FindName("PART_Header", container) as UIElement ?? container;
        if (_adorner?.AdornedElement == header)
        {
            _adorner.Position = position;
            return;
        }
        ClearAdorner();
        if (AdornerLayer.GetAdornerLayer(header) is not { } layer) return;
        _adorner = new DropAdorner(header) { Position = position };
        layer.Add(_adorner);
    }

    private static void ClearAdorner()
    {
        if (_adorner is null) return;
        AdornerLayer.GetAdornerLayer(_adorner.AdornedElement)?.Remove(_adorner);
        _adorner = null;
    }

    /// <summary>Einfügelinie (davor/dahinter) bzw. Rahmen (in Ordner) in Akzentfarbe.</summary>
    private sealed class DropAdorner(UIElement adorned) : Adorner(adorned)
    {
        private DropPosition _position;

        public DropPosition Position
        {
            get => _position;
            set { _position = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var brush = SystemParameters.WindowGlassBrush as Brush ?? Brushes.DodgerBlue;
            var pen = new Pen(brush, 2);
            var size = AdornedElement.RenderSize;
            switch (Position)
            {
                case DropPosition.Before: dc.DrawLine(pen, new Point(0, 1), new Point(size.Width, 1)); break;
                case DropPosition.After: dc.DrawLine(pen, new Point(0, size.Height - 1), new Point(size.Width, size.Height - 1)); break;
                default: dc.DrawRoundedRectangle(null, pen, new Rect(1, 1, Math.Max(0, size.Width - 2), Math.Max(0, size.Height - 2)), 4, 4); break;
            }
            IsHitTestVisible = false;
        }
    }
}
