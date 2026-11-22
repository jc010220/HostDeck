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
    /// <summary>Sicherung unter einem vom Benutzer gewählten Dateinamen (Schlüsseldatei daneben als *.key).</summary>
    string BackupTo(string targetFile);
    /// <summary>Anzahl der aktuell gespeicherten Verbindungen.</summary>
    int CountConnections();
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
    /// <summary>Fensterposition/-größe beim letzten Beenden (null = Standard, zentriert).</summary>
    public WindowPlacement? MainWindow { get; set; }
    /// <summary>Seitenleiste eingeklappt (mehr Platz für Sessions).</summary>
    public bool SidebarCollapsed { get; set; }
    public CredentialExtractionMode CredentialExtraction { get; set; } = CredentialExtractionMode.SharedOnly;
    public bool TerminalHighlighting { get; set; } = true;
    public bool SftpFollowsTerminal { get; set; } = true;
    /// <summary>Rechtsklick ins Terminal fügt erst nach Rückfrage ein (gegen versehentliches Einfügen).</summary>
    public bool ConfirmRightClickPaste { get; set; } = true;
    /// <summary>Online-Status im Baum anzeigen (TCP-Test des Dienst-Ports sichtbarer Verbindungen).</summary>
    public bool ShowReachability { get; set; } = true;
    public int TerminalFontSize { get; set; } = 14;
    /// <summary>Update-Quelle: Ordner/UNC-Pfad, HTTP(S)-URL oder GitHub-Repo-URL. Leer = keine Updates.</summary>
    public string? UpdateSource { get; set; }
    /// <summary>Standard-Update-Quelle wurde einmalig eingetragen (danach gilt die Wahl des Benutzers, auch „leer“).</summary>
    public bool UpdateSourceDefaulted { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>Automatisch sperren nach so vielen Minuten ohne Eingabe (0 = aus, Standard).</summary>
    public int AutoLockMinutes { get; set; }
    /// <summary>Sperren, sobald Windows gesperrt wird (Win+L).</summary>
    public bool LockWithWindows { get; set; }
    /// <summary>Tägliche automatische Sicherung (verschlüsselt).</summary>
    public bool AutoBackup { get; set; } = true;
    /// <summary>Zielordner; leer = %APPDATA%\HostDeck\backups. Z. B. OneDrive- oder Netzwerkordner.</summary>
    public string? BackupFolder { get; set; }
    /// <summary>Aufbewahrung (Großvater-Vater-Sohn): neueste Sicherung je Tag / Woche / Monat.</summary>
    public int BackupKeepDaily { get; set; } = 7;
    public int BackupKeepWeekly { get; set; } = 4;
    public int BackupKeepMonthly { get; set; } = 12;
    /// <summary>Täglich im Hintergrund prüfen, laden und beim Beenden installieren.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Gemeinsame Team-Datei (confCons.xml auf dem Netzlaufwerk oder Ordner mit lokaler Kopie).</summary>
    public string? TeamLibraryPath { get; set; }
    /// <summary>Nur lesen (z. B. Jumphost): Änderungen anderer übernehmen, nie zurückschreiben.</summary>
    public bool TeamReadOnly { get; set; }
    /// <summary>Weitere Bibliotheken (weitere Teams oder eine eigene Datei, z. B. auf OneDrive).</summary>
    public List<LibraryConfig> Libraries { get; set; } = [];
    /// <summary>Offene Sessions beim letzten Beenden (wird beim Start wiederhergestellt).</summary>
    public WorkspaceSnapshot? LastWorkspace { get; set; }
    public bool RestoreWorkspaceOnStart { get; set; } = true;
    /// <summary>Benannte Arbeitsbereiche („Kunde ACME – 4 Server“).</summary>
    public Dictionary<string, WorkspaceSnapshot> SavedWorkspaces { get; set; } = [];
    /// <summary>Zuletzt geöffnete Verbindungen (neueste zuerst) für Schnellsuche und Start.</summary>
    public List<Guid> RecentConnections { get; set; } = [];
}

/// <summary>Eine weitere Bibliothek: eigene confCons.xml, eigener Ordner im Baum, eigenes Passwort.</summary>
public sealed class LibraryConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "Library";
    public string Path { get; set; } = "";
    public bool ReadOnly { get; set; }
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

/// <summary>Position und Größe (geräteunabhängige Pixel) sowie ob das Fenster maximiert war.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Layout + Verbindungs-IDs je Bereich (Quick-Connect-Tabs werden nicht gespeichert).</summary>
public sealed class WorkspaceSnapshot
{
    public HostDeck.Presentation.ViewModels.WorkspaceLayout Layout { get; set; }
    public List<List<Guid>> Panes { get; set; } = [];
    /// <summary>Abgelöste Fenster (z. B. auf dem zweiten Monitor) mit Position und eigenen Tabs.</summary>
    public List<DetachedWindowSnapshot> Windows { get; set; } = [];
    public int ConnectionCount => Panes.Sum(p => p.Count) + Windows.Sum(w => w.Workspace.ConnectionCount);
}

public sealed class DetachedWindowSnapshot
{
    public WindowPlacement? Placement { get; set; }
    public WorkspaceSnapshot Workspace { get; set; } = new();
}

/// <summary>Brücke zum Fehlerprotokoll der App (Presentation kennt die Dateiablage nicht).</summary>
public static class AppLog
{
    public static Action<string, Exception>? Sink { get; set; }
    public static Action<string>? InfoSink { get; set; }
    public static void Error(string context, Exception ex) => Sink?.Invoke(context, ex);
    public static void Info(string message) => InfoSink?.Invoke(message);
}
