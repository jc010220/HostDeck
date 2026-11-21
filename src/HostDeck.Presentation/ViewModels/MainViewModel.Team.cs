using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Core.Model;
using HostDeck.Core.Sync;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.Team;

namespace HostDeck.Presentation.ViewModels;

/// <summary>
/// Team-Bibliothek: gemeinsame confCons.xml (Netzlaufwerk bzw. lokale Kopie auf dem Jumphost) als eigener Ordner
/// im Baum. Änderungen anderer werden automatisch übernommen, eigene mit Strg+S zusammengeführt und gespeichert
/// (wie KeePass „Synchronisieren“). Persönliche Zugangsdaten-Zuordnungen bleiben lokal.
/// </summary>
public sealed partial class MainViewModel
{
    internal const string TeamPasswordDocument = "team-password";
    internal const string TeamBaseDocument = "team-base";
    private const string TeamHashDocument = "team-hash";

    private bool _teamBusy;
    private int _treeVersion;
    private Timer? _teamPoll;
    private (DateTime, long)? _teamProbe;
    private SynchronizationContext? _teamUi;

    /// <summary>Statuszeile der Team-Bibliothek (leer = nicht eingerichtet).</summary>
    [ObservableProperty] private string _teamStatus = "";
    [ObservableProperty] private int _teamPendingChanges;

    public bool HasTeamLibrary => !string.IsNullOrWhiteSpace(Settings.TeamLibraryPath);

    private const int TeamBackupSlots = 10;

