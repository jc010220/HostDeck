using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Presentation.ViewModels;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace HostDeck.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        SidebarColumn.Width = new GridLength(Math.Clamp(viewModel.Settings.SidebarWidth, 200, 700));
        SetSidebarVisible(!viewModel.Settings.SidebarCollapsed, save: false);
        Workspace.OpenInPane = viewModel.OpenInPane;
        RegisterShortcuts();
        // Ordner aufgeklappt ⇒ Online-Status der jetzt sichtbaren Verbindungen gleich prüfen
        SessionTreeView.AddHandler(System.Windows.Controls.TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, _) => _vm.Reachability?.Invalidate()));

        // Terminal (WebView2) – Kürzel, Theme und Schriftgröße
        TerminalView.FontSize = viewModel.Settings.TerminalFontSize;
        TerminalView.FontSizeChanged = size => { viewModel.Settings.TerminalFontSize = size; viewModel.SaveSettings(); };
        TerminalView.CurrentTheme = () => Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Light ? "light" : "dark";
        TerminalView.ShortcutHandler = OnTerminalShortcut;

        viewModel.DetachedWindowRequested += workspace =>
        {
            var window = new SessionWindow(viewModel, workspace);
            window.Show();
        };
        Activated += (_, _) => viewModel.FocusedWorkspace = viewModel.Workspace;
        Closing += (_, _) =>
        {
            foreach (var w in Application.Current.Windows.OfType<SessionWindow>().ToList()) w.Close();
        };

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentTheme))
                TerminalView.BroadcastTheme(TerminalView.CurrentTheme!());
        };
    }

    /// <summary>Tastenkürzel, die im Terminal gedrückt wurden (dort hat WebView2 den Tastaturfokus).</summary>
    private void OnTerminalShortcut(string key)
    {
        switch (key)
        {
            case "ctrl+shift+k" or "ctrl+shift+p": ShowPalette(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this); break;
            case "ctrl+tab": _vm.FocusedWorkspace.SelectNextTab(); break;
            case "ctrl+shift+tab": _vm.FocusedWorkspace.SelectNextTab(backwards: true); break;
            case "ctrl+shift+w": _vm.FocusedWorkspace.Close(_vm.FocusedWorkspace.ActiveTab); break;
            case "ctrl+shift+m": _vm.ToggleMultiExecutionCommand.Execute(null); break;
            case "ctrl+shift+l": QuickConnectBox.Focus(); break;
            case "ctrl+f": FocusSearch(); break;
            case "ctrl+shift+b": ToggleSidebar(); break;
            case "ctrl+alt+arrowright": _vm.Workspace.FocusNextPane(); break;
            case "ctrl+alt+1": _vm.Workspace.SetLayout(WorkspaceLayout.Single); break;
            case "ctrl+alt+2": _vm.Workspace.SetLayout(WorkspaceLayout.TwoColumns); break;
            case "ctrl+alt+3": _vm.Workspace.SetLayout(WorkspaceLayout.TwoRows); break;
            case "ctrl+alt+4": _vm.Workspace.SetLayout(WorkspaceLayout.Grid); break;
        }
    }

    /// <summary>Schnellsuche öffnen (Strg+K; im Terminal Strg+Umschalt+K, da Strg+K dort „Zeile löschen“ ist).</summary>
    public void ShowPalette() => ShowPalette(this);

    public void ShowPalette(Window owner)
    {
        if (Application.Current.Windows.OfType<CommandPaletteWindow>().Any()) return;
        new CommandPaletteWindow(_vm, owner).Show();
    }

    // ---- Snippets ------------------------------------------------------------------------------------

    private void OnSnippetRun(object sender, MouseButtonEventArgs e) => _vm.Snippets.Run(SnippetList.SelectedItem as HostDeck.Presentation.Snippets.Snippet);

    private void OnSnippetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm.Snippets.Run(SnippetList.SelectedItem as HostDeck.Presentation.Snippets.Snippet); e.Handled = true; }
        if (e.Key == Key.Delete) { OnSnippetDelete(sender, e); e.Handled = true; }
    }

    private void OnSnippetAdd(object sender, RoutedEventArgs e)
    {
        var snippet = new HostDeck.Presentation.Snippets.Snippet();
        if (new SnippetDialog(snippet) { Owner = this }.ShowDialog() == true)
            _vm.Snippets.Add(snippet.Name, snippet.Command, snippet.Category, snippet.Execute);
    }

    private void OnSnippetEdit(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is HostDeck.Presentation.Snippets.Snippet s && new SnippetDialog(s) { Owner = this }.ShowDialog() == true)
            _vm.Snippets.Save();
    }

    private void OnSnippetDelete(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is HostDeck.Presentation.Snippets.Snippet s &&
            System.Windows.MessageBox.Show(this, $"Snippet „{s.Name}“ löschen?", "Snippets", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _vm.Snippets.Remove(s);
    }

    // ---- SFTP-Seitenleiste ---------------------------------------------------------------------------

    private SftpBrowserViewModel? Sftp => _vm.ActiveSftp;
    private List<object> SftpSelection => SftpList.SelectedItems.Cast<object>().ToList();

    private void OnSftpPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Sftp is { } sftp) { _ = sftp.NavigateAsync(SftpPath.Text.Trim()); e.Handled = true; }
    }

    private void OnSftpDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SftpList.SelectedItem is HostDeck.Protocols.Ssh.RemoteEntry entry) Sftp?.OpenCommand.Execute(entry);
    }

    private void OnSftpOpen(object sender, RoutedEventArgs e) => OnSftpDoubleClick(sender, null!);
    private void OnSftpDownload(object sender, RoutedEventArgs e) => Sftp?.DownloadCommand.Execute(SftpSelection);
    private void OnSftpDelete(object sender, RoutedEventArgs e) => Sftp?.DeleteCommand.Execute(SftpSelection);
    private void OnSftpRename(object sender, RoutedEventArgs e) => Sftp?.RenameCommand.Execute(SftpList.SelectedItem);

    private void OnSftpUpload(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Dateien hochladen", Multiselect = true };
        if (dialog.ShowDialog(this) == true && Sftp is { } sftp) _ = sftp.UploadAsync(dialog.FileNames);
    }

    private void OnSftpDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && Sftp is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSftpDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && Sftp is { } sftp) _ = sftp.UploadAsync(files);
        e.Handled = true;
    }

    private void RegisterShortcuts()
    {
        void Bind(Key key, ModifierKeys modifiers, Action action) =>
            InputBindings.Add(new KeyBinding(new RelayCommand(action), key, modifiers));

        Bind(Key.K, ModifierKeys.Control, ShowPalette);
        Bind(Key.P, ModifierKeys.Control | ModifierKeys.Shift, ShowPalette);
        Bind(Key.F, ModifierKeys.Control, FocusSearch);
        Bind(Key.B, ModifierKeys.Control | ModifierKeys.Shift, ToggleSidebar);
        Bind(Key.S, ModifierKeys.Control, () => { if (_vm.HasTeamLibrary) _vm.SyncTeamCommand.Execute(null); });
        Bind(Key.L, ModifierKeys.Control, () => QuickConnectBox.Focus());
        Bind(Key.O, ModifierKeys.Control, () => _vm.ImportMRemoteNGCommand.Execute(null));
        Bind(Key.N, ModifierKeys.Control, () => _vm.NewConnectionCommand.Execute(null));
        Bind(Key.N, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.NewFolderCommand.Execute(null));
        Bind(Key.W, ModifierKeys.Control, () => _vm.Workspace.Close(_vm.Workspace.ActiveTab));
        Bind(Key.Tab, ModifierKeys.Control, () => _vm.Workspace.SelectNextTab());
        Bind(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.Workspace.SelectNextTab(backwards: true));
        Bind(Key.Right, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.FocusNextPane());
        Bind(Key.D1, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.SetLayout(WorkspaceLayout.Single));
        Bind(Key.D2, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.SetLayout(WorkspaceLayout.TwoColumns));
        Bind(Key.D3, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.SetLayout(WorkspaceLayout.TwoRows));
        Bind(Key.D4, ModifierKeys.Control | ModifierKeys.Alt, () => _vm.Workspace.SetLayout(WorkspaceLayout.Grid));
        Bind(Key.M, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.ToggleMultiExecutionCommand.Execute(null));
    }

    // ---- Quick-Connect / Suche ----------------------------------------------------------------------

    private void OnQuickConnectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !QuickConnectBox.IsDropDownOpen)
        {
            _vm.QuickConnectCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.QuickConnectText = "";
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _vm.SessionTree.FilterText = "";
                e.Handled = true;
                break;
            case Key.Down: // in die Trefferliste springen
                var first = _vm.SessionTree.AllItems.FirstOrDefault(i => i.IsVisible && (i.IsMatch || !i.IsFolder));
                if (first is not null) first.IsSelected = true;
                SessionTreeView.Focus();
                e.Handled = true;
                break;
            case Key.Enter: // ersten Treffer öffnen
                var match = _vm.SessionTree.AllItems.FirstOrDefault(i => i.IsVisible && !i.IsFolder);
                if (match is not null) _vm.OpenCommand.Execute(match);
                e.Handled = true;
                break;
        }
    }

    // ---- Session-Tree --------------------------------------------------------------------------------

    private void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _vm.SessionTree.SelectedItem = e.NewValue as TreeItemViewModel;

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Doppelklick nur auf Verbindungen auswerten; Ordner klappen per Standardverhalten auf/zu
        if (_vm.SessionTree.SelectedItem is { IsFolder: false } item && e.OriginalSource is FrameworkElement { DataContext: TreeItemViewModel clicked } && clicked == item)
        {
            _vm.OpenCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        var item = _vm.SessionTree.SelectedItem;
        if (item is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                _vm.OpenCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.Delete:
                ConfirmDelete(item);
                e.Handled = true;
                break;
            case Key.F2:
                _vm.RenameCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.F4:
                _vm.EditItemCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.D when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.DuplicateCommand.Execute(item);
                e.Handled = true;
                break;
        }
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e) => _vm.OpenCommand.Execute(_vm.SessionTree.SelectedItem);

    private void OnMenuOpenTiled(object sender, RoutedEventArgs e)
    {
        var item = _vm.SessionTree.SelectedItem;
        _vm.OpenAllTiledCommand.Execute(item?.IsFolder == true ? item : item?.Parent);
    }

    private void OnMenuEdit(object sender, RoutedEventArgs e) => _vm.EditItemCommand.Execute(_vm.SessionTree.SelectedItem);
    private void OnMenuRename(object sender, RoutedEventArgs e) => _vm.RenameCommand.Execute(_vm.SessionTree.SelectedItem);
    private void OnMenuDuplicate(object sender, RoutedEventArgs e) => _vm.DuplicateCommand.Execute(_vm.SessionTree.SelectedItem);

    private void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.SessionTree.SelectedItem is { } item) ConfirmDelete(item);
    }

    private void ConfirmDelete(TreeItemViewModel item)
    {
        var what = item.IsFolder ? $"den Ordner „{item.Name}“ mit allen Einträgen" : $"die Verbindung „{item.Name}“";
        var result = System.Windows.MessageBox.Show(this, $"Soll {what} gelöscht werden?", "Löschen",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes) _vm.DeleteCommand.Execute(item);
    }

    private void OnPaletteButton(object sender, RoutedEventArgs e) => ShowPalette();

    private void OnSecurityMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } element)
        {
            menu.PlacementTarget = element;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    // ---- Seitenleiste ein-/ausklappen ---------------------------------------------------------------

    private void OnSidebarToggle(object sender, RoutedEventArgs e) => SetSidebarVisible(SidebarToggle.IsChecked == true);

    private void ToggleSidebar() => SetSidebarVisible(SidebarPanel.Visibility != Visibility.Visible);

    /// <summary>Suche braucht die Seitenleiste – eingeklappt wird sie dafür wieder geöffnet.</summary>
    private void FocusSearch()
    {
        if (SidebarPanel.Visibility != Visibility.Visible) SetSidebarVisible(true);
        SidebarTabs.SelectedIndex = 0;
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SetSidebarVisible(bool visible, bool save = true)
    {
        SidebarPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = SidebarPanel.Visibility;
        SidebarColumn.MinWidth = visible ? 200 : 0;
        SidebarColumn.Width = visible ? new GridLength(Math.Clamp(_vm.Settings.SidebarWidth, 200, 700)) : new GridLength(0);
        SplitterColumn.Width = new GridLength(visible ? 6 : 0);
        SidebarToggle.IsChecked = visible;
        if (!save || _vm.Settings.SidebarCollapsed == !visible) return;
        _vm.Settings.SidebarCollapsed = !visible;
        _vm.SaveSettings();
    }

    private void OnSidebarResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _vm.Settings.SidebarWidth = SidebarColumn.ActualWidth;
        _vm.SaveSettings();
    }
}
