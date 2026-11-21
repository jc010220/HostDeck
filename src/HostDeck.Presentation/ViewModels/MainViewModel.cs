using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.QuickConnect;
using HostDeck.Presentation.Search;
using HostDeck.Presentation.Snippets;
using HostDeck.Presentation.Services;

namespace HostDeck.Presentation.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxHistory = 20;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settingsStore;
    private readonly IThemeService _theme;
    private readonly IConnectionStore _store;

    public MainViewModel(IDialogService dialogs, ISettingsStore settingsStore, IThemeService theme, IConnectionStore store)
    {
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _theme = theme;
        _store = store;
        Settings = settingsStore.Load();
        _currentTheme = Settings.Theme;
        _isDeviceUnlockEnabled = store.HasDeviceUnlock;
        SessionTree.Changed += (_, _) => Persist();
        FocusedWorkspace = Workspace;
        Workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.ActiveTab)) UpdateActiveSftp();
        };
    }

    /// <summary>SFTP-Browser des aktiven SSH-Tabs (Seitenleiste „SFTP“).</summary>
    [ObservableProperty] private SftpBrowserViewModel? _activeSftp;
    private TerminalSessionViewModel? _sftpSource;

    private void UpdateActiveSftp()
    {
        if (_sftpSource is not null) _sftpSource.PropertyChanged -= OnTerminalPropertyChanged;
        _sftpSource = Workspace.ActiveTab?.Terminal;
        if (_sftpSource is not null) _sftpSource.PropertyChanged += OnTerminalPropertyChanged;
        ActiveSftp = _sftpSource?.Sftp;
    }

    private void OnTerminalPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSessionViewModel.Sftp)) ActiveSftp = _sftpSource?.Sftp;
    }

    /// <summary>Tab erzeugen; SSH-Tabs bekommen eine Terminal-Engine und verbinden sofort.</summary>
    private SessionTabViewModel CreateTab(ConnectionNode connection, CredentialVault vault, bool transient)
    {
        var tab = new SessionTabViewModel(connection, vault, transient);
        tab.ActionRequested += OnTabAction;
        if (!transient) RememberRecent(connection.Id);
        if (tab.Protocol is ProtocolKind.Ssh2 or ProtocolKind.Ssh1 or ProtocolKind.Telnet or ProtocolKind.Raw)
        {
            tab.Terminal = new TerminalSessionViewModel(tab, _store, _dialogs, Settings, RouteTerminalInput);
            _ = tab.Terminal.ConnectAsync();
        }
        else if (tab.Protocol == ProtocolKind.Rdp)
        {
            tab.Rdp = new RdpSessionViewModel(tab); // verbindet, sobald die View ihr Fenster-Handle hat
        }
        else if (tab.Protocol is ProtocolKind.Http or ProtocolKind.Https)
        {
            tab.Web = new WebSessionViewModel(tab);
        }
        else if (tab.Protocol == ProtocolKind.Vnc)
        {
            tab.Vnc = new VncSessionViewModel(tab, _dialogs);
        }
        return tab;
    }

    private void OnTabAction(SessionTabViewModel tab, TabAction action)
    {
        switch (action)
        {
            case TabAction.Close:
                AllWorkspaces.FirstOrDefault(w => w.AllTabs.Contains(tab))?.Close(tab);
                break;
            case TabAction.Detach:
                if (AllWorkspaces.Any(w => w.AllTabs.Contains(tab))) DetachTab(tab);
                break;
            case TabAction.Save:
                SaveTransientSessionCommand.Execute(tab);
                break;
        }
    }

    /// <summary>
    /// Multi-Execution: Eingaben einer markierten Session gehen an alle verbundenen, markierten
    /// Terminal-Tabs (MobaXterm „MultiExec“); sonst nur an die eigene Session.
    /// </summary>
    internal void RouteTerminalInput(TerminalSessionViewModel source, string data)
    {
        if (!IsMultiExecutionEnabled || !source.Tab.IsMultiExecutionTarget)
        {
            source.Send(data);
            return;
        }
        foreach (var terminal in AllTabs.Where(t => t.IsMultiExecutionTarget).Select(t => t.Terminal).OfType<TerminalSessionViewModel>())
            if (terminal.IsConnected) terminal.Send(data);
    }

    public SessionTreeViewModel SessionTree { get; } = new();

    // ---- Abgelöste Fenster (zweiter Monitor) ----------------------------------------------------------

    /// <summary>Arbeitsbereiche in eigenen Fenstern (Tab „in neuem Fenster“).</summary>
    public System.Collections.ObjectModel.ObservableCollection<WorkspaceViewModel> DetachedWorkspaces { get; } = [];

    /// <summary>Die View erzeugt dafür ein Fenster.</summary>
    public event Action<WorkspaceViewModel>? DetachedWindowRequested;

    public IEnumerable<WorkspaceViewModel> AllWorkspaces => DetachedWorkspaces.Prepend(Workspace);
    public IEnumerable<SessionTabViewModel> AllTabs => AllWorkspaces.SelectMany(w => w.AllTabs);

    /// <summary>Workspace des zuletzt aktiven Fensters (für Snippets/Schnellsuche).</summary>
    public WorkspaceViewModel FocusedWorkspace { get; set; } = null!;

    /// <summary>Tab in ein neues Fenster verschieben (ohne die Session zu schließen).</summary>
    public WorkspaceViewModel DetachTab(SessionTabViewModel tab)
    {
        var source = AllWorkspaces.First(w => w.AllTabs.Contains(tab));
        var target = new WorkspaceViewModel();
        source.Detach(tab);
        target.Open(tab);
        DetachedWorkspaces.Add(target);
        DetachedWindowRequested?.Invoke(target);
        return target;
    }

    /// <summary>Tab aus einem anderen Fenster hierher holen (Drag &amp; Drop zwischen Fenstern).</summary>
    public void MoveTabToWorkspace(SessionTabViewModel tab, WorkspaceViewModel target, PaneViewModel? pane = null)
    {
        var source = AllWorkspaces.FirstOrDefault(w => w.AllTabs.Contains(tab));
        if (source is null) return;
        if (source == target)
        {
            if (pane is not null) target.MoveTab(tab, pane);
            return;
        }
        source.Detach(tab);
        target.Open(tab, pane);
    }

    /// <summary>Zusatzfenster wird geschlossen: Sessions zurück ins Hauptfenster statt sie zu beenden.</summary>
    public void CloseDetachedWorkspace(WorkspaceViewModel workspace)
    {
        foreach (var tab in workspace.AllTabs.ToList())
        {
            workspace.Detach(tab);
            Workspace.Open(tab);
        }
        DetachedWorkspaces.Remove(workspace);
        if (FocusedWorkspace == workspace) FocusedWorkspace = Workspace;
    }

    // ---- Snippets / Makros ----------------------------------------------------------------------------

    private SnippetsViewModel? _snippets;

    public SnippetsViewModel Snippets => _snippets ??= new SnippetsViewModel(
        new DocumentSnippetStore(_store),
        (title, message) => _dialogs.Prompt(title, message),
        SendToTerminals);

    /// <summary>An das aktive Terminal senden – bei aktivem Multi-Exec an alle markierten Terminals.</summary>
    public void SendToTerminals(string text)
    {
        var active = FocusedWorkspace.ActiveTab?.Terminal;
        if (active is { IsConnected: true })
        {
            RouteTerminalInput(active, text);
        }
        else if (IsMultiExecutionEnabled)
        {
            foreach (var t in AllTabs.Where(t => t.IsMultiExecutionTarget).Select(t => t.Terminal).OfType<TerminalSessionViewModel>())
                if (t.IsConnected) t.Send(text);
        }
        else
        {
            StatusText = "No connected terminal active – snippet not sent.";
        }
    }

    // ---- Schnellsuche (Strg+K) ------------------------------------------------------------------------

    private CommandPaletteViewModel? _palette;

    public CommandPaletteViewModel Palette => _palette ??= new CommandPaletteViewModel(
        () => SessionTree.Tree,
        () => Settings.RecentConnections,
        () => Settings.QuickConnectDefaultProtocol,
        PaletteActions);

    public ReachabilityMonitor? Reachability { get; private set; }

    /// <summary>Zusätzliche Befehle der App-Schicht (z. B. Updates) für die Schnellsuche.</summary>
    public List<(string Title, string Keywords, Action Run)> AppActions { get; } = [];

    private IEnumerable<(string Title, string Keywords, Action Run)> PaletteActions()
    {
        foreach (var action in AppActions) yield return action;
        foreach (var (name, snapshot) in Settings.SavedWorkspaces.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
            yield return ($"Open workspace: {name}  ({snapshot.ConnectionCount} sessions)", "workspace arbeitsbereich " + name, () => RestoreWorkspace(snapshot));
        yield return ("Save workspace as …", "workspace arbeitsbereich save speichern", () => SaveWorkspaceCommand.Execute(null));
        foreach (var name in Settings.SavedWorkspaces.Keys.Order())
            yield return ($"Delete workspace: {name}", "workspace arbeitsbereich delete löschen " + name, () => { Settings.SavedWorkspaces.Remove(name); SaveSettings(); });
        yield return ("Layout: single pane", "single layout 1", () => Workspace.SetLayout(WorkspaceLayout.Single));
        yield return ("Layout: two side by side", "split vertical columns 2", () => Workspace.SetLayout(WorkspaceLayout.TwoColumns));
        yield return ("Layout: two stacked", "split horizontal rows 2", () => Workspace.SetLayout(WorkspaceLayout.TwoRows));
        yield return ("Layout: 2×2 grid", "grid quad 4", () => Workspace.SetLayout(WorkspaceLayout.Grid));
        yield return ("Toggle multi-execution", "multiexec broadcast", () => IsMultiExecutionEnabled = !IsMultiExecutionEnabled);
        if (HasTeamLibrary)
        {
            yield return ("Sync team library  (Ctrl+S)", "team sync synchronisieren speichern share laufwerk", () => SyncTeamCommand.Execute(null));
            yield return ("Disconnect team library", "team trennen disconnect", () => DisconnectTeamLibraryCommand.Execute(null));
            yield return ("Set team file password …", "team password passwort schützen protect master", () => SetTeamPasswordCommand.Execute(null));
        }
        yield return (HasTeamLibrary ? "Reconnect team library …" : "Connect team library (shared confCons.xml) …",
            "team bibliothek library share laufwerk gemeinsam confcons keepass", () => ConnectTeamLibraryCommand.Execute(null));
        yield return ("Import mRemoteNG file", "import confcons", () => ImportMRemoteNGCommand.Execute(null));
        yield return ("Export as mRemoteNG file", "export confcons", () => ExportMRemoteNGCommand.Execute(null));
        yield return ("Switch theme (system / dark / light)", "theme dark light dunkel hell", () => ToggleThemeCommand.Execute(null));
        yield return ("New connection", "new connection add", () => NewConnectionCommand.Execute(null));
        yield return ("New folder", "new folder add", () => NewFolderCommand.Execute(null));
        yield return ("Manage credentials (vault)", "vault credentials passwort", () => ManageVaultCommand.Execute(null));
        yield return ("Change master password", "password security sicherheit", () => ChangeMasterPasswordCommand.Execute(null));
        if (FocusedWorkspace.ActiveTab is { IsConnecting: true } connecting)
            yield return ($"Cancel connecting: {connecting.Title}", "abbrechen cancel stop verbindung", connecting.CancelConnect);
        yield return ("Close all tabs", "close all tabs", CloseAllTabs);
        yield return ("Settings …", "settings optionen einstellungen preferences", () => OpenSettingsCommand.Execute(null));
        yield return ("Create backup …", "backup sicherung export datenbank", () => BackupCommand.Execute(null));
        yield return (Settings.ShowReachability ? "Hide online status in tree" : "Show online status in tree", "online status erreichbar ping", () =>
        {
            Settings.ShowReachability = !Settings.ShowReachability;
            SaveSettings();
            Reachability?.Invalidate();
            if (!Settings.ShowReachability) foreach (var i in SessionTree.AllItems) i.Reachability = Services.Reachability.Unknown;
        });
    }

    // ---- Arbeitsbereiche --------------------------------------------------------------------------

    public WorkspaceSnapshot CaptureWorkspace() => new()
    {
        Layout = Workspace.Layout,
        Panes = Workspace.Panes.Select(p => p.Tabs.Where(t => !t.IsTransient).Select(t => t.Connection.Id).ToList()).ToList(),
    };

    /// <summary>Öffnet die Sessions eines Arbeitsbereichs (zusätzlich zu bereits offenen Tabs).</summary>
    public int RestoreWorkspace(WorkspaceSnapshot snapshot)
    {
        if (WorkspaceViewModel.PaneCount(snapshot.Layout) > Workspace.Panes.Count) Workspace.SetLayout(snapshot.Layout);
        var opened = 0;
        for (var i = 0; i < snapshot.Panes.Count; i++)
        {
            var pane = Workspace.Panes[Math.Min(i, Workspace.Panes.Count - 1)];
            foreach (var id in snapshot.Panes[i])
            {
                if (SessionTree.Tree.Find(id) is not ConnectionNode node) continue; // inzwischen gelöscht
                Workspace.Open(CreateTab(node, SessionTree.Tree.Vault, transient: false), pane);
                opened++;
            }
        }
        if (Workspace.Panes.Count > 0) Workspace.ActivePane = Workspace.Panes[0];
        StatusText = $"Workspace restored: {opened} session(s)";
        return opened;
    }

    [RelayCommand]
    private void SaveWorkspace()
    {
        var snapshot = CaptureWorkspace();
        if (snapshot.ConnectionCount == 0)
        {
            StatusText = "No saved connections open – nothing to save.";
            return;
        }
        if (_dialogs.Prompt("Save workspace", "Workspace name:", "") is not { Length: > 0 } name) return;
        Settings.SavedWorkspaces[name.Trim()] = snapshot;
        SaveSettings();
        StatusText = $"Workspace “{name.Trim()}” saved ({snapshot.ConnectionCount} sessions) – open it via Ctrl+K";
    }

    /// <summary>Treffer ausführen. <paramref name="split"/>: in einem neuen Split-Bereich öffnen (Strg+Enter).</summary>
    public void ExecutePaletteItem(PaletteItem? item, bool split = false)
    {
        if (item is null) return;
        PaneViewModel? pane = null;
        if (split && item.Kind != PaletteItemKind.Action)
        {
            if (FocusedWorkspace.Layout == WorkspaceLayout.Single) FocusedWorkspace.SetLayout(WorkspaceLayout.TwoColumns);
            pane = FocusedWorkspace.Panes.FirstOrDefault(p => p.IsEmpty) ?? FocusedWorkspace.Panes.FirstOrDefault(p => p != FocusedWorkspace.ActivePane);
        }

        switch (item.Kind)
        {
            case PaletteItemKind.Connection:
                FocusedWorkspace.Open(CreateTab(item.Connection!, SessionTree.Tree.Vault, transient: false), pane);
                if (SessionTree.FindByNode(item.Connection!) is { } treeItem) RevealInTree(treeItem);
                StatusText = $"Opened: {item.Connection!.Path}";
                break;
            case PaletteItemKind.QuickConnect:
                FocusedWorkspace.Open(CreateTab(item.QuickConnect!.ToConnection(), new CredentialVault(), transient: true), pane);
                StatusText = $"Quick connect: {item.QuickConnect.Protocol.DisplayName()} → {item.QuickConnect.Host}:{item.QuickConnect.EffectivePort}";
                break;
            case PaletteItemKind.Action:
                item.Action!();
                break;
        }
    }

    /// <summary>Eintrag im Baum sichtbar machen (Ordner aufklappen, auswählen).</summary>
    public void RevealInTree(TreeItemViewModel item)
    {
        for (var p = item.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        item.IsSelected = true;
    }

    private void RememberRecent(Guid id)
    {
        var recent = Settings.RecentConnections;
        recent.Remove(id);
        recent.Insert(0, id);
        if (recent.Count > 30) recent.RemoveRange(30, recent.Count - 30);
        SaveSettings();
    }

    private void CloseAllTabs()
    {
        foreach (var ws in AllWorkspaces.ToList())
            foreach (var tab in ws.AllTabs.ToList()) ws.Close(tab);
    }
    public WorkspaceViewModel Workspace { get; } = new();
    public AppSettings Settings { get; }
    public IReadOnlyList<string> QuickConnectHistory => Settings.QuickConnectHistory;
    public bool DeviceUnlockSupported => _store.DeviceUnlockSupported;
    public string StoreLocation => _store.Location;

    [ObservableProperty] private string _quickConnectText = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private AppTheme _currentTheme;
    /// <summary>Tastatureingaben an alle markierten Terminal-Tabs senden (Engine folgt in Phase 4).</summary>
    [ObservableProperty] private bool _isMultiExecutionEnabled;
    [ObservableProperty] private bool _isDeviceUnlockEnabled;

    /// <summary>
    /// True erst, wenn die Datenbank erfolgreich geladen wurde. Vorher wird NIE gespeichert – sonst würde ein
    /// leerer Baum (nach einem Ladefehler) beim Beenden alle Verbindungen überschreiben.
    /// </summary>
    private bool _loadSucceeded;

    [ObservableProperty] private bool _isImporting;

    /// <summary>Nur für Tests/Aufrufer ohne UI-SynchronizationContext.</summary>
    public void Initialize(string? mRemoteNgDefaultFile = null) => InitializeAsync(mRemoteNgDefaultFile).GetAwaiter().GetResult();

    /// <summary>Beim Start: Theme anwenden, Bestand laden, beim ersten Start mRemoteNG-Übernahme anbieten.</summary>
    public async Task InitializeAsync(string? mRemoteNgDefaultFile = null)
    {
        _theme.Apply(Settings.Theme);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            SessionTree.Load(_store.Load());
            _loadSucceeded = true;
            AppLog.Info($"Database loaded: {SessionTree.Tree.Connections.Count()} connections, {SessionTree.Tree.Folders.Count()} folders, {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            AppLog.Error("Load database", ex);
            StatusText = "Database could not be loaded – nothing will be saved";
            _dialogs.ShowError("Connections could not be loaded",
                $"{ex.Message}\n\nTo protect your data, HostDeck will not save anything now. The database stays unchanged.\n" +
                "Please send the error log. Backups are in the “backups” folder next to the database.");
            return;
        }

        StatusText = $"{SessionTree.Tree.Connections.Count()} connections loaded";
        if (Settings.RestoreWorkspaceOnStart && Settings.LastWorkspace is { ConnectionCount: > 0 } last)
            RestoreWorkspace(last);

        await StartTeamLibraryAsync();

        Reachability = new ReachabilityMonitor(SessionTree, () => Settings.ShowReachability && !IsImporting, SynchronizationContext.Current);
        Reachability.Start();

        if (!Settings.FirstRunImportOffered && !SessionTree.Tree.Root.Children.Any()
            && mRemoteNgDefaultFile is not null && File.Exists(mRemoteNgDefaultFile))
        {
            Settings.FirstRunImportOffered = true;
            SaveSettings();
            if (_dialogs.Confirm("mRemoteNG found",
                    $"An mRemoteNG connection file was found:\n{mRemoteNgDefaultFile}\n\nImport these connections now?"))
                await TryImportAsync(mRemoteNgDefaultFile);
        }
    }

    /// <summary>Beim Beenden: Stand (inkl. Auf-/Zuklapp-Zustand) speichern.</summary>
    public void Shutdown()
    {
        OfferTeamSyncOnExit();
        Reachability?.Dispose();
        Settings.LastWorkspace = CaptureWorkspace();
        SaveSettings();
        Persist();
    }

    // ---- Persistenz -------------------------------------------------------------------------------

    private string BackupDirectory => Path.Combine(Path.GetDirectoryName(_store.Location) ?? ".", "backups");

    public bool Persist()
    {
        if (!_loadSucceeded)
        {
            AppLog.Info("Save skipped: database was not loaded successfully.");
            return false;
        }
        try
        {
            // Notbremse: Verschwinden auf einen Schlag mehr als die Hälfte der Verbindungen, vorher sichern.
            var before = _store.CountConnections();
            var after = SessionTree.Tree.Connections.Count();
            if (before >= 10 && after < before / 2)
            {
                var backup = _store.Backup(BackupDirectory);
                AppLog.Info($"Automatic backup before saving ({before} → {after} connections): {backup}");
            }
            _store.Save(SessionTree.Tree);
            _treeVersion++;
            if (HasTeamLibrary) RefreshTeamPending();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Data.Common.DbException)
        {
            AppLog.Error("Save", ex);
            StatusText = "Saving failed!";
            _dialogs.ShowError("Saving failed", ex.Message);
            return false;
        }
    }

    [RelayCommand]
    private void ChangeMasterPassword()
    {
        if (_dialogs.AskChangeMasterPassword() is not { } input) return;
        try
        {
            _store.ChangeMasterPassword(input.Current, input.New);
            StatusText = "Master password changed";
        }
        catch (UnauthorizedAccessException)
        {
            _dialogs.ShowError("Master password", "The current master password is wrong.");
        }
        catch (ArgumentException ex)
        {
            _dialogs.ShowError("Master password", ex.Message);
        }
    }

    partial void OnIsDeviceUnlockEnabledChanged(bool value)
    {
        if (value == _store.HasDeviceUnlock) return;
        try
        {
            _store.SetDeviceUnlock(value);
            StatusText = value ? "Unlock without password enabled on this PC" : "Unlock without password disabled";
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or System.Security.Cryptography.CryptographicException)
        {
            _dialogs.ShowError("Remember on this PC", ex.Message);
            IsDeviceUnlockEnabled = _store.HasDeviceUnlock;
        }
    }

    // ---- Verbinden --------------------------------------------------------------------------------

    [RelayCommand]
    private void Open(TreeItemViewModel? item)
    {
        switch (item?.Node)
        {
            case ConnectionNode connection:
                Workspace.Open(CreateTab(connection, SessionTree.Tree.Vault, transient: false));
                StatusText = $"Opened: {connection.Path}";
                break;
            case FolderNode:
                item.IsExpanded = !item.IsExpanded;
                break;
        }
    }

    /// <summary>Tree-Eintrag auf einen Bereich gezogen: Verbindung dort öffnen, Ordner im Split-Screen.</summary>
    public void OpenInPane(TreeItemViewModel item, PaneViewModel pane)
    {
        if (item.Node is ConnectionNode connection)
        {
            var owner = AllWorkspaces.FirstOrDefault(w => w.Panes.Contains(pane)) ?? Workspace;
            owner.Open(CreateTab(connection, SessionTree.Tree.Vault, transient: false), pane);
            StatusText = $"Opened: {connection.Path}";
        }
        else
        {
            OpenAllTiled(item);
        }
    }

    /// <summary>Öffnet alle Verbindungen eines Ordners im Split-Screen.</summary>
    [RelayCommand]
    private void OpenAllTiled(TreeItemViewModel? folder)
    {
        if (folder?.Node is not FolderNode f) return;
        var tabs = f.Descendants().OfType<ConnectionNode>()
            .Select(c => CreateTab(c, SessionTree.Tree.Vault, transient: false)).ToList();
        Workspace.OpenTiled(tabs);
        StatusText = $"{tabs.Count} sessions from “{f.Name}” opened";
    }

    /// <summary>
    /// Quick-Connect: zuerst exakter Treffer (Name oder Hostname) im Baum, sonst Eingabe parsen
    /// (<c>ssh user@host:22</c>, <c>rdp://host</c>, IP …).
    /// </summary>
    [RelayCommand]
    private void QuickConnect()
    {
        var text = QuickConnectText.Trim();
        if (text.Length == 0) return;

        var existing = SessionTree.Tree.Connections.FirstOrDefault(c =>
            string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Hostname, text, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            Workspace.Open(CreateTab(existing, SessionTree.Tree.Vault, transient: false));
            StatusText = $"Opened: {existing.Path}";
        }
        else if (QuickConnectParser.TryParse(text, Settings.QuickConnectDefaultProtocol, out var target))
        {
            Workspace.Open(CreateTab(target!.ToConnection(), new CredentialVault(), transient: true));
            StatusText = $"Quick connect: {target.Protocol.DisplayName()} → {target.Host}:{target.EffectivePort}";
        }
        else
        {
            StatusText = $"Quick connect: “{text}” not understood (example: ssh admin@10.0.0.1:22)";
            return;
        }

        RememberQuickConnect(text);
        QuickConnectText = "";
    }

    private void RememberQuickConnect(string text)
    {
        var history = Settings.QuickConnectHistory;
        history.RemoveAll(h => string.Equals(h, text, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, text);
        if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
        SaveSettings();
        OnPropertyChanged(nameof(QuickConnectHistory));
    }

    /// <summary>Quick-Connect-Session dauerhaft in den Baum übernehmen.</summary>
    [RelayCommand]
    private void SaveTransientSession(SessionTabViewModel? tab)
    {
        if (tab is not { IsTransient: true }) return;
        SessionTree.Add(tab.Connection);
        StatusText = $"“{tab.Connection.Name}” saved";
    }

    // ---- Bearbeiten -------------------------------------------------------------------------------

    [RelayCommand]
    private void NewFolder() => SessionTree.Add(new FolderNode("New folder") { IsExpanded = true });

    [RelayCommand]
    private void NewConnection()
    {
        var item = SessionTree.Add(new ConnectionNode("New connection") { Hostname = "" });
        if (!Edit(item))
        {
            SessionTree.Delete(item); // abgebrochen ⇒ nicht als leere Verbindung stehen lassen
        }
    }

    /// <summary>
    /// Duplizieren (Strg+D / Kontextmenü): Kopie direkt unter dem Original, alle Einstellungen und
    /// Zugangsdaten übernommen. Bei Verbindungen öffnet sich sofort der Eigenschaften-Dialog, damit nur
    /// noch Name und Host angepasst werden müssen; Abbrechen verwirft die Kopie.
    /// </summary>
    [RelayCommand]
    private void Duplicate(TreeItemViewModel? item)
    {
        if (item is null) return;
        var copy = DeepCopy(item.Node);
        copy.Name = $"{item.Node.Name} (copy)";
        var vm = SessionTree.InsertAfter(item, copy);
        if (copy is ConnectionNode)
        {
            if (!Edit(vm)) SessionTree.Delete(vm);
            return;
        }
        Persist();
        StatusText = $"Duplicated: {copy.Path}";
    }

    private static TreeNode DeepCopy(TreeNode node)
    {
        TreeNode copy = node switch
        {
            FolderNode f => new FolderNode(f.Name) { IsExpanded = f.IsExpanded },
            ConnectionNode c => new ConnectionNode(c.Name) { Hostname = c.Hostname },
            _ => throw new NotSupportedException(node.GetType().Name),
        };
        copy.Settings = node.Settings.Clone();
        foreach (var (k, v) in node.Extensions) copy.Extensions[k] = v;
        if (node is FolderNode folder)
            foreach (var child in folder.Children)
                ((FolderNode)copy).Add(DeepCopy(child));
        return copy;
    }

    /// <summary>Eigenschaften bearbeiten (F4 / Doppelklick mit Alt / Kontextmenü).</summary>
    [RelayCommand]
    private void EditItem(TreeItemViewModel? item) => Edit(item);

    private bool Edit(TreeItemViewModel? item)
    {
        if (item is null) return false;
        var editor = new ConnectionEditorViewModel(item.Node, SessionTree.Tree.Vault);
        if (!_dialogs.EditConnection(editor)) return false;
        item.NotifyNameChanged();
        item.RefreshDisplay(); // Ordner: Kinder erben ggf. neue Werte
        SessionTree.ApplyFilter();
        Persist();
        StatusText = $"Saved: {item.Node.Path}";
        return true;
    }

    [RelayCommand]
    private void Rename(TreeItemViewModel? item)
    {
        if (item is null || _dialogs.Prompt("Rename", "New name:", item.Name) is not { Length: > 0 } name) return;
        item.Name = name;
        Persist();
    }

    /// <summary>Nach „Einstellungen speichern“ (z. B. Update-Quelle neu prüfen).</summary>
    public event Action? SettingsApplied;

    [RelayCommand]
    private void OpenSettings()
    {
        var editor = new SettingsViewModel(Settings, !string.IsNullOrEmpty(_store.ReadDocument(SettingsViewModel.UpdateTokenDocument)));
        if (!_dialogs.EditSettings(editor)) return;
        var themeChanged = editor.ThemeChanged;
        editor.Apply();
        if (editor.UpdateToken.Trim() is { Length: > 0 } token)
        {
            _store.WriteDocument(SettingsViewModel.UpdateTokenDocument, token); // verschlüsselt in der Datenbank
            Settings.LastUpdateCheckUtc = null;
        }
        SaveSettings();
        SettingsApplied?.Invoke();
        ApplyTeamSettings(editor.TeamLibraryPath.Trim(), editor.TeamReadOnly);
        if (themeChanged) { CurrentTheme = Settings.Theme; _theme.Apply(Settings.Theme); }
        Reachability?.Invalidate();
        if (!Settings.ShowReachability) foreach (var i in SessionTree.AllItems) i.Reachability = Services.Reachability.Unknown;
        foreach (var t in AllTabs.Select(t => t.Terminal).OfType<TerminalSessionViewModel>()) t.HighlightingEnabled = Settings.TerminalHighlighting;
        StatusText = "Settings saved";
    }

    [RelayCommand]
    private void Backup()
    {
        if (_dialogs.PickFolder("Target folder for the backup") is not { } folder) return;
        try
        {
            Persist();
            var file = _store.Backup(folder);
            _dialogs.ShowInfo("Backup created",
                $"{file}\n{file}.key\n\nBoth files belong together. To restore: close HostDeck and copy the files as " +
                "connections.hdk / connections.hdk.key to %APPDATA%\\HostDeck. They open with the master password that was valid at the time of the backup.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            _dialogs.ShowError("Backup failed", ex.Message);
        }
    }

    [RelayCommand]
    private void ManageVault()
    {
        _dialogs.ManageVault(new CredentialVaultViewModel(SessionTree.Tree));
        SessionTree.Load(SessionTree.Tree); // Anzeige (z. B. Benutzer in Tooltips) aktualisieren
        Persist();
    }

    [RelayCommand]
    private void Delete(TreeItemViewModel? item)
    {
        if (item is null) return;
        SessionTree.Delete(item);
        StatusText = $"Deleted: {item.Name}";
    }

    // ---- Layout / Theme ---------------------------------------------------------------------------

    public string ThemeToolTip => CurrentTheme switch
    {
        AppTheme.System => "Theme: System (follows Windows) – click for Dark",
        AppTheme.Dark => "Theme: Dark – click for Light",
        _ => "Theme: Light – click for System",
    };

    partial void OnCurrentThemeChanged(AppTheme value) => OnPropertyChanged(nameof(ThemeToolTip));

    [RelayCommand]
    private void ToggleTheme()
    {
        // System → Dark → Light → System
        CurrentTheme = CurrentTheme switch { AppTheme.System => AppTheme.Dark, AppTheme.Dark => AppTheme.Light, _ => AppTheme.System };
        Settings.Theme = CurrentTheme;
        _theme.Apply(CurrentTheme);
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleMultiExecution() => IsMultiExecutionEnabled = !IsMultiExecutionEnabled;

    // ---- Import / Export --------------------------------------------------------------------------

    [RelayCommand]
    private async Task ImportMRemoteNG()
    {
        var path = _dialogs.OpenFile("Import mRemoteNG connections", "mRemoteNG (confCons.xml)|*.xml|All files|*.*");
        if (path is not null) await TryImportAsync(path);
    }

    /// <summary>Nur für Aufrufer ohne UI-SynchronizationContext (Tests).</summary>
    public bool TryImport(string path) => TryImportAsync(path).GetAwaiter().GetResult();

    /// <summary>
    /// Import im Hintergrund (Oberfläche bleibt bedienbar). Leerer Bestand ⇒ Import wird übernommen, sonst landet
    /// er in einem neuen Ordner „Import &lt;Datei&gt; &lt;Datum&gt;“ – bestehende Verbindungen werden nie überschrieben.
    /// </summary>
    public async Task<bool> TryImportAsync(string path)
    {
        if (IsImporting) return false;
        if (!_loadSucceeded)
        {
            if (!_dialogs.Confirm("Rebuild?",
                    "The existing database could not be loaded. Import into a new database?\n\n" +
                    "The existing database is backed up to the “backups” folder first."))
                return false;
            try { AppLog.Info($"Backup before rebuild: {_store.Backup(BackupDirectory)}"); }
            catch (Exception ex) { AppLog.Error("Backup before rebuild", ex); }
            _loadSucceeded = true;
        }

        IsImporting = true;
        StatusText = $"Importing {Path.GetFileName(path)} …";
        var ui = SynchronizationContext.Current;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Passwortdialog im UI-Thread, Import selbst im Hintergrund
            string? AskPassword(MasterPasswordRequest r)
            {
                string? answer = null;
                void Ask() => answer = _dialogs.AskMasterPassword(r.SourceName, r.PreviousAttemptFailed, r.Attempt, r.MaxAttempts);
                if (ui is null || SynchronizationContext.Current == ui) Ask();
                else ui.Send(_ => Ask(), null);
                return answer;
            }

            var importer = new ConfConsImporter(new ConfConsImportOptions
            {
                CredentialExtraction = Settings.CredentialExtraction,
                MasterPasswordProvider = AskPassword,
            });

            ConfConsImportResult result;
            try
            {
                result = await Task.Run(() => importer.ImportFile(path));
            }
            catch (ConfConsAuthenticationException ex) when (ex.Cancelled)
            {
                StatusText = "Import cancelled";
                return false;
            }
            catch (Exception ex) when (ex is ConfConsAuthenticationException or ConfConsFormatException or IOException or UnauthorizedAccessException)
            {
                StatusText = "Import failed";
                _dialogs.ShowError("Import failed", ex.Message);
                return false;
            }
            AppLog.Info($"Import read: {result.Statistics.Connections} connections, {result.Statistics.Folders} folders, {sw.ElapsedMilliseconds} ms");

            var current = SessionTree.Tree;
            if (!current.Root.Children.Any() && current.Vault.Count == 0)
            {
                SessionTree.Load(result.Tree);
            }
            else
            {
                MergeInto(current, result.Tree, $"Import {Path.GetFileNameWithoutExtension(path)} {DateTime.Now:yyyy-MM-dd HH-mm}");
                SessionTree.Load(current);
            }
            AppLog.Info($"Import shown: {sw.ElapsedMilliseconds} ms");
            OfferSecurityFixes(result.Tree);
            Persist();
            AppLog.Info($"Import saved: {sw.ElapsedMilliseconds} ms");

            Settings.LastImportPath = path;
            SaveSettings();
            StatusText = $"{result.Statistics.Connections} connections in {result.Statistics.Folders} folders imported"
                         + (result.Warnings.Count > 0 ? $" – {result.Warnings.Count} warning(s)" : "");
            if (result.Warnings.Count > 0)
                _dialogs.ShowInfo("Import finished with warnings", string.Join(Environment.NewLine, result.Warnings.Take(30)));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Import", ex);
            StatusText = "Import failed";
            _dialogs.ShowError("Import failed", ex.Message);
            return false;
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// mRemoteNG speichert „Server-Zertifikat nicht prüfen“ als Standard explizit an jedem Knoten. Anbieten,
    /// diese Einträge auf den sicheren HostDeck-Standard (warnen) zurückzusetzen.
    /// </summary>
    internal int OfferSecurityFixes(ConnectionTree imported)
    {
        var insecure = imported.Root.Descendants().Where(n => n.Settings.Rdp.AuthenticationLevel == RdpAuthenticationLevel.NoAuth).ToList();
        var affectedConnections = imported.Connections.Count(c =>
            Core.Inheritance.SettingsResolver.Resolve(c) is { Protocol: ProtocolKind.Rdp, Rdp.AuthenticationLevel: RdpAuthenticationLevel.NoAuth });
        if (affectedConnections == 0) return 0;
        if (!_dialogs.Confirm("Security check",
                $"{affectedConnections} RDP connection(s) do not verify the server certificate (mRemoteNG default). " +
                "This allows man-in-the-middle attacks to go unnoticed.\n\n" +
                "Switch to the secure default “warn on invalid certificate”? (recommended)"))
            return 0;
        foreach (var node in insecure) node.Settings.Rdp.AuthenticationLevel = null; // erbt sicheren Standard
        StatusText = $"{affectedConnections} RDP connection(s) switched to certificate verification";
        return affectedConnections;
    }

    internal static void MergeInto(ConnectionTree target, ConnectionTree imported, string folderName)
    {
        var existingIds = target.Root.Descendants().Select(n => n.Id).Append(target.Root.Id).ToHashSet();
        // Dieselbe Datei erneut importiert ⇒ IDs kollidieren ⇒ Kopie mit neuen IDs anlegen
        var needsNewIds = imported.Root.Descendants().Any(n => existingIds.Contains(n.Id));

        var folder = target.Root.Add(new FolderNode(folderName) { IsExpanded = true });
        foreach (var child in imported.Root.Children.ToList())
            folder.Add(needsNewIds ? CloneWithNewIds(child) : child);
        foreach (var credential in imported.Vault.Items.ToList())
        {
            if (target.Vault.Get(credential.Id) is null) target.Vault.Add(credential);
        }
    }

    private static TreeNode CloneWithNewIds(TreeNode node)
    {
        TreeNode copy = node switch
        {
            FolderNode f => new FolderNode(f.Name) { IsExpanded = f.IsExpanded },
            ConnectionNode c => new ConnectionNode(c.Name) { Hostname = c.Hostname },
            _ => throw new NotSupportedException(node.GetType().Name),
        };
        copy.Settings = node.Settings; // Quellbaum wird verworfen ⇒ Teilen ist unkritisch
        foreach (var (k, v) in node.Extensions) copy.Extensions[k] = v;
        if (node is FolderNode folder)
            foreach (var child in folder.Children)
                ((FolderNode)copy).Add(CloneWithNewIds(child));
        return copy;
    }

    [RelayCommand]
    private void ExportMRemoteNG()
    {
        var path = _dialogs.SaveFile("Export as mRemoteNG file", "mRemoteNG (confCons.xml)|*.xml", "confCons.xml");
        if (path is null) return;
        try
        {
            new ConfConsExporter().ExportFile(SessionTree.Tree, path);
            StatusText = $"Exported: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    public void SaveSettings()
    {
        try { _settingsStore.Save(Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = $"Settings not saved: {ex.Message}"; }
    }
}
