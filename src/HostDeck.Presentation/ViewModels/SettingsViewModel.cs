using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.Services;

namespace HostDeck.Presentation.ViewModels;

/// <summary>Einstellungen-Dialog: arbeitet auf einer Kopie, übernimmt erst mit <see cref="Apply"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    /// <summary>Name des verschlüsselten Dokuments mit dem GitHub-Token für Updates aus privaten Repos.</summary>
    public const string UpdateTokenDocument = "github-update-token";

    public SettingsViewModel(AppSettings settings, bool hasUpdateToken = false)
    {
        HasUpdateToken = hasUpdateToken;
        _settings = settings;
        _theme = settings.Theme;
        _defaultProtocol = settings.QuickConnectDefaultProtocol;
        _credentialExtraction = settings.CredentialExtraction;
        _highlighting = settings.TerminalHighlighting;
        _sftpFollows = settings.SftpFollowsTerminal;
        _rightClickMenu = settings.TerminalRightClickMenu;
        _closeTabOnSessionEnd = settings.CloseTabOnSessionEnd;
        _confirmMultiLinePaste = settings.ConfirmMultiLinePaste;
        _restoreWorkspace = settings.RestoreWorkspaceOnStart;
        _showReachability = settings.ShowReachability;
        // Per Strg+Mausrad gewählte Größe (z. B. 17) ist evtl. nicht in der Liste ⇒ ergänzen statt still auf 14 zurückzusetzen
        var size = Math.Clamp(settings.TerminalFontSize, 8, 32);
        if (!FontSizes.Contains(size)) FontSizes = [.. FontSizes.Append(size).Order()];
        _fontSize = size;
        _updateSource = settings.UpdateSource ?? "";
        _autoUpdate = settings.AutoUpdate;
        _autoBackup = settings.AutoBackup;
        _autoLock = AutoLockOptions.FirstOrDefault(o => o.Minutes == settings.AutoLockMinutes) ?? AutoLockOptions[0];
        _lockWithWindows = settings.LockWithWindows;
        _backupFolder = settings.BackupFolder ?? "";
        _keepDaily = settings.BackupKeepDaily;
        _keepWeekly = settings.BackupKeepWeekly;
        _keepMonthly = settings.BackupKeepMonthly;
        LastUpdateCheckText = settings.LastUpdateCheckUtc is { } last
            ? $"Last checked: {last.ToLocalTime():g}" : "Not checked yet";
    }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();
    public IReadOnlyList<int> FontSizes { get; } = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];
    public IReadOnlyList<ProtocolKind> Protocols { get; } = [ProtocolKind.Ssh2, ProtocolKind.Rdp, ProtocolKind.Telnet, ProtocolKind.Vnc, ProtocolKind.Https];
    public IReadOnlyList<CredentialExtractionMode> ExtractionModes { get; } = Enum.GetValues<CredentialExtractionMode>();

    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private ProtocolKind _defaultProtocol;
    [ObservableProperty] private CredentialExtractionMode _credentialExtraction;
    [ObservableProperty] private bool _highlighting;
    [ObservableProperty] private bool _sftpFollows;
    [ObservableProperty] private bool _rightClickMenu;
    [ObservableProperty] private bool _closeTabOnSessionEnd;
    [ObservableProperty] private bool _confirmMultiLinePaste;
    [ObservableProperty] private bool _restoreWorkspace;
    [ObservableProperty] private bool _showReachability;
    [ObservableProperty] private int _fontSize;
    [ObservableProperty] private string _updateSource;

    // ---- Bibliotheken (werden nicht in Apply übernommen – Verbinden/Trennen erledigt das MainViewModel) ----

    public sealed partial class LibraryRow : ObservableObject
    {
        /// <summary>null = neu hinzugefügt.</summary>
        public string? Id { get; init; }
        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _path = "";
        [ObservableProperty] private bool _readOnly;
    }

    public System.Collections.ObjectModel.ObservableCollection<LibraryRow> Libraries { get; } = [];

    /// <summary>Verbundene Bibliotheken (vom MainViewModel gesetzt).</summary>
    public IEnumerable<LibraryRow> LibraryRows
    {
        init { foreach (var row in value) Libraries.Add(row); }
    }

    /// <summary>Dateiauswahl (vom Fenster gesetzt): aktueller Pfad ⇒ gewählter Pfad oder null.</summary>
    public Func<string, string?>? PickLibraryFile { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void AddLibrary()
    {
        var path = PickLibraryFile?.Invoke("");
        if (path is null) return;
        Libraries.Add(new LibraryRow { Name = MainViewModel.SuggestLibraryName(path), Path = path });
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void BrowseLibrary(LibraryRow? row)
    {
        if (row is null || PickLibraryFile?.Invoke(row.Path) is not { } path) return;
        row.Path = path;
        if (row.Id is null && row.Name.Trim().Length == 0) row.Name = MainViewModel.SuggestLibraryName(path);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void RemoveLibrary(LibraryRow? row)
    {
        if (row is not null) Libraries.Remove(row);
    }
    [ObservableProperty] private bool _autoUpdate;

    // ---- Sperre ----
    public sealed record LockOption(int Minutes, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<LockOption> AutoLockOptions { get; } =
        [new(0, "Off"), new(5, "after 5 minutes"), new(10, "after 10 minutes"), new(15, "after 15 minutes"), new(30, "after 30 minutes"), new(60, "after 1 hour")];

    [ObservableProperty] private LockOption _autoLock;
    [ObservableProperty] private bool _lockWithWindows;

    // ---- Sicherungen ----
    [ObservableProperty] private bool _autoBackup;
    [ObservableProperty] private string _backupFolder;
    [ObservableProperty] private int _keepDaily;
    [ObservableProperty] private int _keepWeekly;
    [ObservableProperty] private int _keepMonthly;
    public IReadOnlyList<int> DailyOptions { get; } = [0, 3, 7, 14, 30];
    public IReadOnlyList<int> WeeklyOptions { get; } = [0, 4, 8, 12, 26];
    public IReadOnlyList<int> MonthlyOptions { get; } = [0, 3, 6, 12, 24, 36];

    /// <summary>Automatische Sicherungen (von der App gesetzt) – für „Back up now“ und die Übersicht.</summary>
    public Services.IBackupManager? Backups { get; init; }
    public string BackupFolderHint => Backups?.Folder ?? "";
    public string BackupSummary => Backups?.Summary() ?? "";

    public bool RetentionChanged { get; private set; }

    // ---- Team-Sicherungen (Regel gilt für das ganze Team, liegt neben der Team-Datei) ----

    public sealed record TeamFileOption(string Name, string File)
    {
        public override string ToString() => Name;
    }

    private readonly Dictionary<string, Team.TeamBackupPolicy> _loadedPolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Team.TeamBackupPolicy> _editedPolicies = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<TeamFileOption> _teamFiles = [];

    /// <summary>Beschreibbare Bibliotheksdateien (vom MainViewModel gesetzt); die erste ist ausgewählt.</summary>
    public IReadOnlyList<TeamFileOption> TeamFiles
    {
        get => _teamFiles;
        init
        {
            _teamFiles = value;
            SelectedTeamFile = value.FirstOrDefault();
        }
    }

    /// <summary>Einzelne Datei (Kompatibilität).</summary>
    public string? TeamFile
    {
        get => SelectedTeamFile?.File;
        init => TeamFiles = value is null ? [] : [new TeamFileOption("Team", value)];
    }

    public bool HasSeveralTeamFiles => TeamFiles.Count > 1;

    [ObservableProperty] private TeamFileOption? _selectedTeamFile;

    partial void OnSelectedTeamFileChanging(TeamFileOption? value)
    {
        // Eingaben der bisher gewählten Datei merken (geschrieben wird erst mit OK)
        if (SelectedTeamFile is { } old)
            _editedPolicies[old.File] = new Team.TeamBackupPolicy { Enabled = TeamBackupEnabled, KeepDaily = TeamKeepDaily, KeepWeekly = TeamKeepWeekly, KeepMonthly = TeamKeepMonthly };
    }

    partial void OnSelectedTeamFileChanged(TeamFileOption? value)
    {
        OnPropertyChanged(nameof(TeamFile));
        OnPropertyChanged(nameof(TeamBackupAvailable));
        if (value is null) return;
        if (!_loadedPolicies.TryGetValue(value.File, out var loaded))
            _loadedPolicies[value.File] = loaded = Team.TeamBackup.ReadPolicy(value.File);
        var shown = _editedPolicies.TryGetValue(value.File, out var edited) ? edited : loaded;
        TeamBackupEnabled = shown.Enabled;
        TeamKeepDaily = shown.KeepDaily;
        TeamKeepWeekly = shown.KeepWeekly;
        TeamKeepMonthly = shown.KeepMonthly;
        RefreshTeamBackupSummary();
    }

    /// <summary>Bibliothek verbunden und hier beschreibbar (Jumphost/Read-only darf die Regel nicht ändern).</summary>
    public bool TeamBackupAvailable => TeamFile is not null;

    [ObservableProperty] private bool _teamBackupEnabled;
    [ObservableProperty] private int _teamKeepDaily = 14;
    [ObservableProperty] private int _teamKeepWeekly = 8;
    [ObservableProperty] private int _teamKeepMonthly = 12;
    [ObservableProperty] private string _teamBackupSummary = "Connect a team library to use team backups.";

    public void RefreshTeamBackupSummary()
    {
        if (TeamFile is null) return;
        try { TeamBackupSummary = Team.TeamBackup.Summary(TeamFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TeamBackupSummary = $"Team backup folder not reachable: {ex.Message}"; }
    }

    /// <summary>Geänderte Regeln in die Sicherungsordner neben den Dateien schreiben.</summary>
    public bool ApplyTeamBackupPolicy()
    {
        if (SelectedTeamFile is { } current)
            _editedPolicies[current.File] = new Team.TeamBackupPolicy { Enabled = TeamBackupEnabled, KeepDaily = TeamKeepDaily, KeepWeekly = TeamKeepWeekly, KeepMonthly = TeamKeepMonthly };
        var changed = false;
        foreach (var (file, edited) in _editedPolicies)
        {
            if (!_loadedPolicies.TryGetValue(file, out var loaded)) continue;
            if ((loaded.Enabled, loaded.KeepDaily, loaded.KeepWeekly, loaded.KeepMonthly)
                == (edited.Enabled, edited.KeepDaily, edited.KeepWeekly, edited.KeepMonthly)) continue;
            loaded.Enabled = edited.Enabled;
            loaded.KeepDaily = edited.KeepDaily;
            loaded.KeepWeekly = edited.KeepWeekly;
            loaded.KeepMonthly = edited.KeepMonthly;
            Team.TeamBackup.WritePolicy(file, loaded);
            changed = true;
        }
        return changed;
    }

    /// <summary>Sicherung wiederherstellen (von der App gesetzt).</summary>
    public Action<string>? RestoreBackup { get; init; }
    public string LastUpdateCheckText { get; }

    /// <summary>„Jetzt nach Updates suchen“ (von der App gesetzt).</summary>
    public Action? CheckForUpdates { get; init; }
    /// <summary>Neu eingegebener Token (leer = unverändert). Wird nie aus dem Speicher vorbefüllt.</summary>
    [ObservableProperty] private string _updateToken = "";

    public bool HasUpdateToken { get; }

    /// <summary>Angezeigte HostDeck-Version (Fußzeile des Dialogs).</summary>
    public string AppVersion { get; init; } = "";
    public string UpdateTokenHint => HasUpdateToken ? "Token stored – enter a new one to replace it" : "Only for private GitHub repos: read-only token";

    public bool ThemeChanged => Theme != _settings.Theme;

    public void Apply()
    {
        _settings.Theme = Theme;
        _settings.QuickConnectDefaultProtocol = DefaultProtocol;
        _settings.CredentialExtraction = CredentialExtraction;
        _settings.TerminalHighlighting = Highlighting;
        _settings.SftpFollowsTerminal = SftpFollows;
        _settings.TerminalRightClickMenu = RightClickMenu;
        _settings.CloseTabOnSessionEnd = CloseTabOnSessionEnd;
        _settings.ConfirmMultiLinePaste = ConfirmMultiLinePaste;
        _settings.RestoreWorkspaceOnStart = RestoreWorkspace;
        _settings.ShowReachability = ShowReachability;
        _settings.TerminalFontSize = Math.Clamp(FontSize, 8, 32);
        _settings.AutoUpdate = AutoUpdate;
        RetentionChanged = (KeepDaily, KeepWeekly, KeepMonthly) != (_settings.BackupKeepDaily, _settings.BackupKeepWeekly, _settings.BackupKeepMonthly)
                           || BackupFolder.Trim() != (_settings.BackupFolder ?? "");
        _settings.AutoBackup = AutoBackup;
        _settings.AutoLockMinutes = AutoLock.Minutes;
        _settings.LockWithWindows = LockWithWindows;
        _settings.BackupFolder = string.IsNullOrWhiteSpace(BackupFolder) ? null : BackupFolder.Trim();
        _settings.BackupKeepDaily = KeepDaily;
        _settings.BackupKeepWeekly = KeepWeekly;
        _settings.BackupKeepMonthly = KeepMonthly;
        var source = UpdateSource.Trim();
        if (source != (_settings.UpdateSource ?? ""))
        {
            _settings.UpdateSource = source.Length == 0 ? null : source;
            _settings.LastUpdateCheckUtc = null;
        }
    }
}