    /// <summary>
    /// Vorheriger Stand der Team-Datei vor jedem Speichern – verschlüsselt in der HostDeck-Datenbank
    /// (eine Datei-Kopie wäre bei mRemoteNG-Standardschlüssel praktisch Klartext).
    /// </summary>
    private void BackupTeamFile(byte[] content)
    {
        var index = int.TryParse(_store.ReadDocument("team-backup-index"), out var i) ? (i + 1) % TeamBackupSlots : 0;
        _store.WriteDocument($"team-backup-{index}", JsonSerializer.Serialize(new TeamBackup(DateTime.UtcNow, System.Text.Encoding.UTF8.GetString(content))));
        _store.WriteDocument("team-backup-index", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Gesicherte Stände (neueste zuerst) – z. B. zum Wiederherstellen nach einem Fehler.</summary>
    public IReadOnlyList<(DateTime SavedUtc, string Content)> TeamBackups()
    {
        var list = new List<(DateTime, string)>();
        for (var i = 0; i < TeamBackupSlots; i++)
        {
            if (_store.ReadDocument($"team-backup-{i}") is not { Length: > 0 } json) continue;
            try { if (JsonSerializer.Deserialize<TeamBackup>(json) is { } b) list.Add((b.SavedUtc, b.Content)); }
            catch (JsonException) { }
        }
        return list.OrderByDescending(b => b.Item1).ToList();
    }

    private sealed record TeamBackup(DateTime SavedUtc, string Content);

    /// <summary>Lokaler Ordner, der den Inhalt der Team-Datei hält.</summary>
    public FolderNode? TeamContainer =>
        SessionTree.Tree.Root.Children.OfType<FolderNode>().FirstOrDefault(f => f.Extensions.ContainsKey(TreeMerge.TeamContainerKey));

    // ---- Einrichten / Trennen ---------------------------------------------------------------------

    [RelayCommand]
    private async Task ConnectTeamLibrary()
    {
        var path = _dialogs.OpenFile("Team-Bibliothek (gemeinsame confCons.xml) wählen", "mRemoteNG (confCons.xml)|*.xml|Alle Dateien|*.*");
        if (path is null) return;
        var readOnly = _dialogs.Confirm("Team-Bibliothek",
            "Soll diese Kopie nur gelesen werden?\n\n" +
            "„Ja“ für Jumphosts mit lokaler Kopie: Änderungen anderer werden übernommen, Strg+S schreibt nicht.\n" +
            "„Nein“ (Standard am Arbeitsplatz): Du kannst Team-Einträge ändern und mit Strg+S für alle speichern.");
        await ConnectTeamLibraryAsync(path, readOnly);
    }

    public async Task<bool> ConnectTeamLibraryAsync(string path, bool readOnly)
    {
        if (!_loadSucceeded || _teamBusy) return false;
        _teamBusy = true;
        try
        {
            StatusText = "Lese Team-Bibliothek …";
            var resolved = TeamLibraryFile.Resolve(path) ?? path;
            TeamSnapshot snapshot;
            try
            {
                snapshot = await Io(() => TeamLibraryFile.Read(resolved, null, AskTeamPassword()));
            }
            catch (Exception ex) when (ex is ConfConsAuthenticationException or ConfConsFormatException or IOException or UnauthorizedAccessException)
            {
                if (ex is not ConfConsAuthenticationException { Cancelled: true })
                    _dialogs.ShowError("Team-Bibliothek", $"Die Datei konnte nicht gelesen werden:\n{ex.Message}");
                StatusText = "Team-Bibliothek nicht eingerichtet";
                return false;
            }

            var tree = SessionTree.Tree;
            var personal = new Dictionary<Guid, Guid?>();
            var existing = TeamContainer;
            if (existing is not null)
            {
                foreach (var node in existing.Descendants()) personal[node.Id] = node.Settings.CredentialId;
                tree.Root.Remove(existing);
            }

            // Früher manuell importierte Kopie derselben Datei (gleiche IDs) ersetzen, persönliche Zuordnungen übernehmen
            var teamIds = Flatten(snapshot.Children).Select(n => n.Id).ToHashSet();
            var duplicates = tree.Root.Descendants().Where(n => teamIds.Contains(n.Id)).ToList();
            if (duplicates.Count > 0)
            {
                if (!_dialogs.Confirm("Bisherige Kopie ersetzen?",
                        $"{duplicates.Count} Einträge aus dieser Datei wurden früher schon importiert.\n\n" +
                        "Diese alte Kopie wird durch die Team-Bibliothek ersetzt (persönliche Zugangsdaten-Zuordnungen bleiben erhalten). " +
                        "Vorher wird automatisch eine Sicherung angelegt.\n\nFortfahren?"))
                {
                    if (existing is not null) tree.Root.Insert(0, existing);
                    StatusText = "Team-Bibliothek nicht eingerichtet";
                    return false;
                }
                try { AppLog.Info($"Sicherung vor Team-Einrichtung: {_store.Backup(BackupDirectory)}"); }
                catch (Exception ex) { AppLog.Error("Sicherung vor Team-Einrichtung", ex); }
                foreach (var node in duplicates) personal.TryAdd(node.Id, node.Settings.CredentialId);
                foreach (var node in duplicates.Where(d => !duplicates.Any(o => o != d && d.Ancestors().Contains(o))))
                    node.Parent?.Remove(node);
            }

            var container = new FolderNode("Team") { IsExpanded = true };
            container.Extensions[TreeMerge.TeamContainerKey] = "1";
            tree.Root.Insert(0, container);
            foreach (var child in snapshot.Children) container.Add(child);
            foreach (var node in container.Descendants())
                if (personal.TryGetValue(node.Id, out var credential)) node.Settings.CredentialId = credential;

            Settings.TeamLibraryPath = path;
            Settings.TeamReadOnly = readOnly;
            SaveSettings();
            if (snapshot.Password is { } pw) _store.WriteDocument(TeamPasswordDocument, pw);
            SaveTeamBase(TreeMerge.Snapshot(container.Children), snapshot.Hash);
            _teamProbe = TeamLibraryFile.Probe(resolved);

            SessionTree.Load(tree);
            Persist();
            UpdateTeamStatus($"verbunden ({Flatten(snapshot.Children).OfType<ConnectionNode>().Count()} Verbindungen)");
            StatusText = "Team-Bibliothek eingerichtet";
            WarnIfTeamFileUnprotected(snapshot);
            StartTeamPolling();
            return true;
        }
        finally
        {
            _teamBusy = false;
            OnPropertyChanged(nameof(HasTeamLibrary));
        }
    }

    [RelayCommand]
    private void DisconnectTeamLibrary()
    {
        if (!HasTeamLibrary) return;
        var keep = _dialogs.Confirm("Team-Bibliothek trennen",
            "Die Verbindung zur gemeinsamen Datei wird getrennt.\n\n„Ja“: Einträge als persönliche Kopie behalten\n„Nein“: Team-Ordner aus dem Baum entfernen");
        if (TeamContainer is { } container)
        {
            if (keep) container.Extensions.Remove(TreeMerge.TeamContainerKey);
            else SessionTree.Tree.Root.Remove(container);
        }
        Settings.TeamLibraryPath = null;
        SaveSettings();
        _store.WriteDocument(TeamBaseDocument, "{}");
        _store.WriteDocument(TeamPasswordDocument, "");
        _teamPoll?.Dispose();
        _teamPoll = null;
        SessionTree.Load(SessionTree.Tree);
        Persist();
        TeamStatus = "";
        TeamPendingChanges = 0;
        OnPropertyChanged(nameof(HasTeamLibrary));
    }

    private void ApplyTeamSettings(string path, bool readOnly)
    {
        var current = Settings.TeamLibraryPath ?? "";
        if (path.Length == 0 && current.Length > 0) { DisconnectTeamLibrary(); return; }
        if (path.Length > 0 && !string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
        {
            _ = ConnectTeamLibraryAsync(path, readOnly);
            return;
        }
        if (readOnly != Settings.TeamReadOnly)
        {
            Settings.TeamReadOnly = readOnly;
            SaveSettings();
            RefreshTeamPending();
        }
    }

    // ---- Synchronisieren --------------------------------------------------------------------------

    /// <summary>Strg+S: Änderungen anderer übernehmen und eigene in die Team-Datei schreiben.</summary>
    [RelayCommand]
    private Task SyncTeam() => SyncTeamAsync(write: true, interactive: true);

    /// <summary>
    /// Kern der Synchronisation. <paramref name="write"/> = eigene Änderungen speichern (Strg+S), sonst nur
    /// Änderungen aus der Datei übernehmen (automatisch). Konflikte werden nur beim Speichern abgefragt.
    /// </summary>
    public async Task<bool> SyncTeamAsync(bool write, bool interactive)
    {
        if (!HasTeamLibrary || !_loadSucceeded || _teamBusy) return false;
        if (TeamContainer is null)
        {
            // Team-Ordner wurde gelöscht ⇒ als „trennen“ verstehen, NIE als „alles in der Datei löschen“
            Settings.TeamLibraryPath = null;
            SaveSettings();
            TeamStatus = "";
            OnPropertyChanged(nameof(HasTeamLibrary));
            return false;
        }
        if (Settings.TeamReadOnly && write)
        {
            if (interactive) _dialogs.ShowInfo("Team-Bibliothek", "Diese Kopie ist schreibgeschützt (z. B. Jumphost). Änderungen bitte am Arbeitsplatz vornehmen.");
            write = false;
        }

        _teamBusy = true;
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var path = TeamLibraryFile.Resolve(Settings.TeamLibraryPath);
                var container = TeamContainer;
                if (path is null || container is null) return false;
                var version = _treeVersion;
                var mine = container.Children.Select(c => TreeMerge.CopyWithIds(c, includePersonal: true)).ToList();
                var (baseline, _) = LoadTeamBase();
                if (Settings.TeamReadOnly) baseline = TreeMerge.Snapshot(mine); // lokale Abweichungen nie behalten

                TeamSnapshot remote;
                try
                {
                    var knownPassword = _store.ReadDocument(TeamPasswordDocument);
                    remote = await Io(() => TeamLibraryFile.Read(path, string.IsNullOrEmpty(knownPassword) ? null : knownPassword,
                        interactive ? AskTeamPassword() : null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    UpdateTeamStatus("Datei nicht erreichbar – Offline-Stand");
                    if (interactive) _dialogs.ShowError("Team-Bibliothek", $"Die Team-Datei ist nicht erreichbar:\n{ex.Message}\n\nDeine Änderungen bleiben lokal erhalten.");
                    return false;
                }
                catch (ConfConsAuthenticationException ex)
                {
                    UpdateTeamStatus(ex.Cancelled ? "Passwort nicht eingegeben" : "Team-Passwort benötigt – Strg+S");
                    if (interactive && !ex.Cancelled) _dialogs.ShowError("Team-Bibliothek", ex.Message);
                    return false;
                }
                catch (ConfConsFormatException ex)
                {
                    UpdateTeamStatus("Datei beschädigt?");
                    AppLog.Error("Team-Bibliothek lesen", ex);
                    if (interactive) _dialogs.ShowError("Team-Bibliothek", $"Die Team-Datei konnte nicht gelesen werden:\n{ex.Message}");
                    return false;
                }
                if (remote.PasswordWasEntered && remote.Password is { } entered) _store.WriteDocument(TeamPasswordDocument, entered);

                var merge = TreeMerge.Merge(baseline, mine, remote.Children);
                if (write && merge.Conflicts.Count > 0 && interactive)
                {
                    var list = string.Join("\n", merge.Conflicts.Take(15).Select(c => "• " + c.Path))
                               + (merge.Conflicts.Count > 15 ? $"\n… und {merge.Conflicts.Count - 15} weitere" : "");
                    var keepMine = _dialogs.Confirm("Gleichzeitig geändert",
                        $"Diese Einträge wurden von dir und von jemand anderem unterschiedlich geändert:\n\n{list}\n\n" +
                        "„Ja“: deine Version speichern\n„Nein“: Version aus der Team-Datei übernehmen");
                    if (!keepMine) merge = TreeMerge.Merge(baseline, mine, remote.Children, MergeConflictResolution.TakeTheirs);
                }

                var children = merge.Children;
                var newBase = merge.NewBase;
                var newHash = remote.Hash;
                var wrote = false;
                if (write && merge.LocalChanges > 0)
                {
                    try
                    {
                        var format = remote.Info;
                        var password = remote.Password;
                        var merged = children;
                        (newHash, var written) = await Io(() => TeamLibraryFile.Write(path, merged, format, password, remote.Hash, BackupTeamFile));
                        children = TreeMerge.WithPersonalFrom(written, merged);
                        newBase = TreeMerge.Snapshot(children);
                        wrote = true;
                    }
                    catch (TeamFileChangedException)
                    {
                        continue; // jemand war schneller ⇒ neu einlesen und erneut zusammenführen
                    }
                    catch (TeamFileLockedException ex)
                    {
                        UpdateTeamStatus("gerade gesperrt – bitte erneut Strg+S");
                        if (interactive) _dialogs.ShowInfo("Team-Bibliothek", ex.Message);
                        return false;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Error("Team-Bibliothek schreiben", ex);
                        UpdateTeamStatus("Speichern fehlgeschlagen");
                        if (interactive) _dialogs.ShowError("Team-Bibliothek", $"Speichern in die Team-Datei fehlgeschlagen:\n{ex.Message}\n\nDeine Änderungen bleiben lokal erhalten.");
                        return false;
                    }
                }

                if (version != _treeVersion)
                {
                    // Während des Lesens/Schreibens lokal bearbeitet ⇒ nichts überschreiben, Basis aber fortschreiben
                    if (wrote) SaveTeamBase(newBase, newHash);
                    if (attempt < 3) continue;
                    return false;
                }

                ApplyTeamChildren(children);
                SaveTeamBase(newBase, newHash);
                _teamProbe = TeamLibraryFile.Probe(path);

                var info = new List<string>();
                if (merge.RemoteChanges > 0) info.Add($"{merge.RemoteChanges} Änderung(en) übernommen");
                if (wrote) info.Add($"{merge.LocalChanges} Änderung(en) gespeichert");
                UpdateTeamStatus(info.Count > 0 ? string.Join(", ", info) : "aktuell");
                if (interactive && merge.Notes.Count > 0) _dialogs.ShowInfo("Team-Bibliothek", string.Join("\n", merge.Notes.Take(20)));
                if (interactive || info.Count > 0) StatusText = "Team: " + (info.Count > 0 ? string.Join(", ", info) : "aktuell");
                return true;
            }
            UpdateTeamStatus("viele gleichzeitige Änderungen – bitte erneut Strg+S");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Team-Synchronisation", ex);
            UpdateTeamStatus("Fehler bei der Synchronisation");
            if (interactive) _dialogs.ShowError("Team-Bibliothek", ex.Message);
            return false;
        }
        finally
        {
            _teamBusy = false;
        }
    }

    /// <summary>Nur für Tests/Aufrufer ohne UI-SynchronizationContext.</summary>
    public bool SyncTeam(bool write) => SyncTeamAsync(write, interactive: true).GetAwaiter().GetResult();

    private void ApplyTeamChildren(IReadOnlyList<TreeNode> children)
    {
        if (TeamContainer is not { } container) return;
        foreach (var old in container.Children.ToList()) container.Remove(old);
        foreach (var child in children) container.Add(child);

        var item = SessionTree.Items.FirstOrDefault(i => i.Node == container);
        if (item is not null) SessionTree.ReloadChildren(item);
        else SessionTree.Load(SessionTree.Tree);
        Persist();
        Reachability?.Invalidate();
    }

    // ---- Automatisch nachladen --------------------------------------------------------------------

    /// <summary>Alle 15 s günstig prüfen (Zeitstempel/Größe), ob sich die Datei geändert hat.</summary>
    private void StartTeamPolling()
    {
        _teamUi ??= SynchronizationContext.Current;
        if (_teamUi is null || !HasTeamLibrary) return; // Tests: kein UI-Kontext ⇒ manuell
        _teamPoll?.Dispose();
        _teamPoll = new Timer(_ =>
        {
            try
            {
                if (_teamBusy || !HasTeamLibrary) return;
                var path = TeamLibraryFile.Resolve(Settings.TeamLibraryPath);
                if (path is null) return;
                var probe = TeamLibraryFile.Probe(path);
                if (probe is null || probe == _teamProbe) return;
                _teamUi.Post(_ => _ = SyncTeamAsync(write: false, interactive: false), null);
            }
            catch (Exception ex)
            {
                AppLog.Error("Team-Prüfung", ex);
            }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>Beim Start: Stand der Datei übernehmen (eigene, noch nicht gespeicherte Änderungen bleiben).</summary>
    private async Task StartTeamLibraryAsync()
    {
        if (!HasTeamLibrary) return;
        _teamUi = SynchronizationContext.Current;
        RefreshTeamPending();
        await SyncTeamAsync(write: false, interactive: false);
        StartTeamPolling();
    }

    /// <summary>Beim Beenden: nicht gespeicherte Team-Änderungen anbieten.</summary>
    private void OfferTeamSyncOnExit()
    {
        _teamPoll?.Dispose();
        _teamPoll = null;
        if (!HasTeamLibrary || Settings.TeamReadOnly) return;
        RefreshTeamPending();
        if (TeamPendingChanges == 0) return;
        if (_dialogs.Confirm("Team-Bibliothek",
                $"{TeamPendingChanges} Änderung(en) an der Team-Bibliothek sind noch nicht gespeichert.\n\nJetzt für alle speichern?"))
        {
            // Beim Beenden synchron im UI-Thread (kein await auf den blockierten Dispatcher ⇒ kein Deadlock)
            _inlineIo = true;
            try { SyncTeamAsync(write: true, interactive: true).GetAwaiter().GetResult(); }
            finally { _inlineIo = false; }
        }
    }

    // ---- Hilfen -----------------------------------------------------------------------------------

    private void RefreshTeamPending()
    {
        if (!HasTeamLibrary || TeamContainer is not { } container)
        {
            TeamPendingChanges = 0;
            return;
        }
        var (baseline, _) = LoadTeamBase();
        TeamPendingChanges = Settings.TeamReadOnly ? 0 : TreeMerge.CountChanges(container.Children, baseline);
        UpdateTeamStatus(null);
    }

    private string _teamLastInfo = "";
    private bool _inlineIo;

    /// <summary>Datei-I/O im Hintergrund – beim Beenden direkt (synchron), damit nichts auf den UI-Thread wartet.</summary>
    private Task<T> Io<T>(Func<T> work) => _inlineIo ? Task.FromResult(work()) : Task.Run(work);

    private void UpdateTeamStatus(string? info)
    {
        if (info is not null) _teamLastInfo = $"{info} ({DateTime.Now:HH:mm})";
        if (!HasTeamLibrary) { TeamStatus = ""; return; }
        var pending = TeamPendingChanges > 0 ? $" · {TeamPendingChanges} nicht gespeichert – Strg+S" : "";
        TeamStatus = $"Team{(Settings.TeamReadOnly ? " (nur lesen)" : "")}: {_teamLastInfo}{pending}";
    }

    partial void OnTeamPendingChangesChanged(int value) => UpdateTeamStatus(null);

    private (Dictionary<Guid, BaseEntry> Base, string? Hash) LoadTeamBase()
    {
        try
        {
            var json = _store.ReadDocument(TeamBaseDocument);
            var map = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Dictionary<Guid, BaseEntry>>(json);
            return (map ?? [], _store.ReadDocument(TeamHashDocument));
        }
        catch (JsonException)
        {
            return ([], null);
        }
    }

    private void SaveTeamBase(Dictionary<Guid, BaseEntry> baseline, string hash)
    {
        _store.WriteDocument(TeamBaseDocument, JsonSerializer.Serialize(baseline));
        _store.WriteDocument(TeamHashDocument, hash);
        RefreshTeamPending();
    }

    private Func<MasterPasswordRequest, string?> AskTeamPassword()
    {
        var ui = SynchronizationContext.Current;
        return r =>
        {
            string? answer = null;
            void Ask() => answer = _dialogs.AskMasterPassword($"Team-Bibliothek ({r.SourceName})", r.PreviousAttemptFailed, r.Attempt, r.MaxAttempts);
            if (ui is null || SynchronizationContext.Current == ui) Ask();
            else ui.Send(_ => Ask(), null);
            return answer;
        };
    }

    private void WarnIfTeamFileUnprotected(TeamSnapshot snapshot)
    {
        if (snapshot.Info.CustomMasterPassword) return;
        var withPasswords = Flatten(snapshot.Children).Count(n => !string.IsNullOrEmpty(n.Settings.Password));
        if (withPasswords == 0) return;
        _dialogs.ShowInfo("Sicherheitshinweis",
            $"Die Team-Datei enthält {withPasswords} gespeicherte Passwörter, ist aber nicht mit einem eigenen Master-Passwort geschützt.\n\n" +
            "mRemoteNG verwendet dann einen öffentlich bekannten Standardschlüssel – jeder mit Lesezugriff auf die Datei kann die Passwörter auslesen.\n\n" +
            "Empfehlung: in mRemoteNG unter Optionen → Sicherheit ein Master-Passwort für die Datei setzen (HostDeck fragt es dann einmalig ab) " +
            "oder persönliche Logins (z. B. RADIUS) im eigenen Vault hinterlegen statt in der Datei.");
    }

    private static IEnumerable<TreeNode> Flatten(IEnumerable<TreeNode> nodes) =>
        nodes.SelectMany(n => n is FolderNode f ? f.Descendants().Prepend(n) : [n]);
}
