using System.Text.Json;
using System.Text.Json.Serialization;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;

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
}

public interface IThemeService
{
    void Apply(AppTheme theme);
}

/// <summary>Nicht geheime UI-Einstellungen (Theme, letzte Datei, Quick-Connect-Verlauf …).</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string? LastImportPath { get; set; }
    public ProtocolKind QuickConnectDefaultProtocol { get; set; } = ProtocolKind.Ssh2;
    public List<string> QuickConnectHistory { get; set; } = [];
    public double SidebarWidth { get; set; } = 300;
    public CredentialExtractionMode CredentialExtraction { get; set; } = CredentialExtractionMode.SharedOnly;
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
