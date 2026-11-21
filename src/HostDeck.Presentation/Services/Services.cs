using System.Text.Json;
using System.Text.Json.Serialization;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Protocols.Ssh;

namespace HostDeck.Presentation.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>Dialoge – in der WPF-App implementiert, in Tests gemockt.</summary>
public interface IDialogService
{
    string? OpenFile(string title, string filter);
    string? SaveFile(string title, string filter, string defaultFileName);

    /// <summary>Master-Passwort abfragen; <c>null</c> = abgebrochen.</summary>
    string? AskMasterPassword(string? sourceName, bool previousAttemptFailed, int attempt, int maxAttempts);

    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
    bool Confirm(string title, string message);

    /// <summary>Altes + neues Master-Passwort abfragen; <c>null</c> = abgebrochen.</summary>
    (string Current, string New)? AskChangeMasterPassword();

    /// <summary>Unbekannter oder geänderter SSH-Host-Key.</summary>
    HostKeyDecision AskHostKey(HostKeyInfo info);

    /// <summary>Freie Eingabe (z. B. Ordnername, OTP-Code); <c>null</c> = abgebrochen.</summary>
    string? Prompt(string title, string message, string defaultValue = "", bool secret = false);

    /// <summary>Lokalen Zielordner wählen.</summary>
    string? PickFolder(string title);

    /// <summary>Eigenschaften-Dialog; true = übernommen (Apply wurde bereits erfolgreich ausgeführt).</summary>
    bool EditConnection(ViewModels.ConnectionEditorViewModel editor);

    /// <summary>Einstellungen bearbeiten; true = übernommen.</summary>
    bool EditSettings(ViewModels.SettingsViewModel settings);

    /// <summary>Credential-Vault verwalten (Änderungen wirken direkt auf den Vault).</summary>
    void ManageVault(ViewModels.CredentialVaultViewModel vault);
}

/// <summary>Verschlüsselter Speicher des Verbindungsbestands (in der App: EncryptedConnectionStore).</summary>
public interface IConnectionStore : IKnownHosts
{
    string Location { get; }
    ConnectionTree Load();
    void Save(ConnectionTree tree);

    /// <summary>Weitere verschlüsselte Dokumente (Snippets …); <c>null</c> = nicht vorhanden.</summary>
    string? ReadDocument(string name);
    /// <summary>Verschlüsselte Sicherung in einen Ordner; liefert den Dateipfad.</summary>
    string Backup(string targetDirectory);
    void WriteDocument(string name, string json);

    bool HasDeviceUnlock { get; }
    bool DeviceUnlockSupported { get; }
    void SetDeviceUnlock(bool enabled);

    /// <exception cref="UnauthorizedAccessException">Aktuelles Passwort falsch.</exception>
    void ChangeMasterPassword(string currentPassword, string newPassword);
}

public interface IThemeService
{
    void Apply(AppTheme theme);
}

/// <summary>Nicht geheime UI-Einstellungen (Theme, letzte Datei, Quick-Connect-Verlauf …).</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    /// <summary>Ordner der zuletzt importierten Datei (Vorschlag im Dateidialog).</summary>
    public string? LastImportPath { get; set; }
    /// <summary>Angebot "mRemoteNG-Verbindungen übernehmen?" wurde schon gezeigt.</summary>
    public bool FirstRunImportOffered { get; set; }
    public ProtocolKind QuickConnectDefaultProtocol { get; set; } = ProtocolKind.Ssh2;
    public List<string> QuickConnectHistory { get; set; } = [];
    public double SidebarWidth { get; set; } = 300;
    public CredentialExtractionMode CredentialExtraction { get; set; } = CredentialExtractionMode.SharedOnly;
    public bool TerminalHighlighting { get; set; } = true;
    public bool SftpFollowsTerminal { get; set; } = true;
    /// <summary>Online-Status im Baum anzeigen (TCP-Test des Dienst-Ports sichtbarer Verbindungen).</summary>
    public bool ShowReachability { get; set; } = true;
    public int TerminalFontSize { get; set; } = 14;
    /// <summary>Update-Quelle: Ordner/UNC-Pfad, HTTP(S)-URL oder GitHub-Repo-URL. Leer = keine Updates.</summary>
    public string? UpdateSource { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>Offene Sessions beim letzten Beenden (wird beim Start wiederhergestellt).</summary>
    public WorkspaceSnapshot? LastWorkspace { get; set; }
    public bool RestoreWorkspaceOnStart { get; set; } = true;
    /// <summary>Benannte Arbeitsbereiche („Kunde ACME – 4 Server“).</summary>
    public Dictionary<string, WorkspaceSnapshot> SavedWorkspaces { get; set; } = [];
    /// <summary>Zuletzt geöffnete Verbindungen (neueste zuerst) für Schnellsuche und Start.</summary>
    public List<Guid> RecentConnections { get; set; } = [];
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>JSON-Datei, standardmäßig %APPDATA%\HostDeck\settings.json. Enthält bewusst keine Geheimnisse.</summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HostDeck", "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings(); // kaputte Einstellungen dürfen den Start nicht verhindern
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Layout + Verbindungs-IDs je Bereich (Quick-Connect-Tabs werden nicht gespeichert).</summary>
public sealed class WorkspaceSnapshot
{
    public HostDeck.Presentation.ViewModels.WorkspaceLayout Layout { get; set; }
    public List<List<Guid>> Panes { get; set; } = [];
    public int ConnectionCount => Panes.Sum(p => p.Count);
}

/// <summary>Brücke zum Fehlerprotokoll der App (Presentation kennt die Dateiablage nicht).</summary>
public static class AppLog
{
    public static Action<string, Exception>? Sink { get; set; }
    public static void Error(string context, Exception ex) => Sink?.Invoke(context, ex);
}
