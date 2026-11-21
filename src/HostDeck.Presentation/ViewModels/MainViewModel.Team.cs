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
        var path = _dialogs.OpenFile("Choose team library (shared confCons.xml)", "mRemoteNG (confCons.xml)|*.xml|All files|*.*");
        if (path is null) return;
        // Standard am Arbeitsplatz: bearbeiten und speichern. „Nur lesen“ (Jumphost) bewusst nur über die Einstellungen –
        // eine Ja/Nein-Frage beim Verbinden war missverständlich.
        await ConnectTeamLibraryAsync(path, readOnly: false);
    }

    // ---- Schreibschutz (Jumphost) -----------------------------------------------------------------

    /// <summary>Liegt der Knoten in einer schreibgeschützten Team-Kopie? (Team-Ordner selbst zählt nicht – löschen = trennen.)</summary>
    public bool IsReadOnlyTeamNode(TreeNode? node) =>
        node is not null && Settings.TeamReadOnly && HasTeamLibrary && TeamContainer is { } container
        && node != container && node.Ancestors().Contains(container);

    /// <summary>Zielordner für neue Einträge (ausgewählter Ordner bzw. Ordner der ausgewählten Verbindung).</summary>
    private bool TargetIsReadOnlyTeam()
    {
        var selected = SessionTree.SelectedItem?.Node;
        var parent = selected is FolderNode f ? f : selected?.Parent;
        return parent is not null && (IsReadOnlyTeamNode(parent) || (Settings.TeamReadOnly && parent == TeamContainer));
    }

    private bool BlockReadOnlyTeam(bool blocked)
    {
        if (!blocked) return false;
        _dialogs.ShowInfo("Team library is read-only",
            "This team library copy is read-only (Settings → Team library → “Read-only”), so its entries cannot be changed here.\n\n" +
            "You can still assign your own login from the vault (Properties → Login).\n" +
            "To edit team entries, turn off “Read-only” in the settings.");
        return true;
    }

    public async Task<bool> ConnectTeamLibraryAsync(string path, bool readOnly)
    {
        if (!_loadSucceeded || _teamBusy) return false;
        _teamBusy = true;
        try
        {
            StatusText = "Reading team library …";
            var resolved = TeamLibraryFile.Resolve(path) ?? path;
            TeamSnapshot snapshot;
            try
            {
                // Passwort-Rückfrage im UI-Thread erzeugen (sie merkt sich den UI-Kontext) – nicht erst im Hintergrund-Thread
                var ask = AskTeamPassword();
                snapshot = await Io(() => TeamLibraryFile.Read(resolved, null, ask));
            }
            catch (Exception ex) when (ex is ConfConsAuthenticationException or ConfConsFormatException or IOException or UnauthorizedAccessException)
            {
                if (ex is not ConfConsAuthenticationException { Cancelled: true })
                    _dialogs.ShowError("Team library", $"The file could not be read:\n{ex.Message}");
                StatusText = "Team library not set up";
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
                if (!_dialogs.Confirm("Replace existing copy?",
                        $"{duplicates.Count} entries from this file were imported before.\n\n" +
                        "This old copy will be replaced by the team library (personal credential assignments are kept). " +
                        "A backup is created automatically first.\n\nContinue?"))
                {
                    if (existing is not null) tree.Root.Insert(0, existing);
                    StatusText = "Team library not set up";
                    return false;
                }
                try { AppLog.Info($"Backup before team setup: {_store.Backup(BackupDirectory)}"); }
                catch (Exception ex) { AppLog.Error("Backup before team setup", ex); }
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
            UpdateTeamStatus($"connected ({Flatten(snapshot.Children).OfType<ConnectionNode>().Count()} connections)");
            StatusText = "Team library set up";
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
        RefreshTeamPending();
        var pending = TeamPendingChanges > 0
            ? $"\n\nWarning: {TeamPendingChanges} change(s) have not been saved to the team file yet and will be lost. Press Ctrl+S first to keep them."
            : "";
        if (!_dialogs.Confirm("Disconnect team library",
                "The Team folder will be removed from your tree. The shared file itself is not changed – you can connect again at any time." + pending))
            return;
        if (TeamContainer is { } container) SessionTree.Tree.Root.Remove(container);
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

    private const int TeamKdfIterations = 10_000;

    /// <summary>
    /// Master-Passwort der Team-Datei setzen oder ändern (in mRemoteNG: Wurzelknoten „Connections“ → „Password protect“).
    /// Danach fragen HostDeck und mRemoteNG jeden Kollegen einmal nach diesem Passwort.
    /// </summary>
    [RelayCommand]
    private async Task SetTeamPassword()
    {
        if (!HasTeamLibrary) return;
        if (Settings.TeamReadOnly)
        {
            _dialogs.ShowInfo("Team library", "This copy is read-only (e.g. jump host). Please set the password on your workstation.");
            return;
        }
        var first = _dialogs.Prompt("Team file password",
            "New master password for the shared team file (at least 10 characters).\n\n" +
            "Everyone using the file – in HostDeck or mRemoteNG – will be asked for it once. Share it only through a safe channel.",
            secret: true);
        if (first is null) return;
        if (TeamPasswordProblem(first) is { } problem) { _dialogs.ShowError("Team file password", problem); return; }
        var second = _dialogs.Prompt("Team file password", "Repeat the password:", secret: true);
        if (second is null) return;
        if (second != first) { _dialogs.ShowError("Team file password", "The passwords do not match."); return; }

        if (await SyncTeamAsync(write: true, interactive: true, newPassword: first))
            _dialogs.ShowInfo("Team file password", "The team file is now protected with the new password.\n\nColleagues will be asked for it the next time they open or sync the file.");
    }

    /// <summary>mRemoteNG/BouncyCastle verarbeitet nur Latin-1-Zeichen (1 Byte pro Zeichen).</summary>
    internal static string? TeamPasswordProblem(string password) =>
        password.Length < 10 ? "The password must be at least 10 characters long."
        : password.Any(c => c > '\u00FF') ? "The password may only contain Latin-1 characters (mRemoteNG limitation) – no emoji or special Unicode characters."
        : null;

    // ---- Synchronisieren --------------------------------------------------------------------------

    /// <summary>Strg+S: Änderungen anderer übernehmen und eigene in die Team-Datei schreiben.</summary>
    [RelayCommand]
    private Task SyncTeam() => SyncTeamAsync(write: true, interactive: true);

    /// <summary>
    /// Kern der Synchronisation. <paramref name="write"/> = eigene Änderungen speichern (Strg+S), sonst nur
    /// Änderungen aus der Datei übernehmen (automatisch). Konflikte werden nur beim Speichern abgefragt.
    /// </summary>
    public async Task<bool> SyncTeamAsync(bool write, bool interactive, string? newPassword = null)
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
            if (interactive) _dialogs.ShowInfo("Team library", "This copy is read-only (e.g. jump host). Please make changes on your workstation.");
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
                    var ask = interactive ? AskTeamPassword() : null; // im UI-Thread erzeugen
                    remote = await Io(() => TeamLibraryFile.Read(path, string.IsNullOrEmpty(knownPassword) ? null : knownPassword,
                        ask));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    UpdateTeamStatus("file not reachable – offline copy");
                    if (interactive) _dialogs.ShowError("Team library", $"The team file is not reachable:\n{ex.Message}\n\nYour changes are kept locally.");
                    return false;
                }
                catch (ConfConsAuthenticationException ex)
                {
                    UpdateTeamStatus(ex.Cancelled ? "password not entered" : "team password required – Ctrl+S");
                    if (interactive && !ex.Cancelled) _dialogs.ShowError("Team library", ex.Message);
                    return false;
                }
                catch (ConfConsFormatException ex)
                {
                    UpdateTeamStatus("file damaged?");
                    AppLog.Error("Read team library", ex);
                    if (interactive) _dialogs.ShowError("Team library", $"The team file could not be read:\n{ex.Message}");
                    return false;
                }
                if (remote.PasswordWasEntered && remote.Password is { } entered) _store.WriteDocument(TeamPasswordDocument, entered);

                var merge = TreeMerge.Merge(baseline, mine, remote.Children);
                if (write && merge.Conflicts.Count > 0 && interactive)
                {
                    var list = string.Join("\n", merge.Conflicts.Take(15).Select(c => "• " + c.Path))
                               + (merge.Conflicts.Count > 15 ? $"\n… and {merge.Conflicts.Count - 15} more" : "");
                    var keepMine = _dialogs.Confirm("Changed at the same time",
                        $"These entries were changed differently by you and by someone else:\n\n{list}\n\n" +
                        "“Yes”: save your version\n“No”: take the version from the team file");
                    if (!keepMine) merge = TreeMerge.Merge(baseline, mine, remote.Children, MergeConflictResolution.TakeTheirs);
                }

                var children = merge.Children;
                var newBase = merge.NewBase;
                var newHash = remote.Hash;
                var wrote = false;
                if (write && (merge.LocalChanges > 0 || newPassword is not null))
                {
                    try
                    {
                        // Neues Team-Passwort: gleich etwas mehr KDF-Iterationen (mRemoteNG liest den Wert aus der Datei)
                        var format = newPassword is null ? remote.Info
                            : remote.Info with { KdfIterations = Math.Max(remote.Info.KdfIterations ?? 1000, TeamKdfIterations) };
                        var password = newPassword ?? remote.Password;
                        var merged = children;
                        (newHash, var written) = await Io(() => TeamLibraryFile.Write(path, merged, format, password, remote.Hash, BackupTeamFile));
                        children = TreeMerge.WithPersonalFrom(written, merged);
                        newBase = TreeMerge.Snapshot(children);
                        wrote = true;
                        if (newPassword is not null)
                        {
                            _store.WriteDocument(TeamPasswordDocument, newPassword);
                            newPassword = null; // erledigt – bei einem erneuten Durchlauf nicht noch einmal erzwingen
                        }
                    }
                    catch (TeamFileChangedException)
                    {
                        continue; // jemand war schneller ⇒ neu einlesen und erneut zusammenführen
                    }
                    catch (TeamFileLockedException ex)
                    {
                        UpdateTeamStatus("locked right now – please press Ctrl+S again");
                        if (interactive) _dialogs.ShowInfo("Team library", ex.Message);
                        return false;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Error("Write team library", ex);
                        UpdateTeamStatus("saving failed");
                        if (interactive) _dialogs.ShowError("Team library", $"Saving to the team file failed:\n{ex.Message}\n\nYour changes are kept locally.");
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
                if (merge.RemoteChanges > 0) info.Add($"{merge.RemoteChanges} change(s) pulled");
                if (wrote) info.Add($"{merge.LocalChanges} change(s) saved");
                UpdateTeamStatus(info.Count > 0 ? string.Join(", ", info) : "up to date");
                if (interactive && merge.Notes.Count > 0) _dialogs.ShowInfo("Team library", string.Join("\n", merge.Notes.Take(20)));
                if (interactive || info.Count > 0) StatusText = "Team: " + (info.Count > 0 ? string.Join(", ", info) : "up to date");
                return true;
            }
            UpdateTeamStatus("many simultaneous changes – please press Ctrl+S again");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Team sync", ex);
            UpdateTeamStatus("sync error");
            if (interactive) _dialogs.ShowError("Team library", ex.Message);
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
                AppLog.Error("Team check", ex);
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
        if (_dialogs.Confirm("Team library",
                $"{TeamPendingChanges} change(s) to the team library are not saved yet.\n\nSave them for everyone now?"))
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
        var pending = TeamPendingChanges > 0 ? $" · {TeamPendingChanges} unsaved – Ctrl+S" : "";
        TeamStatus = $"Team{(Settings.TeamReadOnly ? " (read-only)" : "")}: {_teamLastInfo}{pending}";
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
            void Ask() => answer = _dialogs.AskMasterPassword($"Team library ({r.SourceName})", r.PreviousAttemptFailed, r.Attempt, r.MaxAttempts);
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
        _dialogs.ShowInfo("Security notice",
            $"The team file contains {withPasswords} stored passwords but is not protected by its own master password.\n\n" +
            "mRemoteNG then uses a publicly known default key – anyone with read access to the file can extract the passwords.\n\n" +
            "Recommendation: protect the file with a password – in HostDeck via Ctrl+K → “Set team file password …” " +
            "(in mRemoteNG: select the root node “Connections” → property “Password protect”). " +
            "Personal logins (e.g. RADIUS) belong in your own vault instead of the file.");
    }

    private static IEnumerable<TreeNode> Flatten(IEnumerable<TreeNode> nodes) =>
        nodes.SelectMany(n => n is FolderNode f ? f.Descendants().Prepend(n) : [n]);
}
