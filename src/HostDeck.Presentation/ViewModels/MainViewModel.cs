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
            StatusText = "Kein verbundenes Terminal aktiv – Snippet nicht gesendet.";
        }
    }

    // ---- Schnellsuche (Strg+K) ------------------------------------------------------------------------

    private CommandPaletteViewModel? _palette;

    public CommandPaletteViewModel Palette => _palette ??= new CommandPaletteViewModel(
        () => SessionTree.Tree,
        () => Settings.RecentConnections,
        () => Settings.QuickConnectDefaultProtocol,
        PaletteActions);

    private IEnumerable<(string Title, string Keywords, Action Run)> PaletteActions()
    {
        foreach (var (name, snapshot) in Settings.SavedWorkspaces.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
            yield return ($"Arbeitsbereich öffnen: {name}  ({snapshot.ConnectionCount} Sessions)", "workspace arbeitsbereich " + name, () => RestoreWorkspace(snapshot));
        yield return ("Arbeitsbereich speichern unter …", "workspace arbeitsbereich save speichern", () => SaveWorkspaceCommand.Execute(null));
        foreach (var name in Settings.SavedWorkspaces.Keys.Order())
            yield return ($"Arbeitsbereich löschen: {name}", "workspace arbeitsbereich delete löschen " + name, () => { Settings.SavedWorkspaces.Remove(name); SaveSettings(); });
        yield return ("Layout: ein Bereich", "single layout 1", () => Workspace.SetLayout(WorkspaceLayout.Single));
        yield return ("Layout: zwei nebeneinander", "split vertical columns 2", () => Workspace.SetLayout(WorkspaceLayout.TwoColumns));
        yield return ("Layout: zwei untereinander", "split horizontal rows 2", () => Workspace.SetLayout(WorkspaceLayout.TwoRows));
        yield return ("Layout: 4er-Grid", "grid quad 4", () => Workspace.SetLayout(WorkspaceLayout.Grid));
        yield return ("Multi-Execution umschalten", "multiexec broadcast", () => IsMultiExecutionEnabled = !IsMultiExecutionEnabled);
        yield return ("mRemoteNG-Datei importieren", "import confcons", () => ImportMRemoteNGCommand.Execute(null));
        yield return ("Als mRemoteNG-Datei exportieren", "export confcons", () => ExportMRemoteNGCommand.Execute(null));
        yield return ("Hell/Dunkel umschalten", "theme dark light dunkel hell", () => ToggleThemeCommand.Execute(null));
        yield return ("Neue Verbindung", "new connection add", () => NewConnectionCommand.Execute(null));
        yield return ("Neuer Ordner", "new folder add", () => NewFolderCommand.Execute(null));
        yield return ("Zugangsdaten verwalten (Vault)", "vault credentials passwort", () => ManageVaultCommand.Execute(null));
        yield return ("Master-Passwort ändern", "password security sicherheit", () => ChangeMasterPasswordCommand.Execute(null));
        yield return ("Alle Tabs schließen", "close all tabs", CloseAllTabs);
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
        StatusText = $"Arbeitsbereich wiederhergestellt: {opened} Session(s)";
        return opened;
    }

    [RelayCommand]
    private void SaveWorkspace()
    {
        var snapshot = CaptureWorkspace();
        if (snapshot.ConnectionCount == 0)
        {
            StatusText = "Keine gespeicherten Verbindungen geöffnet – nichts zu speichern.";
            return;
        }
        if (_dialogs.Prompt("Arbeitsbereich speichern", "Name des Arbeitsbereichs:", "") is not { Length: > 0 } name) return;
        Settings.SavedWorkspaces[name.Trim()] = snapshot;
        SaveSettings();
        StatusText = $"Arbeitsbereich „{name.Trim()}“ gespeichert ({snapshot.ConnectionCount} Sessions) – über Strg+K abrufbar";
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
                StatusText = $"Geöffnet: {item.Connection!.Path}";
                break;
            case PaletteItemKind.QuickConnect:
                FocusedWorkspace.Open(CreateTab(item.QuickConnect!.ToConnection(), new CredentialVault(), transient: true), pane);
                StatusText = $"Quick-Connect: {item.QuickConnect.Protocol} → {item.QuickConnect.Host}:{item.QuickConnect.EffectivePort}";
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
    [ObservableProperty] private string _statusText = "Bereit";
    [ObservableProperty] private AppTheme _currentTheme;
    /// <summary>Tastatureingaben an alle markierten Terminal-Tabs senden (Engine folgt in Phase 4).</summary>
    [ObservableProperty] private bool _isMultiExecutionEnabled;
    [ObservableProperty] private bool _isDeviceUnlockEnabled;

    /// <summary>Beim Start: Theme anwenden, Bestand laden, beim ersten Start mRemoteNG-Übernahme anbieten.</summary>
    public void Initialize(string? mRemoteNgDefaultFile = null)
    {
        _theme.Apply(Settings.Theme);
        SessionTree.Load(_store.Load());
        StatusText = $"{SessionTree.Tree.Connections.Count()} Verbindungen geladen";
        if (Settings.RestoreWorkspaceOnStart && Settings.LastWorkspace is { ConnectionCount: > 0 } last)
            RestoreWorkspace(last);

        if (!Settings.FirstRunImportOffered && !SessionTree.Tree.Root.Children.Any()
            && mRemoteNgDefaultFile is not null && File.Exists(mRemoteNgDefaultFile))
        {
            Settings.FirstRunImportOffered = true;
            SaveSettings();
            if (_dialogs.Confirm("mRemoteNG gefunden",
                    $"Es wurde eine mRemoteNG-Verbindungsdatei gefunden:\n{mRemoteNgDefaultFile}\n\nVerbindungen jetzt übernehmen?"))
                TryImport(mRemoteNgDefaultFile);
        }
    }

    /// <summary>Beim Beenden: Stand (inkl. Auf-/Zuklapp-Zustand) speichern.</summary>
    public void Shutdown()
    {
        Settings.LastWorkspace = CaptureWorkspace();
        SaveSettings();
        Persist();
    }

    // ---- Persistenz -------------------------------------------------------------------------------

    public bool Persist()
    {
        try
        {
            _store.Save(SessionTree.Tree);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Data.Common.DbException)
        {
            StatusText = "Speichern fehlgeschlagen!";
            _dialogs.ShowError("Speichern fehlgeschlagen", ex.Message);
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
            StatusText = "Master-Passwort geändert";
        }
        catch (UnauthorizedAccessException)
        {
            _dialogs.ShowError("Master-Passwort", "Das aktuelle Master-Passwort ist falsch.");
        }
        catch (ArgumentException ex)
        {
            _dialogs.ShowError("Master-Passwort", ex.Message);
        }
    }

    partial void OnIsDeviceUnlockEnabledChanged(bool value)
    {
        if (value == _store.HasDeviceUnlock) return;
        try
        {
            _store.SetDeviceUnlock(value);
            StatusText = value ? "Entsperren ohne Passwort auf diesem PC aktiviert" : "Entsperren ohne Passwort deaktiviert";
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or System.Security.Cryptography.CryptographicException)
        {
            _dialogs.ShowError("Auf diesem PC merken", ex.Message);
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
                StatusText = $"Geöffnet: {connection.Path}";
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
            StatusText = $"Geöffnet: {connection.Path}";
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
        StatusText = $"{tabs.Count} Sessions aus „{f.Name}“ geöffnet";
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
            StatusText = $"Geöffnet: {existing.Path}";
        }
        else if (QuickConnectParser.TryParse(text, Settings.QuickConnectDefaultProtocol, out var target))
        {
            Workspace.Open(CreateTab(target!.ToConnection(), new CredentialVault(), transient: true));
            StatusText = $"Quick-Connect: {target.Protocol} → {target.Host}:{target.EffectivePort}";
        }
        else
        {
            StatusText = $"Quick-Connect: „{text}“ nicht verstanden (Beispiel: ssh admin@10.0.0.1:22)";
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
        StatusText = $"„{tab.Connection.Name}“ gespeichert";
    }

    // ---- Bearbeiten -------------------------------------------------------------------------------

    [RelayCommand]
    private void NewFolder() => SessionTree.Add(new FolderNode("Neuer Ordner") { IsExpanded = true });

    [RelayCommand]
    private void NewConnection()
    {
        var item = SessionTree.Add(new ConnectionNode("Neue Verbindung") { Hostname = "" });
        if (!Edit(item))
        {
            SessionTree.Delete(item); // abgebrochen ⇒ nicht als leere Verbindung stehen lassen
        }
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
        StatusText = $"Gespeichert: {item.Node.Path}";
        return true;
    }

    [RelayCommand]
    private void Rename(TreeItemViewModel? item)
    {
        if (item is null || _dialogs.Prompt("Umbenennen", "Neuer Name:", item.Name) is not { Length: > 0 } name) return;
        item.Name = name;
        Persist();
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
        StatusText = $"Gelöscht: {item.Name}";
    }

    // ---- Layout / Theme ---------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        Settings.Theme = CurrentTheme;
        _theme.Apply(CurrentTheme);
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleMultiExecution() => IsMultiExecutionEnabled = !IsMultiExecutionEnabled;

    // ---- Import / Export --------------------------------------------------------------------------

    [RelayCommand]
    private void ImportMRemoteNG()
    {
        var path = _dialogs.OpenFile("mRemoteNG-Verbindungen importieren", "mRemoteNG (confCons.xml)|*.xml|Alle Dateien|*.*");
        if (path is not null) TryImport(path);
    }

    /// <summary>
    /// Leerer Bestand ⇒ Import wird übernommen. Sonst landet er in einem neuen Ordner
    /// „Import &lt;Datei&gt; &lt;Datum&gt;“ – bestehende Verbindungen werden nie überschrieben.
    /// </summary>
    public bool TryImport(string path)
    {
        ConfConsImportResult result;
        try
        {
            var importer = new ConfConsImporter(new ConfConsImportOptions
            {
                CredentialExtraction = Settings.CredentialExtraction,
                MasterPasswordProvider = r => _dialogs.AskMasterPassword(r.SourceName, r.PreviousAttemptFailed, r.Attempt, r.MaxAttempts),
            });
            result = importer.ImportFile(path);
        }
        catch (ConfConsAuthenticationException ex) when (ex.Cancelled)
        {
            StatusText = "Import abgebrochen";
            return false;
        }
        catch (Exception ex) when (ex is ConfConsAuthenticationException or ConfConsFormatException or IOException or UnauthorizedAccessException)
        {
            StatusText = "Import fehlgeschlagen";
            _dialogs.ShowError("Import fehlgeschlagen", ex.Message);
            return false;
        }

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
        OfferSecurityFixes(result.Tree);
        Persist();

        Settings.LastImportPath = path;
        SaveSettings();
        StatusText = $"{result.Statistics.Connections} Verbindungen in {result.Statistics.Folders} Ordnern importiert"
                     + (result.Warnings.Count > 0 ? $" – {result.Warnings.Count} Warnung(en)" : "");
        if (result.Warnings.Count > 0)
            _dialogs.ShowInfo("Import abgeschlossen mit Warnungen", string.Join(Environment.NewLine, result.Warnings.Take(30)));
        return true;
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
        if (!_dialogs.Confirm("Sicherheitsprüfung",
                $"{affectedConnections} RDP-Verbindung(en) prüfen das Server-Zertifikat nicht (mRemoteNG-Standard). " +
                "Damit sind Man-in-the-Middle-Angriffe unbemerkt möglich.\n\n" +
                "Auf den sicheren Standard „bei ungültigem Zertifikat warnen“ umstellen? (empfohlen)"))
            return 0;
        foreach (var node in insecure) node.Settings.Rdp.AuthenticationLevel = null; // erbt sicheren Standard
        StatusText = $"{affectedConnections} RDP-Verbindung(en) auf Zertifikatsprüfung umgestellt";
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
        var path = _dialogs.SaveFile("Als mRemoteNG-Datei exportieren", "mRemoteNG (confCons.xml)|*.xml", "confCons.xml");
        if (path is null) return;
        try
        {
            new ConfConsExporter().ExportFile(SessionTree.Tree, path);
            StatusText = $"Exportiert: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Export fehlgeschlagen", ex.Message);
        }
    }

    public void SaveSettings()
    {
        try { _settingsStore.Save(Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = $"Einstellungen nicht gespeichert: {ex.Message}"; }
    }
}
