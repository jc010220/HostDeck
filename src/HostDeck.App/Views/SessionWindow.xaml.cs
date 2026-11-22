using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Presentation.ViewModels;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

/// <summary>Zusätzliches Session-Fenster (z. B. auf dem zweiten Monitor).</summary>
public partial class SessionWindow : FluentWindow
{
    private readonly MainViewModel _main;
    private readonly WorkspaceViewModel _workspace;

    public SessionWindow(MainViewModel main, WorkspaceViewModel workspace)
    {
        _main = main;
        _workspace = workspace;
        DataContext = workspace;
        InitializeComponent();
        Workspace.OpenInPane = main.OpenInPane;

        Activated += (_, _) => main.FocusedWorkspace = workspace;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkspaceViewModel.ActiveTab) or nameof(WorkspaceViewModel.TabCount)) UpdateTitle();
            if (e.PropertyName == nameof(WorkspaceViewModel.TabCount) && workspace.TabCount == 0) Close(); // letzter Tab weg
        };
        RestorePlacement(workspace.Placement);
        Closing += (_, _) => workspace.Placement = CurrentPlacement();
        Closed += (_, _) => main.CloseDetachedWorkspace(workspace);
        UpdateTitle();

        void Bind(Key key, ModifierKeys mod, Action action) => InputBindings.Add(new KeyBinding(new RelayCommand(action), key, mod));
        Bind(Key.W, ModifierKeys.Control | ModifierKeys.Shift, () => workspace.Close(workspace.ActiveTab)); // Strg+W bleibt „Wort löschen“ (Shell)
        Bind(Key.Tab, ModifierKeys.Control, () => workspace.SelectNextTab());
        Bind(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, () => workspace.SelectNextTab(backwards: true));
        Bind(Key.K, ModifierKeys.Control, () => (Application.Current.MainWindow as MainWindow)?.ShowPalette(this));
    }

    private void RestorePlacement(HostDeck.Presentation.Services.WindowPlacement? p)
    {
        if (p is null || p.Width < MinWidth || p.Height < MinHeight) return;
        // nur auf einem noch vorhandenen Bildschirm (Zweitmonitor evtl. abgesteckt)
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (Rect.Intersect(screen, new Rect(p.Left, p.Top, p.Width, 40)).Width < 100) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = p.Width;
        Height = p.Height;
        if (p.Maximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private HostDeck.Presentation.Services.WindowPlacement CurrentPlacement()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new() { Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height, Maximized = WindowState == WindowState.Maximized };
    }

    private void UpdateTitle()
    {
        var title = _workspace.ActiveTab is { } t ? $"{t.Title} – HostDeck" : "HostDeck";
        Title = title;
        TitleBar.Title = title;
    }
}
