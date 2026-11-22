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
        _restoreWorkspace = settings.RestoreWorkspaceOnStart;
        _showReachability = settings.ShowReachability;
        _fontSize = FontSizes.Contains(settings.TerminalFontSize) ? settings.TerminalFontSize : 14;
        _updateSource = settings.UpdateSource ?? "";
        _teamLibraryPath = settings.TeamLibraryPath ?? "";
        _teamReadOnly = settings.TeamReadOnly;
        _autoUpdate = settings.AutoUpdate;
        _autoBackup = settings.AutoBackup;
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
    [ObservableProperty] private bool _restoreWorkspace;
    [ObservableProperty] private bool _showReachability;
    [ObservableProperty] private int _fontSize;
    [ObservableProperty] private string _updateSource;
    /// <summary>Wird nicht in <see cref="Apply"/> übernommen – das Verbinden erledigt das MainViewModel.</summary>
    [ObservableProperty] private string _teamLibraryPath;
    [ObservableProperty] private bool _teamReadOnly;
    [ObservableProperty] private bool _autoUpdate;

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

    private Team.TeamBackupPolicy? _teamPolicy;
    private string? _teamFile;

    /// <summary>Aufgelöste Team-Datei (vom MainViewModel gesetzt); lädt dabei die Team-Sicherungsregel.</summary>
    public string? TeamFile
    {
        get => _teamFile;
        init
        {
            _teamFile = value;
            if (value is null) return;
            _teamPolicy = Team.TeamBackup.ReadPolicy(value);
            TeamBackupEnabled = _teamPolicy.Enabled;
            TeamKeepDaily = _teamPolicy.KeepDaily;
            TeamKeepWeekly = _teamPolicy.KeepWeekly;
            TeamKeepMonthly = _teamPolicy.KeepMonthly;
            RefreshTeamBackupSummary();
        }
    }

    /// <summary>Team verbunden und schreibbar (Jumphost/Read-only darf die Team-Regel nicht ändern).</summary>
    public bool TeamBackupAvailable => TeamFile is not null && !_settings.TeamReadOnly;

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

    /// <summary>Geänderte Team-Regel in den Sicherungsordner neben der Team-Datei schreiben.</summary>
    public bool ApplyTeamBackupPolicy()
    {
        if (!TeamBackupAvailable || _teamPolicy is null) return false;
        if ((_teamPolicy.Enabled, _teamPolicy.KeepDaily, _teamPolicy.KeepWeekly, _teamPolicy.KeepMonthly)
            == (TeamBackupEnabled, TeamKeepDaily, TeamKeepWeekly, TeamKeepMonthly)) return false;
        _teamPolicy.Enabled = TeamBackupEnabled;
        _teamPolicy.KeepDaily = TeamKeepDaily;
        _teamPolicy.KeepWeekly = TeamKeepWeekly;
        _teamPolicy.KeepMonthly = TeamKeepMonthly;
        Team.TeamBackup.WritePolicy(TeamFile!, _teamPolicy);
        return true;
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
        _settings.RestoreWorkspaceOnStart = RestoreWorkspace;
        _settings.ShowReachability = ShowReachability;
        _settings.TerminalFontSize = Math.Clamp(FontSize, 8, 32);
        _settings.AutoUpdate = AutoUpdate;
        RetentionChanged = (KeepDaily, KeepWeekly, KeepMonthly) != (_settings.BackupKeepDaily, _settings.BackupKeepWeekly, _settings.BackupKeepMonthly)
                           || BackupFolder.Trim() != (_settings.BackupFolder ?? "");
        _settings.AutoBackup = AutoBackup;
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
