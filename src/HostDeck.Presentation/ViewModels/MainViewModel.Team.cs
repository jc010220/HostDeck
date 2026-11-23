using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Core.Model;
using HostDeck.Core.Sync;
using HostDeck.Import.MRemoteNG;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.Snippets;
using HostDeck.Presentation.Team;

namespace HostDeck.Presentation.ViewModels;

/// <summary>
/// Laufzeitzustand einer verbundenen Bibliothek (gemeinsame confCons.xml eines Teams oder eigene Datei, z. B. auf
/// OneDrive). Die erste Bibliothek nutzt die bisherigen Einstellungen und Dokumentnamen (kompatibel mit älteren
/// Versionen); weitere stehen in <see cref="AppSettings.Libraries"/> und haben eigene, mit ihrer ID versehene Dokumente.
/// </summary>
public sealed class LibraryState
{
    private readonly Func<AppSettings> _settings;

    internal LibraryState(Func<AppSettings> settings, LibraryConfig? config)
    {
        _settings = settings;
        Config = config;
    }

    internal LibraryConfig? Config { get; set; }

    /// <summary>„team“ für die erste (bisherige) Bibliothek, sonst die ID aus den Einstellungen.</summary>
    public string Id => Config?.Id ?? PrimaryId;
    internal const string PrimaryId = "team";
    public bool IsPrimary => Config is null;

    /// <summary>Wert der Markierung am Ordner im Baum (erste Bibliothek: „1“ wie in älteren Versionen).</summary>
    internal string Marker => IsPrimary ? "1" : Config!.Id;

    public string? Path
    {
        get => IsPrimary ? _settings().TeamLibraryPath : Config!.Path;
        internal set
        {
            if (IsPrimary) _settings().TeamLibraryPath = value;
            else Config!.Path = value ?? "";
        }
    }

    public bool ReadOnly
    {
        get => IsPrimary ? _settings().TeamReadOnly : Config!.ReadOnly;
        internal set
        {
            if (IsPrimary) _settings().TeamReadOnly = value;
            else Config!.ReadOnly = value;
        }
    }

    internal string ConfiguredName => IsPrimary ? "Team" : Config!.Name;

    /// <summary>Name des Dokuments in der persönlichen Datenbank (erste Bibliothek: bisheriger Name).</summary>
    internal string Doc(string name) => IsPrimary ? name : $"{name}@{Config!.Id}";

    // Laufzeit
    internal bool Busy;
    internal (DateTime, long)? Probe;
    internal (DateTime, long)? SnippetProbe;
    internal string LastInfo = "";
    internal int Pending;
}

/// <summary>
/// Bibliotheken: gemeinsame confCons.xml (Netzlaufwerk bzw. lokale Kopie auf dem Jumphost) oder eigene Datei
/// (z. B. OneDrive) – jede als eigener Ordner im Baum. Änderungen anderer werden automatisch übernommen, eigene mit
/// Strg+S zusammengeführt und gespeichert (wie KeePass „Synchronisieren“). Persönliche Zugangsdaten-Zuordnungen bleiben lokal.
/// </summary>
public sealed partial class MainViewModel
{
    internal const string TeamPasswordDocument = "team-password";
    internal const string TeamBaseDocument = "team-base";
    private const string TeamHashDocument = "team-hash";

    private int _treeVersion;
    private Timer? _teamPoll;
    private SynchronizationContext? _teamUi;
    private readonly Dictionary<string, LibraryState> _libraryStates = new(StringComparer.Ordinal);

    /// <summary>Statuszeile der Bibliotheken (leer = keine eingerichtet).</summary>
    [ObservableProperty] private string _teamStatus = "";
    [ObservableProperty] private int _teamPendingChanges;

    /// <summary>Alle verbundenen Bibliotheken (erste = bisherige Team-Bibliothek).</summary>
    public IReadOnlyList<LibraryState> Libraries
    {
        get
        {
            var list = new List<LibraryState>();
            if (!string.IsNullOrWhiteSpace(Settings.TeamLibraryPath)) list.Add(StateFor(null));
            foreach (var config in Settings.Libraries.Where(c => !string.IsNullOrWhiteSpace(c.Path))) list.Add(StateFor(config));
            return list;
        }
    }

    private LibraryState StateFor(LibraryConfig? config)
    {
        var id = config?.Id ?? LibraryState.PrimaryId;
        if (!_libraryStates.TryGetValue(id, out var state))
            _libraryStates[id] = state = new LibraryState(() => Settings, config);
        state.Config = config;
        return state;
    }

    public bool HasTeamLibrary => Libraries.Count > 0;

    /// <summary>Ordner der ersten Bibliothek (Kompatibilität; mehrere Bibliotheken ⇒ <see cref="ContainerOf"/>).</summary>
    public FolderNode? TeamContainer => (Libraries.FirstOrDefault() is { } lib ? ContainerOf(lib) : null) ?? TeamContainerFolders().FirstOrDefault();

    public FolderNode? ContainerOf(LibraryState lib) =>
        SessionTree.Tree.Root.Children.OfType<FolderNode>().FirstOrDefault(f =>
            f.Extensions.TryGetValue(TreeMerge.TeamContainerKey, out var marker) && marker == lib.Marker);

    /// <summary>Angezeigter Name: Name des Ordners im Baum (umbenennbar), sonst der eingestellte.</summary>
    public string NameOf(LibraryState lib) => ContainerOf(lib)?.Name ?? lib.ConfiguredName;

    private LibraryState? LibraryOfContainer(FolderNode folder) =>
        folder.Extensions.TryGetValue(TreeMerge.TeamContainerKey, out var marker)
            ? Libraries.FirstOrDefault(l => l.Marker == marker) : null;

