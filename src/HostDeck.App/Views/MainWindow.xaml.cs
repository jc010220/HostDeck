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
        SidebarPanel.IsKeyboardFocusWithinChanged += OnSidebarFocusWithinChanged;
        SizeChanged += (_, _) => UpdateToolbarCompact();
        Workspace.OpenInPane = viewModel.OpenInPane;
        RegisterShortcuts();
        // Ordner aufgeklappt ⇒ Online-Status der jetzt sichtbaren Verbindungen gleich prüfen
        SessionTreeView.AddHandler(System.Windows.Controls.TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, _) => _vm.Reachability?.Invalidate()));

        // Terminal (WebView2) – Kürzel, Theme und Schriftgröße
        TerminalView.FontSize = viewModel.Settings.TerminalFontSize;
        TerminalView.RightClickMenu = () => viewModel.Settings.TerminalRightClickMenu;
        TerminalView.ConfirmMultiLinePaste = () => viewModel.Settings.ConfirmMultiLinePaste;
        TerminalView.MultiLinePasteConfirmationTurnedOff = () => { viewModel.Settings.ConfirmMultiLinePaste = false; viewModel.SaveSettings(); };
        TerminalView.FontSizeChanged = size => { viewModel.Settings.TerminalFontSize = size; viewModel.SaveSettings(); };
        viewModel.SettingsApplied += () => TerminalView.BroadcastFontSize(viewModel.Settings.TerminalFontSize);
        // Windows wechselt hell/dunkel (Modus „System“) ⇒ Terminals sofort mitziehen
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (theme, _) =>
            Dispatcher.BeginInvoke(() => TerminalView.BroadcastTheme(theme == Wpf.Ui.Appearance.ApplicationTheme.Light ? "light" : "dark"));
        TerminalView.CurrentTheme = () => Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Light ? "light" : "dark";
        TerminalView.ShortcutHandler = OnTerminalShortcut;
        // Kürzel auch aus einer RDP-Sitzung heraus (dort gehen sonst alle Tasten an den entfernten Rechner)
        SourceInitialized += (_, _) =>
        {
            var hotkeys = new Services.SessionHotkeys(this, key =>
            {
                if (key == "release")
                {
                    var active = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
                    if (Behaviors.FocusRecovery.Release(active))
                        _vm.StatusText = "Keyboard released from the session – HostDeck shortcuts work now. Click into the session to continue there.";
                }
                else
                {
                    // Bereich wechseln: Tastatur vorher aus der RDP-Sitzung lösen, sonst bliebe sie im alten Bereich
                    if (key == "ctrl+alt+arrowright") Behaviors.FocusRecovery.Release(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this);
                    OnTerminalShortcut(key);
                }
            });
            Closed += (_, _) => hotkeys.Dispose();
        };

        viewModel.DetachedWindowRequested += workspace =>
        {
            var window = new SessionWindow(viewModel, workspace);
            window.Show();
        };
        Activated += (_, _) => viewModel.FocusedWorkspace = viewModel.Workspace;
        RestorePlacement(viewModel.Settings.MainWindow);
        Closing += (_, _) =>
        {
            SavePlacement();
            _vm.CaptureWorkspaceForShutdown(); // vor dem Schließen der Zusatzfenster (deren Tabs wandern sonst hierher)
            foreach (var w in Application.Current.Windows.OfType<SessionWindow>().ToList()) w.Close();
        };

        viewModel.PropertyChanged += (_, e) =>
        {
            // Erst NACH dem Umstellen der App übernehmen (bei „System“ steht das echte Schema erst danach fest)
            if (e.PropertyName == nameof(MainViewModel.CurrentTheme))
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    () => TerminalView.BroadcastTheme(TerminalView.CurrentTheme!()));
        };
    }

    /// <summary>Tastenkürzel, die im Terminal gedrückt wurden (dort hat WebView2 den Tastaturfokus).</summary>
    private void OnTerminalShortcut(string key)
    {
        switch (key)
        {
            case "ctrl+shift+k" or "ctrl+shift+p": ShowPalette(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this); break;
            case "ctrl+,": _vm.OpenSettingsCommand.Execute(null); break;
            case "ctrl+alt+l": _vm.LockNowCommand.Execute(null); break;
            case "ctrl+tab": _vm.FocusedWorkspace.SelectNextTab(); break;
            case "ctrl+shift+tab": _vm.FocusedWorkspace.SelectNextTab(backwards: true); break;
            case "ctrl+shift+w": _vm.FocusedWorkspace.Close(_vm.FocusedWorkspace.ActiveTab); break;
            case "ctrl+shift+m": _vm.ToggleMultiExecutionCommand.Execute(null); break;
            case "ctrl+shift+d": _vm.DuplicateInSplitView(_vm.FocusedWorkspace.ActiveTab); break;
            case "ctrl+shift+l": FocusQuickConnect(); break;
            case "ctrl+f": FocusSearch(); break;
            case "ctrl+shift+b": ToggleSidebar(); break;
            case "ctrl+alt+arrowright": _vm.Workspace.FocusNextPane(); break;
            case "ctrl+alt+1": _vm.Workspace.SetLayout(WorkspaceLayout.Single); break;
            case "ctrl+alt+2": _vm.Workspace.SetLayout(WorkspaceLayout.TwoColumns); break;
            case "ctrl+alt+3": _vm.Workspace.SetLayout(WorkspaceLayout.TwoRows); break;
            case "ctrl+alt+4": _vm.Workspace.SetLayout(WorkspaceLayout.Grid); break;
        }
    }

    internal static bool IsInsideTerminal(DependencyObject? d)
    {
        for (; d is not null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TerminalView) return true;
        return false;
    }

    /// <summary>Schnellsuche öffnen (Strg+K; im Terminal Strg+Umschalt+K, da Strg+K dort „Zeile löschen“ ist).</summary>
    public void ShowPalette() => ShowPalette(this);

    /// <summary>
    /// App-weite Kürzel, die immer funktionieren müssen – egal welches Element (Baum, Suchfeld, Liste, Web-Tab,
    /// abgelöstes Fenster) gerade den Fokus hat. Läuft als Klassen-Handler vor allen Steuerelementen, die
    /// Tastendrücke sonst selbst verbrauchen (z. B. Textfelder, Listen, Menüs).
    /// </summary>
    public static void RegisterGlobalShortcuts(MainWindow main) =>
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler((sender, e) =>
        {
            if (e.Handled || sender is not (MainWindow or SessionWindow)) return; // nicht in Dialogen/Palette
            // Im Terminal gehören Tasten wie Strg+K (nano: ausschneiden, bash: Zeile löschen) dem Server. Das WebView2 meldet
            // solche Tasten zusätzlich an WPF – hier ignorieren; App-Kürzel kommen aus dem Terminal über Strg+Umschalt+…
            if (IsInsideTerminal(e.OriginalSource as DependencyObject) || IsInsideTerminal(Keyboard.FocusedElement as DependencyObject)) return;
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if ((key == Key.K && mods == ModifierKeys.Control) || (key == Key.P && mods == (ModifierKeys.Control | ModifierKeys.Shift)))
            {
                main.ShowPalette((Window)sender);
                e.Handled = true;
            }
            else if (key == Key.L && mods == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                main._vm.LockNowCommand.Execute(null);
                e.Handled = true;
            }
            else if (key == Key.OemComma && mods == ModifierKeys.Control)
            {
                main._vm.OpenSettingsCommand.Execute(null);
                e.Handled = true;
            }
        }));

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
        var snippets = _vm.Snippets;
        var snippet = new HostDeck.Presentation.Snippets.Snippet();
        var dialog = new SnippetDialog(snippet, snippets.TeamAvailable && !snippets.TeamReadOnly) { Owner = this };
        if (dialog.ShowDialog() == true)
            snippets.Add(snippet.Name, snippet.Command, snippet.Category, snippet.Execute, team: dialog.Shared);
    }

    private void OnSnippetEdit(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is not HostDeck.Presentation.Snippets.Snippet s) return;
        var snippets = _vm.Snippets;
        if (!snippets.CanEdit(s))
        {
            System.Windows.MessageBox.Show(this, "This team snippet is read-only here (its library is set to read-only).", "Snippets");
            return;
        }
        var dialog = new SnippetDialog(s, snippets.TeamAvailable && !snippets.TeamReadOnly) { Owner = this };
        if (dialog.ShowDialog() == true) snippets.SetShared(s, dialog.Shared);
    }

    private void OnSnippetDelete(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is not HostDeck.Presentation.Snippets.Snippet s) return;
        if (!_vm.Snippets.CanEdit(s))
        {
            System.Windows.MessageBox.Show(this, "This team snippet is read-only here (its library is set to read-only).", "Snippets");
            return;
        }
        var question = s.IsTeam ? $"Delete team snippet “{s.Name}” for everyone?" : $"Delete snippet “{s.Name}”?";
        if (System.Windows.MessageBox.Show(this, question, "Snippets", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
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
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Upload files", Multiselect = true };
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
        // Im Terminal nicht greifen: Strg+L/O/N/S … gehören dort der Shell bzw. nano. App-Kürzel leitet das Terminal selbst weiter.
        void Bind(Key key, ModifierKeys modifiers, Action action) =>
            InputBindings.Add(new KeyBinding(new RelayCommand(action, () => !IsInsideTerminal(Keyboard.FocusedElement as DependencyObject)), key, modifiers));

        Bind(Key.F, ModifierKeys.Control, FocusSearch);
        Bind(Key.B, ModifierKeys.Control | ModifierKeys.Shift, ToggleSidebar);
        Bind(Key.S, ModifierKeys.Control, () => { if (_vm.HasTeamLibrary) _vm.SyncTeamCommand.Execute(null); });
        Bind(Key.L, ModifierKeys.Control, FocusQuickConnect);
        Bind(Key.D, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.DuplicateInSplitView(_vm.FocusedWorkspace.ActiveTab));
        Bind(Key.O, ModifierKeys.Control, () => _vm.ImportMRemoteNGCommand.Execute(null));
        Bind(Key.N, ModifierKeys.Control, () => _vm.NewConnectionCommand.Execute(null));
        Bind(Key.N, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.NewFolderCommand.Execute(null));
        Bind(Key.W, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.Workspace.Close(_vm.Workspace.ActiveTab)); // Strg+W bleibt „Wort löschen“ (Shell)
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

    /// <summary>Rechtsklick wählt den Eintrag unter der Maus aus (sonst wirkte das Menü auf den vorher markierten).</summary>
    private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.TreeViewItem>(d) is { } container)
        {
            container.IsSelected = true;
            container.Focus();
        }
        else
        {
            _vm.SessionTree.ClearSelection();
        }
    }

    /// <summary>Nur sinnvolle Einträge zeigen: Verbindung, Ordner, Team-Ordner oder leere Fläche.</summary>
    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = _vm.SessionTree.SelectedItem;
        var isConnection = item is { IsFolder: false };
        var isTeamRoot = item is { IsTeam: true };
        var isFolder = item is { IsFolder: true } && !isTeamRoot;
        static Visibility Show(bool v) => v ? Visibility.Visible : Visibility.Collapsed;

        MenuConnect.Visibility = Show(isConnection);
        MenuOpenAll.Visibility = Show(isFolder || isTeamRoot);
        MenuSepOpen.Visibility = Show(item is not null);
        MenuNewPersonal.Visibility = Show(item is null || isTeamRoot || item.Node.Ancestors().Any(a => a.Extensions.ContainsKey(HostDeck.Core.Sync.TreeMerge.TeamContainerKey)));
        MenuSepEdit.Visibility = Show(item is not null);
        MenuProperties.Visibility = Show(isConnection || isFolder);
        MenuRename.Visibility = Show(isConnection || isFolder);
        MenuDuplicate.Visibility = Show(isConnection || isFolder);
        MenuDelete.Visibility = Show(isConnection || isFolder);
        MenuTeamSync.Visibility = Show(isTeamRoot);
        MenuTeamPassword.Visibility = Show(isTeamRoot);
        MenuTeamRemovePassword.Visibility = Show(isTeamRoot);
        MenuTeamDisconnect.Visibility = Show(isTeamRoot);
    }

    /// <summary>Klick auf die leere Fläche im Baum hebt die Auswahl auf ⇒ „Neu …“ legt dann auf oberster Ebene an.</summary>
    private void OnTreeEmptyAreaClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.TreeViewItem>(d) is not null) return;
        _vm.SessionTree.ClearSelection();
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        for (; d is not null; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T match) return match;
        return null;
    }

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
        var what = item.IsFolder ? $"the folder “{item.Name}” with all its entries" : $"the connection “{item.Name}”";
        var result = System.Windows.MessageBox.Show(this, $"Delete {what}?", "Delete",
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

    private bool? _compactToolbar;

    /// <summary>Schmales Fenster (z. B. halber Laptop-Bildschirm): Toolbar nur mit Symbolen, Beschriftung als Tooltip.</summary>
    private void UpdateToolbarCompact()
    {
        var compact = ActualWidth < 1050;
        if (_compactToolbar == compact) return;
        _compactToolbar = compact;
        foreach (var button in Toolbar.Children.OfType<System.Windows.Controls.Primitives.ButtonBase>())
            if (button.Content is System.Windows.Controls.Panel panel)
                foreach (var label in LabelsOf(panel)) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        static IEnumerable<FrameworkElement> LabelsOf(System.Windows.Controls.Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is System.Windows.Controls.TextBlock t) yield return t;
                else if (child is System.Windows.Controls.Border { Tag: "label" } hint) yield return hint;
                else if (child is System.Windows.Controls.Panel inner) foreach (var x in LabelsOf(inner)) yield return x;
                // Zähler-Badge (in einem Border) bleibt sichtbar
            }
        }
    }

    private void ToggleSidebar() => SetSidebarVisible(SidebarPanel.Visibility != Visibility.Visible);

    /// <summary>Seitenleiste nur vorübergehend eingeblendet (Strg+L/Strg+F bei eingeklappter Leiste).</summary>
    private bool _sidebarTemporary;

    /// <summary>
    /// Eingeklappt ⇒ für Schnellverbindung/Suche kurz einblenden (ohne die Einstellung zu ändern) und wieder
    /// einklappen, sobald der Fokus die Seitenleiste verlässt (z. B. nach dem Verbinden).
    /// </summary>
    private void ShowSidebarTemporarily()
    {
        if (SidebarPanel.Visibility == Visibility.Visible) return;
        SetSidebarVisible(true, save: false);
        _sidebarTemporary = true;
    }

    private void OnSidebarFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true || !_sidebarTemporary) return;
        // Dropdown der Schnellverbindung/Kontextmenü: Fokus liegt kurz in einem Popup ⇒ erst danach prüfen
        Dispatcher.BeginInvoke(() =>
        {
            if (!_sidebarTemporary || SidebarPanel.IsKeyboardFocusWithin || QuickConnectBox.IsDropDownOpen) return;
            _sidebarTemporary = false;
            SetSidebarVisible(false, save: false);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FocusQuickConnect()
    {
        ShowSidebarTemporarily();
        Dispatcher.BeginInvoke(() => QuickConnectBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Suche braucht die Seitenleiste – eingeklappt wird sie dafür vorübergehend eingeblendet.</summary>
    private void FocusSearch()
    {
        ShowSidebarTemporarily();
        SidebarTabs.SelectedIndex = 0;
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SetSidebarVisible(bool visible, bool save = true)
    {
        if (save) _sidebarTemporary = false; // bewusst ein-/ausgeklappt ⇒ bleibt so
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

    // ---- Fensterposition merken -------------------------------------------------------------------

    private void RestorePlacement(HostDeck.Presentation.Services.WindowPlacement? p)
    {
        if (p is null || p.Width < MinWidth || p.Height < MinHeight) return;
        // Nur übernehmen, wenn das Fenster noch auf einem vorhandenen Bildschirm liegt (Monitor abgesteckt, Dock gewechselt …)
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(p.Left, p.Top, p.Width, 40); // Titelleiste muss erreichbar sein
        if (!screen.IntersectsWith(title) || Rect.Intersect(screen, title).Width < 100) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = Math.Min(p.Width, screen.Width);
        Height = Math.Min(p.Height, screen.Height);
        if (p.Maximized) Loaded += (_, _) => WindowState = WindowState.Maximized; // erst nach dem Laden ⇒ richtiger Monitor
    }

    private void SavePlacement()
    {
        // RestoreBounds = normale Größe auch dann, wenn das Fenster gerade maximiert/minimiert ist
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0) return;
        _vm.Settings.MainWindow = new HostDeck.Presentation.Services.WindowPlacement
        {
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        _vm.SaveSettings();
    }

    private void OnSidebarResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _vm.Settings.SidebarWidth = SidebarColumn.ActualWidth;
        _vm.SaveSettings();
    }
}
