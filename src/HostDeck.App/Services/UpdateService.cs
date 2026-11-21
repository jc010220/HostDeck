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
    private string? _sourceError;
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

        vm.AppActions.Add(("Check for updates", "update aktualisieren version", () => _ = CheckAsync(interactive: true)));
        vm.AppActions.Add(("Set update source …", "update quelle source share freigabe github", ConfigureSource));
        vm.AppActions.Add(($"Show version ({CurrentVersion})", "version info about über", () =>
            dialogs.ShowInfo("HostDeck", $"Version {CurrentVersion}\nUpdate source: {vm.Settings.UpdateSource ?? "(none)"}\nInstalled: {(Manager?.IsInstalled == true ? "yes" : "no (portable)")}")));
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
            try { _manager = new UpdateManager(CreateSource(source)); _sourceError = null; }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { _manager = null; _sourceError = ex.Message; }
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
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
            // Unverschlüsseltes HTTP: jeder im Netzwerkpfad könnte ein manipuliertes Update unterschieben
            throw new NotSupportedException("HTTP update sources are not allowed (anyone on the network path could inject a manipulated update). Use HTTPS, GitHub or a network share.");
        if (uri is { Scheme: "https" })
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
            if (interactive) dialogs.ShowInfo("Updates", _sourceError is { } error ? $"The update source cannot be used: {error}" : "No update source is set (Ctrl+K → “Set update source”).");
            return;
        }
        if (!manager.IsInstalled)
        {
            if (interactive) dialogs.ShowInfo("Updates", "This copy of HostDeck was not installed via the installer (portable) – automatic updates are only available for installed versions.");
            return;
        }
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            vm.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            vm.SaveSettings();
            if (info is null)
            {
                if (interactive) dialogs.ShowInfo("Updates", $"HostDeck {CurrentVersion} is up to date.");
                return;
            }
            vm.StatusText = $"Downloading update {info.TargetFullRelease.Version} …";
            await manager.DownloadUpdatesAsync(info, p => vm.StatusText = $"Downloading update {info.TargetFullRelease.Version} … {p} %");
            _pending = info;
            vm.StatusText = $"Update {info.TargetFullRelease.Version} ready – installed on exit (Ctrl+K → “Update now”).";
            if (!vm.AppActions.Any(a => a.Title.StartsWith("Update now", StringComparison.Ordinal)))
                vm.AppActions.Insert(0, ($"Update now to {info.TargetFullRelease.Version} (restart)", "update neustart restart", ApplyNow));
        }
        catch (Exception ex) // Hintergrundprüfung darf nie die App stören (Velopack wirft teils eigene Typen)
        {
            var missingToken = IsGithub(vm.Settings.UpdateSource) && string.IsNullOrEmpty(store.ReadDocument(TokenDocument));
            var hint = missingToken ? " The access token for the private GitHub repository is missing (Settings → Updates)." : "";
            if (interactive) dialogs.ShowError("Updates", $"Update check failed: {ex.Message}{hint}");
            else vm.StatusText = "Update check failed." + hint;
        }
    }

    private void ApplyNow()
    {
        if (_pending is null || Manager is null) return;
        if (!dialogs.Confirm("Update now", "HostDeck will close, update and restart. Open connections will be disconnected (the workspace is restored).\n\nContinue?")) return;
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
        var value = dialogs.Prompt("Update source",
            "Network share (e.g. \\\\server\\software\\HostDeck), HTTP(S) URL or GitHub repo URL. Empty = no updates.",
            vm.Settings.UpdateSource ?? "");
        if (value is null) return;
        vm.Settings.UpdateSource = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        vm.Settings.LastUpdateCheckUtc = null;
        vm.SaveSettings();
        _manager = null;

        if (vm.Settings.UpdateSource?.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) == true &&
            dialogs.Prompt("GitHub token", "Only for private repositories: personal access token (read-only access to releases). Leave empty for public repos.", "", secret: true) is { Length: > 0 } token)
            store.WriteDocument(TokenDocument, token);

        _ = CheckAsync(interactive: true);
    }
}
