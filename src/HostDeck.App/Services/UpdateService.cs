using System.IO;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;
using Velopack;
using Velopack.Sources;

namespace HostDeck.App.Services;

/// <summary>
/// Automatische Updates über Velopack. Quelle frei wählbar: Netzwerkfreigabe/Ordner (empfohlen im Unternehmen),
/// HTTP(S)-URL oder GitHub-Repository (bei privatem Repo mit Token aus der verschlüsselten Datenbank).
/// Updates werden im Hintergrund geladen und beim Beenden angewendet – nie mitten in einer Session.
/// </summary>
public sealed class UpdateService(MainViewModel vm, IDialogService dialogs, IConnectionStore store)
{
    /// <summary>Standardquelle: Releases des HostDeck-Repos (privat ⇒ Token nötig).</summary>
    public const string DefaultSource = "https://github.com/jc010220/HostDeck";
    private const string TokenDocument = SettingsViewModel.UpdateTokenDocument;
    private UpdateManager? _manager;
    private string? _managerSource;
    private UpdateInfo? _pending;

    public void Register()
    {
        // Einmalig die Standardquelle eintragen; leert der Benutzer sie später, bleibt sie leer.
        if (!vm.Settings.UpdateSourceDefaulted)
        {
            vm.Settings.UpdateSource ??= DefaultSource;
            vm.Settings.UpdateSourceDefaulted = true;
            vm.SaveSettings();
        }
        vm.SettingsApplied += () => _ = CheckAsync(interactive: false);

        vm.AppActions.Add(("Nach Updates suchen", "update aktualisieren version", () => _ = CheckAsync(interactive: true)));
        vm.AppActions.Add(("Update-Quelle festlegen …", "update quelle source share freigabe github", ConfigureSource));
        vm.AppActions.Add(($"Version anzeigen ({CurrentVersion})", "version info about über", () =>
            dialogs.ShowInfo("HostDeck", $"Version {CurrentVersion}\nUpdate-Quelle: {vm.Settings.UpdateSource ?? "(keine)"}\nInstalliert: {(Manager?.IsInstalled == true ? "ja" : "nein (portable)")}")));
    }

    public string CurrentVersion => Manager?.CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "?";

    private UpdateManager? Manager
    {
        get
        {
            var source = vm.Settings.UpdateSource;
            if (string.IsNullOrWhiteSpace(source)) return null;
            // Quelle/Token können sich über die Einstellungen ändern ⇒ dann neu aufbauen
            var key = source + "|" + (IsGithub(source) ? store.ReadDocument(TokenDocument) : null);
            if (_manager is not null && _managerSource == key) return _manager;
            _managerSource = key;
            try { _manager = new UpdateManager(CreateSource(source)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { _manager = null; }
            return _manager;
        }
    }

    private static bool IsGithub(string? source) =>
        source?.Trim().StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) == true;

    private IUpdateSource CreateSource(string source)
    {
        source = source.Trim();
        if (source.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            return new GithubSource(source.TrimEnd('/'), store.ReadDocument(TokenDocument), prerelease: false);
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return new SimpleWebSource(source);
        return new SimpleFileSource(new DirectoryInfo(source)); // lokaler Ordner oder \\server\freigabe
    }

    /// <summary>Beim Start (verzögert, höchstens einmal täglich) still prüfen und im Hintergrund laden.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (Manager is not { IsInstalled: true }) return;
        if (vm.Settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(20)) return;
        await Task.Delay(TimeSpan.FromSeconds(15));
        await CheckAsync(interactive: false);
    }

    public async Task CheckAsync(bool interactive)
    {
        var manager = Manager;
        if (manager is null)
        {
            if (interactive) dialogs.ShowInfo("Updates", "Es ist keine Update-Quelle festgelegt (Strg+K → „Update-Quelle festlegen“).");
            return;
        }
        if (!manager.IsInstalled)
        {
            if (interactive) dialogs.ShowInfo("Updates", "Diese HostDeck-Kopie wurde nicht über den Installer installiert (portable) – automatische Updates sind nur für installierte Versionen verfügbar.");
            return;
        }
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            vm.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            vm.SaveSettings();
            if (info is null)
            {
                if (interactive) dialogs.ShowInfo("Updates", $"HostDeck {CurrentVersion} ist aktuell.");
                return;
            }
            vm.StatusText = $"Lade Update {info.TargetFullRelease.Version} …";
            await manager.DownloadUpdatesAsync(info, p => vm.StatusText = $"Lade Update {info.TargetFullRelease.Version} … {p} %");
            _pending = info;
            vm.StatusText = $"Update {info.TargetFullRelease.Version} bereit – wird beim Beenden installiert (Strg+K → „Jetzt aktualisieren“).";
            if (!vm.AppActions.Any(a => a.Title.StartsWith("Jetzt aktualisieren", StringComparison.Ordinal)))
                vm.AppActions.Insert(0, ($"Jetzt aktualisieren auf {info.TargetFullRelease.Version} (Neustart)", "update neustart restart", ApplyNow));
        }
        catch (Exception ex) // Hintergrundprüfung darf nie die App stören (Velopack wirft teils eigene Typen)
        {
            var missingToken = IsGithub(vm.Settings.UpdateSource) && string.IsNullOrEmpty(store.ReadDocument(TokenDocument));
            var hint = missingToken ? " Für das private GitHub-Repository fehlt der Zugriffstoken (Einstellungen → Updates)." : "";
            if (interactive) dialogs.ShowError("Updates", $"Update-Prüfung fehlgeschlagen: {ex.Message}{hint}");
            else vm.StatusText = "Update-Prüfung fehlgeschlagen." + hint;
        }
    }

    private void ApplyNow()
    {
        if (_pending is null || Manager is null) return;
        if (!dialogs.Confirm("Jetzt aktualisieren", "HostDeck wird beendet, aktualisiert und neu gestartet. Offene Verbindungen werden getrennt (der Arbeitsbereich wird wiederhergestellt).\n\nFortfahren?")) return;
        vm.Shutdown();
        Manager.ApplyUpdatesAndRestart(_pending);
    }

    /// <summary>Beim Beenden: geladenes Update anwenden, sobald der Prozess beendet ist.</summary>
    public void ApplyOnExit()
    {
        if (_pending is null || Manager is null) return;
        try { Manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }

    private void ConfigureSource()
    {
        var value = dialogs.Prompt("Update-Quelle",
            "Netzwerkfreigabe (z. B. \\\\server\\software\\HostDeck), HTTP(S)-URL oder GitHub-Repo-URL. Leer = keine Updates.",
            vm.Settings.UpdateSource ?? "");
        if (value is null) return;
        vm.Settings.UpdateSource = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        vm.Settings.LastUpdateCheckUtc = null;
        vm.SaveSettings();
        _manager = null;

        if (vm.Settings.UpdateSource?.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) == true &&
            dialogs.Prompt("GitHub-Token", "Nur für private Repositories: Personal Access Token (nur Lesezugriff auf Releases). Leer lassen für öffentliche Repos.", "", secret: true) is { Length: > 0 } token)
            store.WriteDocument(TokenDocument, token);

        _ = CheckAsync(interactive: true);
    }
}
