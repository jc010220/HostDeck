using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.App.Views;

public partial class PaneView : UserControl
{
    private Point _dragStart;
    private SessionTabViewModel? _dragTab;

    public PaneView()
    {
        InitializeComponent();
        PreviewMouseDown += (_, _) => Activate();
        GotKeyboardFocus += (_, _) => Activate();
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    public WorkspaceViewModel? Workspace { get; set; }
    public Action<TreeItemViewModel, PaneViewModel>? OpenTreeItem { get; set; }
    private PaneViewModel Pane => (PaneViewModel)DataContext;

    private void Activate()
    {
        if (Workspace is not null && DataContext is PaneViewModel pane) Workspace.ActivePane = pane;
    }

    private static SessionTabViewModel? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as SessionTabViewModel;

    private void OnCloseButton(object sender, RoutedEventArgs e)
    {
        Workspace?.Close(TabOf(sender));
        e.Handled = true;
    }

    private void OnCloseMenu(object sender, RoutedEventArgs e) => Workspace?.Close(TabOf(sender));

    private void OnReconnectMenu(object sender, RoutedEventArgs e)
    {
        var tab = TabOf(sender);
        if (tab?.Terminal is { } terminal) _ = terminal.ConnectAsync();
        tab?.Rdp?.ConnectCommand.Execute(null);
        if (tab?.Vnc is { } vnc) _ = vnc.ConnectAsync();
    }

    private static MainViewModel? Main => Application.Current.MainWindow?.DataContext as MainViewModel;

    private void OnDetachMenu(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Main?.DetachTab(tab);
    }

    private void OnFullScreenMenu(object sender, RoutedEventArgs e) => TabOf(sender)?.Rdp?.FullScreenCommand.Execute(null);

    private void OnSaveMenu(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel main)
            main.SaveTransientSessionCommand.Execute(TabOf(sender));
    }

    // ---- Tab ziehen (zwischen Bereichen verschieben), Mittelklick schließt -------------------------

    private void OnTabStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragTab = ItemAt(e.OriginalSource as DependencyObject)?.DataContext as SessionTabViewModel;
    }

    private void OnTabStripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ItemAt(e.OriginalSource as DependencyObject)?.DataContext is SessionTabViewModel tab)
        {
            Workspace?.Close(tab);
            e.Handled = true;
        }
        _dragTab = null;
    }

    private void OnTabStripMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragTab is null) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var tab = _dragTab;
        _dragTab = null;
        var result = DragDrop.DoDragDrop(this, new DataObject(typeof(SessionTabViewModel), tab), DragDropEffects.Move);

        // Außerhalb aller HostDeck-Fenster losgelassen ⇒ neues Fenster an der Mausposition (wie Browser-Tabs)
        if (result == DragDropEffects.None && Main is { } main && !IsOverAnyAppWindow(out var screen))
        {
            main.DetachTab(tab);
            if (Application.Current.Windows.OfType<SessionWindow>().LastOrDefault() is { } window)
            {
                window.Left = screen.X - 120;
                window.Top = screen.Y - 20;
            }
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SessionTabViewModel)) ? DragDropEffects.Move
            : e.Data.GetData(typeof(TreeItemViewModel)) is TreeItemViewModel { IsFolder: false } ? DragDropEffects.Link
            : e.Data.GetData(typeof(TreeItemViewModel)) is TreeItemViewModel { IsFolder: true } ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SessionTabViewModel)) is SessionTabViewModel tab)
        {
            if (Workspace is not null && !Workspace.AllTabs.Contains(tab))
            {
                Main?.MoveTabToWorkspace(tab, Workspace, Pane); // aus einem anderen Fenster
            }
            else
            {
                var index = ItemAt(e.OriginalSource as DependencyObject)?.DataContext is SessionTabViewModel over && over != tab
                    ? Pane.Tabs.IndexOf(over) : (int?)null;
                Workspace?.MoveTab(tab, Pane, index);
            }
        }
        else if (e.Data.GetData(typeof(TreeItemViewModel)) is TreeItemViewModel item)
        {
            OpenTreeItem?.Invoke(item, Pane);
        }
        e.Handled = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    /// <summary>Mausposition (Bildschirm, in WPF-Einheiten des Hauptfensters) und ob sie über einem App-Fenster liegt.</summary>
    private static bool IsOverAnyAppWindow(out Point screen)
    {
        GetCursorPos(out var p);
        var source = PresentationSource.FromVisual(Application.Current.MainWindow);
        var scale = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        screen = scale.Transform(new Point(p.X, p.Y));
        foreach (Window w in Application.Current.Windows)
        {
            if (!w.IsVisible || w.WindowState == WindowState.Minimized) continue;
            var rect = w.WindowState == WindowState.Maximized
                ? SystemParameters.WorkArea
                : new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
            if (rect.Contains(screen)) return true;
        }
        return false;
    }

    private static ListBoxItem? ItemAt(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        return source as ListBoxItem;
    }
}
