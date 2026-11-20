using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.QuickConnect;
using HostDeck.Presentation.Services;

namespace HostDeck.Presentation.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxHistory = 20;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settingsStore;
    private readonly IThemeService _theme;

    public MainViewModel(IDialogService dialogs, ISettingsStore settingsStore, IThemeService theme)
    {
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _theme = theme;
        Settings = settingsStore.Load();
        _currentTheme = Settings.Theme;
    }

    public SessionTreeViewModel SessionTree { get; } = new();
    public WorkspaceViewModel Workspace { get; } = new();
    public AppSettings Settings { get; }
    public IReadOnlyList<string> QuickConnectHistory => Settings.QuickConnectHistory;

    [ObservableProperty] private string _quickConnectText = "";
    [ObservableProperty] private string _statusText = "Bereit";
    [ObservableProperty] private AppTheme _currentTheme;
    /// <summary>Tastatureingaben an alle markierten Terminal-Tabs senden (Engine folgt in Phase 4).</summary>
    [ObservableProperty] private bool _isMultiExecutionEnabled;

    /// <summary>Beim Start: Theme anwenden und zuletzt importierte Datei erneut laden.</summary>
    public void Initialize()
    {
        _theme.Apply(Settings.Theme);
        if (Settings.LastImportPath is { } path && File.Exists(path))
            TryImport(path, silentOnCancel: true);
    }

    // ---- Verbinden --------------------------------------------------------------------------------

    [RelayCommand]
    private void Open(TreeItemViewModel? item)
    {
        switch (item?.Node)
        {
            case ConnectionNode connection:
                Workspace.Open(new SessionTabViewModel(connection, SessionTree.Tree.Vault, isTransient: false));
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
            Workspace.Open(new SessionTabViewModel(connection, SessionTree.Tree.Vault, isTransient: false), pane);
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
            .Select(c => new SessionTabViewModel(c, SessionTree.Tree.Vault, isTransient: false)).ToList();
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
            Workspace.Open(new SessionTabViewModel(existing, SessionTree.Tree.Vault, isTransient: false));
            StatusText = $"Geöffnet: {existing.Path}";
        }
        else if (QuickConnectParser.TryParse(text, Settings.QuickConnectDefaultProtocol, out var target))
        {
            Workspace.Open(new SessionTabViewModel(target!.ToConnection(), new CredentialVault(), isTransient: true));
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
    private void NewConnection() =>
        SessionTree.Add(new ConnectionNode("Neue Verbindung") { Hostname = "" });

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
        if (path is not null) TryImport(path, silentOnCancel: false);
    }

    public bool TryImport(string path, bool silentOnCancel)
    {
        try
        {
            var importer = new ConfConsImporter(new ConfConsImportOptions
            {
                CredentialExtraction = Settings.CredentialExtraction,
                MasterPasswordProvider = r => _dialogs.AskMasterPassword(r.SourceName, r.PreviousAttemptFailed, r.Attempt, r.MaxAttempts),
            });
            var result = importer.ImportFile(path);
            SessionTree.Load(result.Tree);

            Settings.LastImportPath = path;
            SaveSettings();

            StatusText = $"{result.Statistics.Connections} Verbindungen in {result.Statistics.Folders} Ordnern importiert"
                         + (result.Warnings.Count > 0 ? $" – {result.Warnings.Count} Warnung(en)" : "");
            if (result.Warnings.Count > 0 && !silentOnCancel)
                _dialogs.ShowInfo("Import abgeschlossen mit Warnungen", string.Join(Environment.NewLine, result.Warnings.Take(30)));
            return true;
        }
        catch (ConfConsAuthenticationException ex) when (ex.Cancelled && silentOnCancel)
        {
            StatusText = "Laden abgebrochen";
        }
        catch (Exception ex) when (ex is ConfConsAuthenticationException or ConfConsFormatException or IOException or UnauthorizedAccessException)
        {
            StatusText = "Import fehlgeschlagen";
            _dialogs.ShowError("Import fehlgeschlagen", ex.Message);
        }
        return false;
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