    /// <summary>Bibliothek, zu der der Knoten gehört (auch der Bibliotheksordner selbst), sonst null.</summary>
    public LibraryState? LibraryOf(TreeNode? node)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n is FolderNode f && LibraryOfContainer(f) is { } lib) return lib;
        return null;
    }

    private void NotifyLibrariesChanged()
    {
        OnPropertyChanged(nameof(HasTeamLibrary));
        OnPropertyChanged(nameof(Libraries));
    }

    /// <summary>
    /// Bibliothek für Befehle aus Menü/Palette: die der Auswahl im Baum, sonst die einzige. Bei mehreren ohne passende
    /// Auswahl wird nachgefragt (Ordner auswählen).
    /// </summary>
    private LibraryState? LibraryForCommand(string action)
    {
        var libs = Libraries;
        if (LibraryOf(SessionTree.SelectedItem?.Node) is { } selected) return selected;
        if (libs.Count == 1) return libs[0];
        if (libs.Count > 1)
            _dialogs.ShowInfo("Library", $"Several libraries are connected. Select the library folder (or an entry in it) in the tree, then choose “{action}” again.");
        return null;
    }

    private const int TeamBackupSlots = 10;

    /// <summary>
    /// Vorheriger Stand der Bibliotheksdatei vor jedem Speichern – verschlüsselt in der HostDeck-Datenbank
    /// (eine Datei-Kopie wäre bei mRemoteNG-Standardschlüssel praktisch Klartext).
    /// </summary>
    private void BackupTeamFile(LibraryState lib, byte[] content)
    {
        var index = int.TryParse(_store.ReadDocument(lib.Doc("team-backup-index")), out var i) ? (i + 1) % TeamBackupSlots : 0;
        _store.WriteDocument(lib.Doc($"team-backup-{index}"), JsonSerializer.Serialize(new TeamBackup(DateTime.UtcNow, System.Text.Encoding.UTF8.GetString(content))));
        _store.WriteDocument(lib.Doc("team-backup-index"), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Gesicherte Stände (neueste zuerst) – z. B. zum Wiederherstellen nach einem Fehler.</summary>
    public IReadOnlyList<(DateTime SavedUtc, string Content)> TeamBackups(LibraryState? lib = null)
    {
        lib ??= StateFor(null);
        var list = new List<(DateTime, string)>();
        for (var i = 0; i < TeamBackupSlots; i++)
        {
            if (_store.ReadDocument(lib.Doc($"team-backup-{i}")) is not { Length: > 0 } json) continue;
            try { if (JsonSerializer.Deserialize<TeamBackup>(json) is { } b) list.Add((b.SavedUtc, b.Content)); }
            catch (JsonException) { }
        }
        return list.OrderByDescending(b => b.Item1).ToList();
    }

    private sealed record TeamBackup(DateTime SavedUtc, string Content);

    // ---- Einrichten / Trennen ---------------------------------------------------------------------

    /// <summary>Bestehende Bibliotheksdatei verbinden (weitere Bibliothek, falls schon eine verbunden ist).</summary>
    [RelayCommand]
    private async Task ConnectTeamLibrary()
    {
        var path = _dialogs.OpenFile("Choose library (shared confCons.xml)", "mRemoteNG (confCons.xml)|*.xml|All files|*.*");
        if (path is null) return;
        string? name = null;
        if (HasTeamLibrary && !Libraries.Any(l => SamePath(l.Path, path)))
        {
            name = _dialogs.Prompt("Library name", "Name of the folder for this library in your tree:", SuggestLibraryName(path));
            if (name is null) return;
        }
        // Standard am Arbeitsplatz: bearbeiten und speichern. „Nur lesen“ (Jumphost) bewusst nur über die Einstellungen.
        await ConnectTeamLibraryAsync(path, readOnly: false, name: name);
    }

    /// <summary>Neue, leere Bibliotheksdatei anlegen – z. B. eigene Verbindungen auf OneDrive oder ein weiteres Team.</summary>
    [RelayCommand]
    private async Task CreateLibrary()
    {
        var path = _dialogs.SaveFile("Create new library file", "mRemoteNG (confCons.xml)|*.xml", "confCons.xml");
        if (path is null) return;
        if (File.Exists(path))
        {
            _dialogs.ShowError("Library", "This file already exists. Use “Connect library …” to connect an existing file.");
            return;
        }
        var name = _dialogs.Prompt("Library name", "Name of the folder for this library in your tree:", SuggestLibraryName(path));
        if (name is null) return;
        if (!CreateLibraryFile(path)) return;
        await ConnectTeamLibraryAsync(path, readOnly: false, name: name);
    }

    /// <summary>Leere confCons.xml schreiben; auf Wunsch gleich mit eigenem Passwort (empfohlen).</summary>
    private bool CreateLibraryFile(string path)
    {
        var password = _dialogs.Prompt("Library password",
            "Password for the new library file (at least 10 characters) – recommended, because without it stored passwords " +
            "are only protected by mRemoteNG's publicly known default key.\n\nLeave empty for no password.", secret: true);
        if (password is null) return false;
        if (password.Length > 0)
        {
            if (TeamPasswordProblem(password) is { } problem) { _dialogs.ShowError("Library password", problem); return false; }
            var repeat = _dialogs.Prompt("Library password", "Repeat the password:", secret: true);
            if (repeat is null) return false;
            if (repeat != password) { _dialogs.ShowError("Library password", "The passwords do not match."); return false; }
        }
        try
        {
            var exporter = new ConfConsExporter(new ConfConsExportOptions
            {
                MasterPassword = password.Length > 0 ? password : null,
                KdfIterations = password.Length > 0 ? TeamKdfIterations : Import.MRemoteNG.Crypto.ConfConsDefaults.DefaultKdfIterations,
            });
            var xml = exporter.Export(new ConnectionTree());
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                writer.Write(xml);
            if (password.Length > 0) _pendingNewLibraryPassword = (path, password);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Library", $"The file could not be created:\n{ex.Message}");
            return false;
        }
    }

    /// <summary>Passwort einer gerade angelegten Datei – erspart die sofortige Rückfrage beim Verbinden.</summary>
    private (string Path, string Password)? _pendingNewLibraryPassword;

    internal static string SuggestLibraryName(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.Equals(name, "confCons", StringComparison.OrdinalIgnoreCase) || name.Length == 0)
            name = new DirectoryInfo(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "").Name;
        return string.IsNullOrWhiteSpace(name) ? "Library" : name;
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(System.IO.Path.GetFullPath(a.Trim()), System.IO.Path.GetFullPath(b.Trim()), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase); }
    }

    // ---- Schreibschutz (Jumphost) -----------------------------------------------------------------

    /// <summary>Liegt der Knoten in einer schreibgeschützten Bibliothek? (Bibliotheksordner selbst zählt nicht – löschen = trennen.)</summary>
    public bool IsReadOnlyTeamNode(TreeNode? node) =>
        node is not null && LibraryOf(node) is { ReadOnly: true } lib && ContainerOf(lib) is { } container && node != container;

    /// <summary>Darf hier nichts abgelegt werden? (Ordner in bzw. Wurzel einer schreibgeschützten Bibliothek.)</summary>
    public bool IsReadOnlyTeamDestination(FolderNode folder) => LibraryOf(folder) is { ReadOnly: true };

    /// <summary>Zielordner für neue Einträge (ausgewählter Ordner bzw. Ordner der ausgewählten Verbindung).</summary>
    private bool TargetIsReadOnlyTeam()
    {
        var selected = SessionTree.SelectedItem?.Node;
        var parent = selected is FolderNode f ? f : selected?.Parent;
        return parent is not null && IsReadOnlyTeamDestination(parent);
    }

    private bool BlockReadOnlyTeam(bool blocked)
    {
        if (!blocked) return false;
        _dialogs.ShowInfo("Library is read-only",
            "This library copy is read-only (Settings → Libraries → “Read-only”), so its entries cannot be changed here.\n\n" +
            "You can still assign your own login from the vault (Properties → Login).\n" +
            "To edit these entries, turn off “Read-only” in the settings.");
        return true;
    }

    /// <summary>
    /// Bibliothek verbinden. Ohne <paramref name="target"/>: dieselbe Datei wird neu eingelesen, sonst wird sie die
    /// erste Bibliothek (falls noch keine) bzw. eine weitere. Mit <paramref name="target"/>: diese Bibliothek auf die
    /// (ggf. neue) Datei umstellen.
    /// </summary>
    public async Task<bool> ConnectTeamLibraryAsync(string path, bool readOnly, LibraryState? target = null, string? name = null)
    {
        if (!_loadSucceeded) return false;
        var libs = Libraries;
        target ??= libs.FirstOrDefault(l => SamePath(l.Path, path))
                   ?? (string.IsNullOrWhiteSpace(Settings.TeamLibraryPath) ? StateFor(null)
                       : StateFor(new LibraryConfig { Name = name ?? SuggestLibraryName(path), Path = "" }));
        if (target.Busy) return false;
        if (libs.FirstOrDefault(l => l != target && SamePath(l.Path, path)) is { } other)
        {
            _dialogs.ShowError("Library", $"This file is already connected as “{NameOf(other)}”.");
            return false;
        }

        target.Busy = true;
        try
        {
            StatusText = "Reading library …";
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                if (!_dialogs.Confirm("Library", $"The file does not exist:\n{path}\n\nCreate a new, empty library file there?")) { StatusText = ""; return false; }
                if (!CreateLibraryFile(path)) { StatusText = ""; return false; }
            }
            var resolved = TeamLibraryFile.Resolve(path) ?? path;
            TeamSnapshot snapshot;
            try
            {
                string? known = null;
                if (_pendingNewLibraryPassword is { } created && SamePath(created.Path, resolved)) known = created.Password;
                _pendingNewLibraryPassword = null;
                // Passwort-Rückfrage im UI-Thread erzeugen (sie merkt sich den UI-Kontext) – nicht erst im Hintergrund-Thread
                var ask = AskTeamPassword();
                snapshot = await Io(() => TeamLibraryFile.Read(resolved, known, ask));
            }
            catch (Exception ex) when (ex is ConfConsAuthenticationException or ConfConsFormatException or IOException or UnauthorizedAccessException)
            {
                if (ex is not ConfConsAuthenticationException { Cancelled: true })
                    _dialogs.ShowError("Library", $"The file could not be read:\n{ex.Message}");
                StatusText = "Library not set up";
                return false;
            }

            var tree = SessionTree.Tree;
            var personal = new Dictionary<Guid, Guid?>();
            var existing = ContainerOf(target);
            var teamIds = Flatten(snapshot.Children).Select(n => n.Id).ToHashSet();

            // Dieselben Einträge in einer anderen Bibliothek (Kopie derselben Datei) ⇒ doppelte IDs im Baum vermeiden
            var clash = libs.Where(l => l != target).FirstOrDefault(l => ContainerOf(l) is { } c && c.Descendants().Any(n => teamIds.Contains(n.Id)));
            if (clash is not null)
            {
                _dialogs.ShowError("Library",
                    $"This file contains the same entries as the library “{NameOf(clash)}” – it is probably a copy of that file. " +
                    "Connect only one of them.");
                StatusText = "Library not set up";
                return false;
            }

            if (existing is not null)
            {
                foreach (var node in existing.Descendants()) personal[node.Id] = node.Settings.CredentialId;
                tree.Root.Remove(existing);
            }

            // Früher manuell importierte Kopie derselben Datei (gleiche IDs) ersetzen, persönliche Zuordnungen übernehmen
            var duplicates = tree.Root.Descendants().Where(n => teamIds.Contains(n.Id) && LibraryOf(n) is null).ToList();
            if (duplicates.Count > 0)
            {
                if (!_dialogs.Confirm("Replace existing copy?",
                        $"{duplicates.Count} entries from this file were imported before.\n\n" +
                        "This old copy will be replaced by the library (personal credential assignments are kept). " +
                        "A backup is created automatically first.\n\nContinue?"))
                {
                    if (existing is not null) tree.Root.Insert(0, existing);
                    StatusText = "Library not set up";
                    return false;
                }
                try { AppLog.Info($"Backup before library setup: {_store.Backup(BackupDirectory)}"); }
                catch (Exception ex) { AppLog.Error("Backup before library setup", ex); }
                foreach (var node in duplicates) personal.TryAdd(node.Id, node.Settings.CredentialId);
                foreach (var node in duplicates.Where(d => !duplicates.Any(o => o != d && d.Ancestors().Contains(o))))
                    node.Parent?.Remove(node);
            }

            var container = new FolderNode(existing?.Name ?? (string.IsNullOrWhiteSpace(name) ? target.ConfiguredName : name.Trim())) { IsExpanded = true };
            container.Extensions[TreeMerge.TeamContainerKey] = target.Marker;
            // Bibliotheken oben, in der Reihenfolge, in der sie verbunden wurden
            var position = tree.Root.Children.TakeWhile(c => c is FolderNode f && f.Extensions.ContainsKey(TreeMerge.TeamContainerKey)).Count();
            tree.Root.Insert(existing is not null && target.IsPrimary ? 0 : position, container);
            foreach (var child in snapshot.Children) container.Add(child);
            foreach (var node in container.Descendants())
                if (personal.TryGetValue(node.Id, out var credential)) node.Settings.CredentialId = credential;

            if (target.IsPrimary)
            {
                Settings.TeamLibraryPath = path;
                Settings.TeamReadOnly = readOnly;
            }
            else
            {
                target.Config!.Path = path;
                target.Config.ReadOnly = readOnly;
                target.Config.Name = container.Name;
                if (!Settings.Libraries.Contains(target.Config)) Settings.Libraries.Add(target.Config);
            }
            SaveSettings();
            if (snapshot.Password is { } pw) _store.WriteDocument(target.Doc(TeamPasswordDocument), pw);
            _store.WriteDocument(target.Doc(ResolvedFileDocument), resolved);
            SaveTeamBase(target, TreeMerge.Snapshot(container.Children), snapshot.Hash);
            target.Probe = TeamLibraryFile.Probe(resolved);

            SessionTree.Load(tree);
            Persist();
            UpdateTeamStatus(target, $"connected ({Flatten(snapshot.Children).OfType<ConnectionNode>().Count()} connections)");
            StatusText = $"Library “{container.Name}” set up";
            WarnIfTeamFileUnprotected(snapshot);
            StartTeamPolling();
            await SyncTeamSnippetsAsync(target);
            return true;
        }
        finally
        {
            target.Busy = false;
            NotifyLibrariesChanged();
        }
    }

    [RelayCommand]
    private void DisconnectTeamLibrary()
    {
        // Auch ohne Verbindungseinstellung: ein verwaister Bibliotheksordner (z. B. aus einer Sicherung) muss entfernbar sein
        if (!HasTeamLibrary)
        {
            if (TeamContainerFolders().FirstOrDefault() is { } orphan && _dialogs.Confirm("Remove folder", $"Remove the folder “{orphan.Name}” (no library connected)?"))
            {
                SessionTree.Tree.Root.Remove(orphan);
                SessionTree.Load(SessionTree.Tree);
                Persist();
            }
            return;
        }
        if (LibraryForCommand("Disconnect library") is { } lib) DisconnectLibrary(lib, ask: true);
    }

    private IEnumerable<FolderNode> TeamContainerFolders() =>
        SessionTree.Tree.Root.Children.OfType<FolderNode>().Where(f => f.Extensions.ContainsKey(TreeMerge.TeamContainerKey));

    /// <summary>Bibliothek trennen: Ordner aus dem Baum, lokale Zwischenstände löschen. Die Datei selbst bleibt unverändert.</summary>
    public bool DisconnectLibrary(LibraryState lib, bool ask)
    {
        RefreshTeamPending(lib);
        var name = NameOf(lib);
        var pending = lib.Pending > 0
            ? $"\n\nWarning: {lib.Pending} change(s) have not been saved to the library file yet and will be lost. Press Ctrl+S first to keep them."
            : "";
        if (ask && !_dialogs.Confirm("Disconnect library",
                $"The folder “{name}” will be removed from your tree. The file itself is not changed – you can connect it again at any time." + pending))
            return false;
        if (ContainerOf(lib) is { } container) SessionTree.Tree.Root.Remove(container);
        ForgetLibrary(lib);
        SessionTree.Load(SessionTree.Tree);
        Persist();
        RefreshAllTeamStatus();
        NotifyLibrariesChanged();
        return true;
    }

    /// <summary>Verbindung und lokale Zwischenstände einer Bibliothek entfernen (Baum bleibt unangetastet).</summary>
    private void ForgetLibrary(LibraryState lib)
    {
        if (lib.IsPrimary) Settings.TeamLibraryPath = null;
        else Settings.Libraries.Remove(lib.Config!);
        SaveSettings();
        _store.WriteDocument(lib.Doc(TeamBaseDocument), "{}");
        _store.WriteDocument(lib.Doc(TeamPasswordDocument), "");
        _store.WriteDocument(lib.Doc(TeamSnippetsCacheDocument), "[]");
        _store.WriteDocument(lib.Doc(TeamSnippetsPendingDocument), "[]");
        lib.Pending = 0;
        lib.LastInfo = "";
        lib.Probe = lib.SnippetProbe = null;
        ShowCachedTeamSnippets(Snippets);
        if (!HasTeamLibrary)
        {
            _teamPoll?.Dispose();
            _teamPoll = null;
        }
    }

    /// <summary>Bearbeitete Liste aus den Einstellungen übernehmen (trennen, umstellen, neu verbinden, umbenennen).</summary>
    private async Task ApplyLibrarySettingsAsync(IReadOnlyList<SettingsViewModel.LibraryRow> rows)
    {
        foreach (var lib in Libraries.ToList())
        {
            var row = rows.FirstOrDefault(r => r.Id == lib.Id);
            var path = row?.Path.Trim() ?? "";
            if (path.Length == 0)
            {
                DisconnectLibrary(lib, ask: true);
                continue;
            }
            var newName = row!.Name.Trim();
            if (!SamePath(path, lib.Path))
            {
                await ConnectTeamLibraryAsync(path, row.ReadOnly, lib, newName.Length > 0 ? newName : null);
                continue;
            }
            if (row.ReadOnly != lib.ReadOnly)
            {
                lib.ReadOnly = row.ReadOnly;
                SaveSettings();
                RefreshTeamPending(lib);
            }
            if (newName.Length > 0 && ContainerOf(lib) is { } container && container.Name != newName)
            {
                container.Name = newName;
                if (!lib.IsPrimary) { lib.Config!.Name = newName; SaveSettings(); }
                SessionTree.Load(SessionTree.Tree);
                Persist();
                RefreshAllTeamStatus();
            }
        }
        foreach (var row in rows.Where(r => r.Id is null && r.Path.Trim().Length > 0))
        {
            var name = row.Name.Trim();
            var path = row.Path.Trim();
            await ConnectTeamLibraryAsync(path, row.ReadOnly, name: name.Length > 0 ? name : SuggestLibraryName(path));
        }
        NotifyLibrariesChanged();
    }

    /// <summary>Zeilen für den Einstellungsdialog.</summary>
    private List<SettingsViewModel.LibraryRow> LibraryRows() =>
        Libraries.Select(l => new SettingsViewModel.LibraryRow { Id = l.Id, Name = NameOf(l), Path = l.Path ?? "", ReadOnly = l.ReadOnly }).ToList();

    private const int TeamKdfIterations = 10_000;

    /// <summary>
    /// Master-Passwort der Bibliotheksdatei setzen oder ändern (in mRemoteNG: Wurzelknoten „Connections“ → „Password protect“).
    /// Danach fragen HostDeck und mRemoteNG jeden Kollegen einmal nach diesem Passwort.
    /// </summary>
    [RelayCommand]
    private async Task SetTeamPassword()
    {
        if (LibraryForCommand("Set library password") is not { } lib) return;
        var name = NameOf(lib);
        if (lib.ReadOnly)
        {
            _dialogs.ShowInfo("Library", $"“{name}” is read-only here (e.g. jump host). Please set the password on your workstation.");
            return;
        }
        var first = _dialogs.Prompt("Library file password",
            $"New master password for the library file “{name}” (at least 10 characters).\n\n" +
            "Everyone using the file – in HostDeck or mRemoteNG – will be asked for it once. Share it only through a safe channel.",
            secret: true);
        if (first is null) return;
        if (TeamPasswordProblem(first) is { } problem) { _dialogs.ShowError("Library file password", problem); return; }
        var second = _dialogs.Prompt("Library file password", "Repeat the password:", secret: true);
        if (second is null) return;
        if (second != first) { _dialogs.ShowError("Library file password", "The passwords do not match."); return; }

        var oldPassword = _store.ReadDocument(lib.Doc(TeamPasswordDocument));
        if (await SyncLibraryAsync(lib, write: true, interactive: true, newPassword: first))
        {
            await ReencryptTeamSnippetsAsync(lib, string.IsNullOrEmpty(oldPassword) ? null : oldPassword, first);
            _dialogs.ShowInfo("Library file password", $"“{name}” is now protected with the new password.\n\nColleagues will be asked for it the next time they open or sync the file.");
        }
    }

    /// <summary>
    /// Passwort entfernen: Datei und Snippets ohne eigenes Passwort neu speichern (wie mRemoteNG
    /// „Password protect = No“). Danach gilt wieder der öffentlich bekannte mRemoteNG-Standardschlüssel.
    /// </summary>
    [RelayCommand]
    private async Task RemoveTeamPassword()
    {
        if (LibraryForCommand("Remove library password") is not { } lib) return;
        var name = NameOf(lib);
        if (lib.ReadOnly)
        {
            _dialogs.ShowInfo("Library", $"“{name}” is read-only here (e.g. jump host). Please change the password on your workstation.");
            return;
        }
        var oldPassword = _store.ReadDocument(lib.Doc(TeamPasswordDocument));
        if (string.IsNullOrEmpty(oldPassword))
        {
            _dialogs.ShowInfo("Library file password", $"“{name}” is not protected with its own password.");
            return;
        }
        if (!_dialogs.Confirm("Remove library file password",
                "Without its own password, the stored passwords in the file are only encrypted with mRemoteNG's publicly " +
                "known default key – anyone who can read the file can read them. Only the file/share permissions protect them then.\n\n" +
                $"Colleagues will no longer be asked for a password.\n\nRemove the password of “{name}”?"))
            return;

        if (await SyncLibraryAsync(lib, write: true, interactive: true, newPassword: ""))
        {
            await ReencryptTeamSnippetsAsync(lib, oldPassword, "");
            _dialogs.ShowInfo("Library file password", $"The password of “{name}” has been removed.");
        }
    }

    /// <summary>mRemoteNG/BouncyCastle verarbeitet nur Latin-1-Zeichen (1 Byte pro Zeichen).</summary>
    internal static string? TeamPasswordProblem(string password) =>
        password.Length < 10 ? "The password must be at least 10 characters long."
        : password.Any(c => c > 'ÿ') ? "The password may only contain Latin-1 characters (mRemoteNG limitation) – no emoji or special Unicode characters."
        : null;

    /// <summary>
    /// Bibliotheksordner ohne verbundene Datei (z. B. nach dem Wiederherstellen einer älteren Sicherung): Er wäre
    /// verwaist – nicht mehr synchronisiert und nicht mehr trennbar. Der Inhalt liegt in der Datei, daher entfernen.
    /// </summary>
    private void RemoveOrphanedTeamFolder()
    {
        var orphans = TeamContainerFolders().Where(f => LibraryOfContainer(f) is null).ToList();
        if (orphans.Count == 0) return;
        foreach (var orphan in orphans) SessionTree.Tree.Root.Remove(orphan);
        SessionTree.Load(SessionTree.Tree);
        Persist();
        AppLog.Info($"Removed {orphans.Count} orphaned library folder(s) (library not connected).");
        StatusText = "Library folder removed – that library is not connected. Connect it again via the settings menu (gear icon) if needed.";
    }

    // ---- Synchronisieren --------------------------------------------------------------------------

    /// <summary>Strg+S: bei allen Bibliotheken Änderungen anderer übernehmen und eigene speichern.</summary>
    [RelayCommand]
    private Task SyncTeam() => SyncTeamAsync(write: true, interactive: true);

    /// <summary>Alle Bibliotheken nacheinander (bzw. nur <paramref name="lib"/>) abgleichen. true = alle erfolgreich.</summary>
    public async Task<bool> SyncTeamAsync(bool write, bool interactive, string? newPassword = null, LibraryState? lib = null)
    {
        if (lib is not null || newPassword is not null)
            return (lib ?? Libraries.FirstOrDefault()) is { } one && await SyncLibraryAsync(one, write, interactive, newPassword);
        var libs = Libraries;
        if (libs.Count == 0) return false;
        var ok = true;
        foreach (var l in libs) ok &= await SyncLibraryAsync(l, write, interactive, null);
        return ok;
    }

    /// <summary>
    /// Kern der Synchronisation einer Bibliothek. <paramref name="write"/> = eigene Änderungen speichern (Strg+S), sonst nur
    /// Änderungen aus der Datei übernehmen (automatisch). Konflikte werden nur beim Speichern abgefragt.
    /// </summary>
    public async Task<bool> SyncLibraryAsync(LibraryState lib, bool write, bool interactive, string? newPassword = null)
    {
        if (!_loadSucceeded || lib.Busy || !Libraries.Contains(lib)) return false;
        if (ContainerOf(lib) is null)
        {
            // Bibliotheksordner wurde gelöscht ⇒ als „trennen“ verstehen, NIE als „alles in der Datei löschen“
            ForgetLibrary(lib);
            RefreshAllTeamStatus();
            NotifyLibrariesChanged();
            return false;
        }
        var name = NameOf(lib);
        if (lib.ReadOnly && write)
        {
            if (interactive && newPassword is not null) _dialogs.ShowInfo("Library", $"“{name}” is read-only here (e.g. jump host). Please make changes on your workstation.");
            write = false;
        }

        lib.Busy = true;
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var path = TeamLibraryFile.Resolve(lib.Path);
                var container = ContainerOf(lib);
                if (path is null || container is null) return false;
                if (!ConfirmResolvedFile(lib, path, interactive)) return false;
                var version = _treeVersion;
                var mine = container.Children.Select(c => TreeMerge.CopyWithIds(c, includePersonal: true)).ToList();
                var (baseline, _) = LoadTeamBase(lib);
                if (lib.ReadOnly) baseline = TreeMerge.Snapshot(mine); // lokale Abweichungen nie behalten

                TeamSnapshot remote;
                try
                {
                    var knownPassword = _store.ReadDocument(lib.Doc(TeamPasswordDocument));
                    var ask = interactive ? AskTeamPassword() : null; // im UI-Thread erzeugen
                    remote = await Io(() => TeamLibraryFile.Read(path, string.IsNullOrEmpty(knownPassword) ? null : knownPassword, ask));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    UpdateTeamStatus(lib, "file not reachable – offline copy");
                    if (interactive) _dialogs.ShowError(name, $"The library file is not reachable:\n{ex.Message}\n\nYour changes are kept locally.");
                    return false;
                }
                catch (ConfConsAuthenticationException ex)
                {
                    UpdateTeamStatus(lib, ex.Cancelled ? "password not entered" : "password required – Ctrl+S");
                    if (interactive && !ex.Cancelled) _dialogs.ShowError(name, ex.Message);
                    return false;
                }
                catch (ConfConsFormatException ex)
                {
                    UpdateTeamStatus(lib, "file damaged?");
                    AppLog.Error("Read library", ex);
                    if (interactive) _dialogs.ShowError(name, $"The library file could not be read:\n{ex.Message}");
                    return false;
                }
                if (remote.PasswordWasEntered && remote.Password is { } entered) _store.WriteDocument(lib.Doc(TeamPasswordDocument), entered);

                // Passwortschutz verschwunden (jemand ohne Passwort hat die Datei ersetzt oder es wurde entfernt)? Nie still
                // übernehmen – sonst würden alle Passwörter beim nächsten Speichern mit dem öffentlichen Standardschlüssel geschrieben.
                if (!remote.Info.CustomMasterPassword && newPassword is null && PasswordOf(lib) is not null)
                {
                    var accept = interactive && _dialogs.Confirm($"Password protection removed – {name}",
                        "The library file is no longer protected with its own password. Someone removed the password – or replaced " +
                        "the file with an unprotected version.\n\nPasswords stored in it are then readable by anyone with access to the file.\n\n" +
                        "“Yes”: accept the unprotected file (only if you know about this change)\n“No”: do not sync until this is clarified");
                    if (!accept)
                    {
                        UpdateTeamStatus(lib, "password protection removed – not synced");
                        AppLog.Info($"Library '{name}': file no longer password-protected – sync refused.");
                        return false;
                    }
                    _store.WriteDocument(lib.Doc(TeamPasswordDocument), "");
                }

                var merge = TreeMerge.Merge(baseline, mine, remote.Children);
                if (write && merge.Conflicts.Count > 0 && interactive)
                {
                    var list = string.Join("\n", merge.Conflicts.Take(15).Select(c => "• " + c.Path))
                               + (merge.Conflicts.Count > 15 ? $"\n… and {merge.Conflicts.Count - 15} more" : "");
                    var keepMine = _dialogs.Confirm($"Changed at the same time – {name}",
                        $"These entries were changed differently by you and by someone else:\n\n{list}\n\n" +
                        "“Yes”: save your version\n“No”: take the version from the library file");
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
                        // Neues Passwort: gleich etwas mehr KDF-Iterationen (mRemoteNG liest den Wert aus der Datei)
                        var format = newPassword is null ? remote.Info
                            : remote.Info with { KdfIterations = Math.Max(remote.Info.KdfIterations ?? 1000, TeamKdfIterations) };
                        var password = newPassword ?? remote.Password;
                        var merged = children;
                        (newHash, var written) = await Io(() => TeamLibraryFile.Write(path, merged, format, password, remote.Hash, c => BackupTeamFile(lib, c)));
                        children = TreeMerge.WithPersonalFrom(written, merged);
                        newBase = TreeMerge.Snapshot(children);
                        wrote = true;
                        if (newPassword is not null)
                        {
                            _store.WriteDocument(lib.Doc(TeamPasswordDocument), newPassword);
                            newPassword = null; // erledigt – bei einem erneuten Durchlauf nicht noch einmal erzwingen
                        }
                    }
                    catch (TeamFileChangedException)
                    {
                        continue; // jemand war schneller ⇒ neu einlesen und erneut zusammenführen
                    }
                    catch (TeamFileLockedException ex)
                    {
                        UpdateTeamStatus(lib, "locked right now – please press Ctrl+S again");
                        if (interactive) _dialogs.ShowInfo(name, ex.Message);
                        return false;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Error("Write library", ex);
                        UpdateTeamStatus(lib, "saving failed");
                        if (interactive) _dialogs.ShowError(name, $"Saving to the library file failed:\n{ex.Message}\n\nYour changes are kept locally.");
                        return false;
                    }
                }

                if (version != _treeVersion)
                {
                    // Während des Lesens/Schreibens lokal bearbeitet ⇒ nichts überschreiben, Basis aber fortschreiben
                    if (wrote) SaveTeamBase(lib, newBase, newHash);
                    if (attempt < 3) continue;
                    return false;
                }

                ApplyTeamChildren(lib, children);
                SaveTeamBase(lib, newBase, newHash);
                lib.Probe = TeamLibraryFile.Probe(path);
                await SyncTeamSnippetsAsync(lib);
                _ = RunTeamBackupAsync(lib);

                var info = new List<string>();
                if (merge.RemoteChanges > 0) info.Add($"{merge.RemoteChanges} change(s) pulled");
                if (wrote) info.Add($"{merge.LocalChanges} change(s) saved");
                UpdateTeamStatus(lib, info.Count > 0 ? string.Join(", ", info) : "up to date");
                if (interactive && merge.Notes.Count > 0) _dialogs.ShowInfo(name, string.Join("\n", merge.Notes.Take(20)));
                if (interactive || info.Count > 0) StatusText = $"{name}: " + (info.Count > 0 ? string.Join(", ", info) : "up to date");
                return true;
            }
            UpdateTeamStatus(lib, "many simultaneous changes – please press Ctrl+S again");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Library sync", ex);
            UpdateTeamStatus(lib, "sync error");
            if (interactive) _dialogs.ShowError(name, ex.Message);
            return false;
        }
        finally
        {
            lib.Busy = false;
        }
    }

    private const string ResolvedFileDocument = "team-resolved-file";

    /// <summary>
    /// Ordner statt Datei (Jumphost): Wechselt die „neueste .xml“ darin auf eine andere Datei, nicht still umschalten –
    /// jeder mit Schreibrecht auf den Ordner könnte sonst eine eigene Datei unterschieben.
    /// </summary>
    private bool ConfirmResolvedFile(LibraryState lib, string resolved, bool interactive)
    {
        if (lib.Path is not { } configured || !Directory.Exists(configured)) return true;
        var known = _store.ReadDocument(lib.Doc(ResolvedFileDocument));
        if (string.IsNullOrEmpty(known) || SamePath(known, resolved))
        {
            if (string.IsNullOrEmpty(known)) _store.WriteDocument(lib.Doc(ResolvedFileDocument), resolved);
            return true;
        }
        if (!interactive || !_dialogs.Confirm($"Other file – {NameOf(lib)}",
                $"The library folder now contains a newer file:\n\n{System.IO.Path.GetFileName(resolved)}\n(before: {System.IO.Path.GetFileName(known)})\n\n" +
                "Use the new file for this library? Only continue if you copied it there yourself."))
        {
            UpdateTeamStatus(lib, "newer file in the folder – press Ctrl+S to review");
            return false;
        }
        _store.WriteDocument(lib.Doc(ResolvedFileDocument), resolved);
        return true;
    }

    /// <summary>Nur für Tests/Aufrufer ohne UI-SynchronizationContext.</summary>
    public bool SyncTeam(bool write) => SyncTeamAsync(write, interactive: true).GetAwaiter().GetResult();

    private void ApplyTeamChildren(LibraryState lib, IReadOnlyList<TreeNode> children)
    {
        if (ContainerOf(lib) is not { } container) return;
        foreach (var old in container.Children.ToList()) container.Remove(old);
        foreach (var child in children) container.Add(child);

        var item = SessionTree.Items.FirstOrDefault(i => i.Node == container);
        if (item is not null) SessionTree.ReloadChildren(item);
        else SessionTree.Load(SessionTree.Tree);
        Persist();
        Reachability?.Invalidate();
    }

    // ---- Ziel-Bindung persönlicher Logins ----------------------------------------------------------
    //
    // Ein Mitglied mit Schreibrechten könnte den Host eines Eintrags auf einen eigenen Server ändern.
    // Wer dort sein persönliches Vault-Login (z. B. RADIUS) zugewiesen hat, würde es sonst beim nächsten
    // Verbinden automatisch dorthin senden. Daher merkt sich HostDeck je Eintrag das bestätigte Ziel
    // (Protokoll|Host|Port) und fragt nach, bevor ein persönliches Login an ein geändertes Ziel geht.

    internal const string TrustedTargetsDocument = "team-trusted-targets";

    /// <summary>
    /// Alles, wohin ein Login gehen kann: Protokoll, Host, Port und – bei RDP – das RD Gateway
    /// (es kann dieselben Zugangsdaten erhalten). Format: <c>protocol|host|port|gateway</c>.
    /// </summary>
    internal static string TargetKey(ConnectionNode connection)
    {
        var s = Core.Inheritance.SettingsResolver.Resolve(connection);
        var protocol = s.Protocol ?? ProtocolKind.Rdp;
        var gateway = "-";
        if (protocol == ProtocolKind.Rdp && (s.Rdp.Gateway.Usage ?? RdGatewayUsage.Never) != RdGatewayUsage.Never
            && !string.IsNullOrWhiteSpace(s.Rdp.Gateway.Hostname))
            gateway = s.Rdp.Gateway.Hostname.Trim().ToLowerInvariant();
        var key = $"{protocol}|{connection.Hostname.Trim().ToLowerInvariant()}|{s.Port ?? ProtocolDefaults.DefaultPort(protocol)}|{gateway}";
        // Abgeschwächter Schutz gehört zum Ziel: Ohne Server-Authentifizierung bzw. ohne NLA/CredSSP kann ein Angreifer im
        // Netz das Login abgreifen. Nur dann wird der Schlüssel länger – sichere Einträge behalten ihren bisherigen Schlüssel.
        if (protocol == ProtocolKind.Rdp)
        {
            var weak = new List<string>();
            if ((s.Rdp.AuthenticationLevel ?? RdpAuthenticationLevel.WarnOnFailedAuth) == RdpAuthenticationLevel.NoAuth) weak.Add("noauth");
            if (s.Rdp.UseCredSsp == false) weak.Add("nocredssp");
            if (weak.Count > 0) key += "|" + string.Join(',', weak);
        }
        return key;
    }

    /// <summary>Einträge aus 1.0.16 (ohne Gateway-Teil) gelten weiter, solange kein Gateway im Spiel ist.</summary>
    private static bool SameTarget(string known, string current) =>
        known == current || (known.Count(c => c == '|') == 2 && current == known + "|-");

    private static string ShowTarget(string key)
    {
        var parts = key.Split('|');
        if (parts.Length < 3) return key;
        var text = $"{parts[1]}:{parts[2]} ({parts[0]})";
        if (parts.Length > 3 && parts[3] != "-") text += $" via gateway {parts[3]}";
        if (parts.Length > 4)
        {
            if (parts[4].Contains("noauth")) text += " – SERVER AUTHENTICATION OFF";
            if (parts[4].Contains("nocredssp")) text += " – NLA/CredSSP OFF";
        }
        return text;
    }

    private Dictionary<Guid, string> LoadTrustedTargets()
    {
        try { return JsonSerializer.Deserialize<Dictionary<Guid, string>>(_store.ReadDocument(TrustedTargetsDocument) ?? "{}") ?? []; }
        catch (JsonException) { return []; }
    }

    private void SaveTrustedTargets(Dictionary<Guid, string> trusted) =>
        _store.WriteDocument(TrustedTargetsDocument, JsonSerializer.Serialize(trusted));

    private bool UsesPersonalLogin(ConnectionNode connection, CredentialVault vault) =>
        Core.Inheritance.CredentialResolver.Resolve(connection, vault).VaultEntry is not null;

    private bool InTeam(TreeNode node) => LibraryOf(node) is not null;

    /// <summary>Vor dem Verbinden: liefert false, wenn das persönliche Login NICHT gesendet werden soll.</summary>
    private bool ConfirmTeamTarget(ConnectionNode connection, CredentialVault vault)
    {
        if (!InTeam(connection) || !UsesPersonalLogin(connection, vault)) return true;
        var trusted = LoadTrustedTargets();
        var current = TargetKey(connection);
        var entry = Core.Inheritance.CredentialResolver.Resolve(connection, vault).VaultEntry!;
        if (trusted.TryGetValue(connection.Id, out var known) && SameTarget(known, current))
        {
            if (known != current) { trusted[connection.Id] = current; SaveTrustedTargets(trusted); } // Format aktualisieren
            return true;
        }

        // Kein stilles „beim ersten Mal vertrauen“: Ein Kollege könnte einen neuen Eintrag mit eigenem Ziel anlegen,
        // der das persönliche Login eines Ordners erbt. Daher jede erste Verwendung ausdrücklich bestätigen.
        var question = known is null
            ? $"First use of your personal login “{entry.Name}” for the library entry “{connection.Name}”:\n\n" +
              $"target: {ShowTarget(current)}\n\n" +
              "Library entries can be created or edited by colleagues. Only continue if this is the system you expect.\n\n" +
              "Send your login to this target?"
            : $"The target of “{connection.Name}” was changed in the library:\n\n" +
              $"before: {ShowTarget(known!)}\nnow:    {ShowTarget(current)}\n\n" +
              $"Your personal login “{entry.Name}” would be sent to the new target. " +
              "Only continue if you know about this change.\n\nSend your login to the new target?";
        if (!_dialogs.Confirm(known is null ? "Confirm target for your login" : "Library entry target changed", question))
        {
            StatusText = $"“{connection.Name}”: connecting without your personal login";
            return false;
        }
        trusted[connection.Id] = current;
        SaveTrustedTargets(trusted);
        return true;
    }

    /// <summary>Ziele aller Verbindungen des Knotens (vor dem Bearbeiten), nur für Bibliothekseinträge.</summary>
    private Dictionary<Guid, string> TeamTargetsOf(TreeNode node) =>
        !InTeam(node) ? [] : (node is FolderNode f ? f.Descendants() : [node]).OfType<ConnectionNode>().ToDictionary(c => c.Id, TargetKey);

    /// <summary>
    /// Nach dem Bearbeiten: Nur Ziele, die der Benutzer selbst geändert hat, gelten als bestätigt – und bei einem Ordner nur
    /// für Einträge, deren bisheriges Ziel schon bestätigt war. Unveränderte (evtl. von Kollegen geänderte) Ziele werden nie
    /// stillschweigend bestätigt: Wer z. B. einem Ordner sein RADIUS-Login zuweist, wird beim Verbinden weiterhin gefragt.
    /// </summary>
    private void TrustEditedTeamTargets(TreeNode node, Dictionary<Guid, string> before)
    {
        if (before.Count == 0 || !InTeam(node)) return;
        var trusted = LoadTrustedTargets();
        var changed = false;
        foreach (var c in (node is FolderNode f ? f.Descendants() : [node]).OfType<ConnectionNode>())
        {
            if (!before.TryGetValue(c.Id, out var old)) continue;
            var now = TargetKey(c);
            if (now == old) continue;                                   // nicht durch diese Bearbeitung geändert
            var wasTrusted = trusted.TryGetValue(c.Id, out var known) && SameTarget(known, old);
            // Der bearbeitete Eintrag selbst: neuer Host wurde vom Benutzer eingetippt ⇒ bestätigt. Nur Port o. Ä. geändert
            // an einem (evtl. fremd geänderten) unbestätigten Ziel ⇒ weiter nachfragen.
            var hostTyped = c == node && old.Split('|').ElementAtOrDefault(1) != now.Split('|').ElementAtOrDefault(1);
            if (wasTrusted || hostTyped)
            {
                trusted[c.Id] = now;
                changed = true;
            }
        }
        if (changed) SaveTrustedTargets(trusted);
    }

    // ---- Sicherungen neben der Datei (Regel liegt neben der Datei, gilt für alle, die sie nutzen) ----

    /// <summary>Tagessicherung der Datei, falls die Regel es vorsieht und heute noch keiner gesichert hat.</summary>
    public async Task RunTeamBackupAsync(LibraryState? lib = null)
    {
        if (lib is null)
        {
            foreach (var l in Libraries) await RunTeamBackupAsync(l);
            return;
        }
        if (lib.ReadOnly || TeamLibraryFile.Resolve(lib.Path) is not { } file) return;
        try
        {
            if (await Io(() => HostDeck.Presentation.Team.TeamBackup.RunIfDue(file, DateTime.Now)))
                AppLog.Info($"Library backup created in {HostDeck.Presentation.Team.TeamBackup.FolderFor(file)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TeamFileLockedException or ConfConsFormatException)
        {
            AppLog.Info($"Library backup skipped: {ex.Message}");
        }
    }

    // ---- Team-Snippets ----------------------------------------------------------------------------
    //
    // Liegen als eigene Datei neben der Bibliotheksdatei und werden sofort gespeichert (kein Strg+S nötig). Ein lokaler,
    // verschlüsselter Zwischenstand macht sie offline verfügbar; nicht schreibbare Änderungen werden gemerkt und
    // beim nächsten Abgleich nachgereicht.

    private const string TeamSnippetsCacheDocument = "team-snippets-cache";
    private const string TeamSnippetsPendingDocument = "team-snippets-pending";

    private List<TeamSnippetEntry> LoadSnippetDoc(string name)
    {
        try { return JsonSerializer.Deserialize<List<TeamSnippetEntry>>(_store.ReadDocument(name) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Snippets aller Bibliotheken anzeigen (jedes mit Herkunft, damit Änderungen in die richtige Datei gehen).</summary>
    private void ShowCachedTeamSnippets(SnippetsViewModel vm)
    {
        var libs = Libraries;
        var all = new List<Snippet>();
        foreach (var lib in libs)
        {
            var visible = TeamSnippetFile.Merge(LoadSnippetDoc(lib.Doc(TeamSnippetsCacheDocument)), LoadSnippetDoc(lib.Doc(TeamSnippetsPendingDocument)));
            foreach (var entry in visible.Where(e => e.DeletedUtc is null))
            {
                var snippet = entry.ToSnippet();
                snippet.LibraryId = lib.Id;
                all.Add(snippet);
            }
        }
        vm.IsReadOnlyTeamSnippet = s => libs.FirstOrDefault(l => l.Id == s.LibraryId) is { ReadOnly: true };
        vm.ConfirmTeamSnippet = ConfirmTeamSnippet;
        vm.SetTeamSnippets(all, available: libs.Count > 0, readOnly: libs.Count > 0 && libs.All(l => l.ReadOnly));
    }

    internal const string ConfirmedSnippetsDocument = "team-snippets-confirmed";

    /// <summary>
    /// Geteiltes Snippet ausführen? Beim ersten Mal und nach jeder Änderung des Befehls (durch wen auch immer) wird der
    /// Befehl gezeigt – ein Kollege mit Schreibzugriff könnte sonst „Disk usage“ durch etwas Schädliches ersetzen.
    /// </summary>
    private bool ConfirmTeamSnippet(Snippet snippet)
    {
        Dictionary<Guid, string> confirmed;
        try { confirmed = JsonSerializer.Deserialize<Dictionary<Guid, string>>(_store.ReadDocument(ConfirmedSnippetsDocument) ?? "{}") ?? []; }
        catch (JsonException) { confirmed = []; }
        var hash = SnippetHash(snippet.Command, snippet.Execute);
        if (confirmed.TryGetValue(snippet.Id, out var known) && known == hash) return true;
        if (snippet.LibraryId is null) return true; // gerade selbst angelegt, noch nicht abgeglichen
        var command = snippet.Command.Length > 600 ? snippet.Command[..600] + " …" : snippet.Command;
        if (!_dialogs.Confirm("Run shared snippet?",
                $"“{snippet.Name}” is shared through a library and is new or was changed since you last ran it.\n\n{command}\n\n" +
                (snippet.Execute ? "It is executed right away (Enter is sent). " : "") + "Run it?"))
            return false;
        confirmed[snippet.Id] = hash;
        _store.WriteDocument(ConfirmedSnippetsDocument, JsonSerializer.Serialize(confirmed));
        return true;
    }

    private static string SnippetHash(string command, bool execute) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(command + "\0" + execute)));

    private void MarkSnippetConfirmed(Guid id, string command, bool execute)
    {
        Dictionary<Guid, string> confirmed;
        try { confirmed = JsonSerializer.Deserialize<Dictionary<Guid, string>>(_store.ReadDocument(ConfirmedSnippetsDocument) ?? "{}") ?? []; }
        catch (JsonException) { confirmed = []; }
        confirmed[id] = SnippetHash(command, execute);
        _store.WriteDocument(ConfirmedSnippetsDocument, JsonSerializer.Serialize(confirmed));
    }

    private static string? SnippetPathOf(LibraryState lib) => TeamLibraryFile.Resolve(lib.Path) is { } file ? TeamSnippetFile.PathFor(file) : null;

    private string? PasswordOf(LibraryState lib) => _store.ReadDocument(lib.Doc(TeamPasswordDocument)) is { Length: > 0 } pw ? pw : null;

    /// <summary>Snippets aller (bzw. einer) Bibliothek einlesen (offene eigene Änderungen vorher nachreichen).</summary>
    public async Task SyncTeamSnippetsAsync(LibraryState? lib = null)
    {
        foreach (var l in lib is null ? Libraries : [lib])
        {
            if (SnippetPathOf(l) is not { } path) continue;
            // Abgleiche nacheinander: gleichzeitige Läufe würden sich sonst an der eigenen Sperrdatei abweisen
            await _snippetGate.WaitAsync();
            try { await SyncTeamSnippetsCoreAsync(l, path); }
            finally { _snippetGate.Release(); }
        }
        // Anzeige ebenfalls nacheinander aktualisieren (zwei gleichzeitige Abgleiche dürfen die Liste nicht doppelt füllen)
        await _snippetGate.WaitAsync();
        try { ShowCachedTeamSnippets(Snippets); }
        finally { _snippetGate.Release(); }
    }

    private readonly SemaphoreSlim _snippetGate = new(1, 1);

    private async Task SyncTeamSnippetsCoreAsync(LibraryState lib, string path)
    {
        var password = PasswordOf(lib);
        var pending = LoadSnippetDoc(lib.Doc(TeamSnippetsPendingDocument));
        try
        {
            List<TeamSnippetEntry> current;
            if (pending.Count > 0 && !lib.ReadOnly)
            {
                current = await Io(() => TeamSnippetFile.Apply(path, password, pending));
                _store.WriteDocument(lib.Doc(TeamSnippetsPendingDocument), "[]");
            }
            else
            {
                current = await Io(() => TeamSnippetFile.Read(path, password));
            }
            _store.WriteDocument(lib.Doc(TeamSnippetsCacheDocument), JsonSerializer.Serialize(current));
            lib.SnippetProbe = TeamLibraryFile.Probe(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TeamSnippetPasswordException
                                       or JsonException or TeamFileLockedException or ConfConsFormatException)
        {
            AppLog.Info($"Library snippets not synchronized: {ex.Message}");
        }
    }

    /// <summary>
    /// Neues, geändertes oder gelöschtes geteiltes Snippet sofort in die Datei seiner Bibliothek schreiben
    /// (neue Snippets: erste beschreibbare Bibliothek).
    /// </summary>
    private async Task SaveTeamSnippetAsync(TeamSnippetEntry entry, string? libraryId)
    {
        var libs = Libraries;
        var lib = libs.FirstOrDefault(l => l.Id == libraryId) ?? libs.FirstOrDefault(l => !l.ReadOnly);
        if (lib is null || lib.ReadOnly) return;
        if (entry.DeletedUtc is null) MarkSnippetConfirmed(entry.Id, entry.Command, entry.Execute); // eigener Befehl ⇒ keine Rückfrage
        var pending = TeamSnippetFile.Merge(LoadSnippetDoc(lib.Doc(TeamSnippetsPendingDocument)), [entry]);
        _store.WriteDocument(lib.Doc(TeamSnippetsPendingDocument), JsonSerializer.Serialize(pending));
        await SyncTeamSnippetsAsync(lib);
        if (LoadSnippetDoc(lib.Doc(TeamSnippetsPendingDocument)).Count > 0)
            StatusText = "Shared snippet saved locally – it will be written to the library at the next sync.";
    }

    /// <summary>Nach dem Setzen eines neuen Passworts auch die Snippets damit verschlüsseln.</summary>
    private async Task ReencryptTeamSnippetsAsync(LibraryState lib, string? oldPassword, string newPassword)
    {
        if (SnippetPathOf(lib) is not { } path || !File.Exists(path)) return;
        try
        {
            var items = await Io(() => TeamSnippetFile.Read(path, oldPassword));
            await Io(() => { TeamSnippetFile.Rewrite(path, newPassword, items); return true; });
            lib.SnippetProbe = TeamLibraryFile.Probe(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TeamSnippetPasswordException or JsonException)
        {
            AppLog.Error("Re-encrypt library snippets", ex);
        }
    }

    // ---- Automatisch nachladen --------------------------------------------------------------------

    /// <summary>Alle 15 s günstig prüfen (Zeitstempel/Größe), ob sich eine Datei geändert hat.</summary>
    private void StartTeamPolling()
    {
        _teamUi ??= SynchronizationContext.Current;
        if (_teamUi is null || !HasTeamLibrary) return; // Tests: kein UI-Kontext ⇒ manuell
        if (_teamPoll is not null) return;
        _teamPoll = new Timer(_ =>
        {
            foreach (var lib in Libraries)
            {
                try
                {
                    if (lib.Busy || TeamLibraryFile.Resolve(lib.Path) is not { } path) continue;
                    var probe = TeamLibraryFile.Probe(path);
                    var snippetProbe = TeamLibraryFile.Probe(TeamSnippetFile.PathFor(path));
                    if (probe is not null && probe != lib.Probe)
                        _teamUi.Post(_ => _ = SyncLibraryAsync(lib, write: false, interactive: false), null);
                    else if (snippetProbe != lib.SnippetProbe)
                        _teamUi.Post(_ => _ = SyncTeamSnippetsAsync(lib), null);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Library check", ex);
                }
            }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>Beim Start: Stand der Dateien übernehmen (eigene, noch nicht gespeicherte Änderungen bleiben).</summary>
    private async Task StartTeamLibraryAsync()
    {
        RemoveOrphanedTeamFolder();
        _teamUi = SynchronizationContext.Current;
        foreach (var lib in Libraries)
        {
            if (ContainerOf(lib) is null)
            {
                // Verbunden, aber kein Ordner (z. B. ältere Sicherung wiederhergestellt) ⇒ neu einlesen statt trennen
                await ConnectTeamLibraryAsync(lib.Path!, lib.ReadOnly, lib);
                continue;
            }
            RefreshTeamPending(lib);
            await SyncLibraryAsync(lib, write: false, interactive: false);
        }
        StartTeamPolling();
    }

    /// <summary>Beim Beenden: nicht gespeicherte Änderungen anbieten.</summary>
    private void OfferTeamSyncOnExit()
    {
        _teamPoll?.Dispose();
        _teamPoll = null;
        foreach (var lib in Libraries.Where(l => !l.ReadOnly))
        {
            RefreshTeamPending(lib);
            if (lib.Pending == 0) continue;
            if (_dialogs.Confirm(NameOf(lib),
                    $"{lib.Pending} change(s) to the library “{NameOf(lib)}” are not saved yet.\n\nSave them for everyone now?"))
            {
                // Beim Beenden synchron im UI-Thread (kein await auf den blockierten Dispatcher ⇒ kein Deadlock)
                _inlineIo = true;
                try { SyncLibraryAsync(lib, write: true, interactive: true).GetAwaiter().GetResult(); }
                finally { _inlineIo = false; }
            }
        }
    }

    // ---- Hilfen -----------------------------------------------------------------------------------

    private void RefreshTeamPending()
    {
        foreach (var lib in Libraries) RefreshTeamPending(lib);
        RefreshAllTeamStatus();
    }

    private void RefreshTeamPending(LibraryState lib)
    {
        if (ContainerOf(lib) is not { } container)
        {
            lib.Pending = 0;
        }
        else
        {
            var (baseline, _) = LoadTeamBase(lib);
            lib.Pending = lib.ReadOnly ? 0 : TreeMerge.CountChanges(container.Children, baseline);
        }
        RefreshAllTeamStatus();
    }

    private bool _inlineIo;

    /// <summary>Datei-I/O im Hintergrund – beim Beenden direkt (synchron), damit nichts auf den UI-Thread wartet.</summary>
    private Task<T> Io<T>(Func<T> work) => _inlineIo ? Task.FromResult(work()) : Task.Run(work);

    private void UpdateTeamStatus(LibraryState lib, string info)
    {
        lib.LastInfo = $"{info} ({DateTime.Now:HH:mm})";
        RefreshAllTeamStatus();
    }

    private void RefreshAllTeamStatus()
    {
        var libs = Libraries;
        TeamPendingChanges = libs.Sum(l => l.Pending);
        TeamStatus = string.Join("   ·   ", libs.Select(l =>
            $"{NameOf(l)}{(l.ReadOnly ? " (read-only)" : "")}: {l.LastInfo}{(l.Pending > 0 ? $" · {l.Pending} unsaved – Ctrl+S" : "")}"));
    }

    private (Dictionary<Guid, BaseEntry> Base, string? Hash) LoadTeamBase(LibraryState lib)
    {
        try
        {
            var json = _store.ReadDocument(lib.Doc(TeamBaseDocument));
            var map = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Dictionary<Guid, BaseEntry>>(json);
            return (map ?? [], _store.ReadDocument(lib.Doc(TeamHashDocument)));
        }
        catch (JsonException)
        {
            return ([], null);
        }
    }

    private void SaveTeamBase(LibraryState lib, Dictionary<Guid, BaseEntry> baseline, string hash)
    {
        _store.WriteDocument(lib.Doc(TeamBaseDocument), JsonSerializer.Serialize(baseline));
        _store.WriteDocument(lib.Doc(TeamHashDocument), hash);
        RefreshTeamPending(lib);
    }

    private Func<MasterPasswordRequest, string?> AskTeamPassword()
    {
        var ui = SynchronizationContext.Current;
        return r =>
        {
            string? answer = null;
            void Ask() => answer = _dialogs.AskMasterPassword($"Library ({r.SourceName})", r.PreviousAttemptFailed, r.Attempt, r.MaxAttempts);
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
            $"The library file contains {withPasswords} stored passwords but is not protected by its own master password.\n\n" +
            "mRemoteNG then uses a publicly known default key – anyone with read access to the file can extract the passwords.\n\n" +
            "Recommendation: protect the file with a password – in HostDeck: select the library folder, then gear menu → " +
            "“Set library password …” (in mRemoteNG: select the root node “Connections” → property “Password protect”). " +
            "Personal logins (e.g. RADIUS) belong in your own vault instead of the file.");
    }

    private static IEnumerable<TreeNode> Flatten(IEnumerable<TreeNode> nodes) =>
        nodes.SelectMany(n => n is FolderNode f ? f.Descendants().Prepend(n) : [n]);
}
