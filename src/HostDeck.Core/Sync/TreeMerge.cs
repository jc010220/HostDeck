using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostDeck.Core.Model;

namespace HostDeck.Core.Sync;

/// <summary>Zustand eines Knotens zum Zeitpunkt der letzten Synchronisation (ohne Inhalt, nur Prüfsumme).</summary>
public sealed record BaseEntry(Guid? ParentId, string Fingerprint);

public enum MergeConflictResolution { KeepMine, TakeTheirs }

/// <summary>Beide Seiten haben denselben Eintrag verschieden geändert.</summary>
public sealed record MergeConflict(Guid Id, string Path, string Description);

public sealed class MergeResult
{
    /// <summary>Zusammengeführte Kinder (Kopien) – ersetzen den Inhalt des Team-Ordners.</summary>
    public required List<TreeNode> Children { get; init; }
    /// <summary>Neue Basis für die nächste Synchronisation.</summary>
    public required Dictionary<Guid, BaseEntry> NewBase { get; init; }
    public required IReadOnlyList<MergeConflict> Conflicts { get; init; }
    /// <summary>Lokale Änderungen, die noch nicht in der Datei stehen (0 ⇒ nichts zu schreiben).</summary>
    public required int LocalChanges { get; init; }
    /// <summary>Änderungen aus der Datei, die lokal übernommen wurden.</summary>
    public required int RemoteChanges { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Drei-Wege-Zusammenführung eines Team-Teilbaums (wie KeePass „Synchronisieren“):
/// <c>Basis</c> = Stand der letzten Synchronisation, <c>Mine</c> = lokaler Stand, <c>Theirs</c> = aktuelle Datei.
/// Pro Eintrag (Id) gilt: nur eine Seite geändert ⇒ diese gewinnt; beide gleich geändert ⇒ unkritisch;
/// beide verschieden ⇒ Konflikt (Auflösung wählbar). Gelöscht + auf der anderen Seite geändert ⇒ behalten
/// (nie still Daten verlieren). Persönliche Zuordnungen (<see cref="ConnectionSettings.CredentialId"/>,
/// Vault-Einträge) werden nie synchronisiert und bleiben lokal erhalten.
/// </summary>
public static class TreeMerge
{
    private static readonly JsonSerializerOptions FingerprintJson = new() { WriteIndented = false };

    /// <summary>Prüfsumme über alle synchronisierten Felder (ohne UI-Zustand und persönliche Zuordnungen).</summary>
    public static string Fingerprint(TreeNode node)
    {
        var settings = node.Settings.Clone();
        settings.CredentialId = null;
        var sb = new StringBuilder();
        sb.Append(node is FolderNode ? 'F' : 'C').Append('\u001f')
          .Append(node.Name).Append('\u001f')
          .Append(node is ConnectionNode c ? c.Hostname : "").Append('\u001f')
          .Append(JsonSerializer.Serialize(settings, FingerprintJson)).Append('\u001f');
        foreach (var (k, v) in node.Extensions.OrderBy(e => e.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v).Append('\u001e');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Basis aus einem Teilbaum (Kinder von <paramref name="container"/>) berechnen.</summary>
    public static Dictionary<Guid, BaseEntry> Snapshot(IEnumerable<TreeNode> topLevel)
    {
        var result = new Dictionary<Guid, BaseEntry>();
        foreach (var (node, parent) in Flatten(topLevel))
            result[node.Id] = new BaseEntry(parent, Fingerprint(node));
        return result;
    }

    /// <summary>Anzahl lokaler Änderungen gegenüber der Basis (für die Anzeige „N nicht synchronisiert“).</summary>
    public static int CountChanges(IEnumerable<TreeNode> mine, IReadOnlyDictionary<Guid, BaseEntry> baseline)
    {
        var current = Snapshot(mine);
        var changed = current.Count(kv => !baseline.TryGetValue(kv.Key, out var b) || b != kv.Value);
        changed += baseline.Keys.Count(id => !current.ContainsKey(id));
        return changed;
    }

    public static MergeResult Merge(
        IReadOnlyDictionary<Guid, BaseEntry> baseline,
        IEnumerable<TreeNode> mine,
        IEnumerable<TreeNode> theirs,
        MergeConflictResolution resolution = MergeConflictResolution.KeepMine)
    {
        var local = Index(mine);
        var remote = Index(theirs);
        var conflicts = new List<MergeConflict>();
        var notes = new List<string>();
        var chosen = new Dictionary<Guid, Pick>();
        int localChanges = 0, remoteChanges = 0;

        foreach (var id in local.Keys.Union(remote.Keys).Union(baseline.Keys))
        {
            local.TryGetValue(id, out var l);
            remote.TryGetValue(id, out var r);
            baseline.TryGetValue(id, out var b);
            var changedL = l is not null && (b is null || b.Fingerprint != l.Fingerprint || b.ParentId != l.ParentId);
            var changedR = r is not null && (b is null || b.Fingerprint != r.Fingerprint || b.ParentId != r.ParentId);

            if (l is not null && r is not null)
            {
                if (l.Fingerprint == r.Fingerprint && l.ParentId == r.ParentId) { chosen[id] = new(r, l); continue; }
                if (!changedL) { chosen[id] = new(r, l); remoteChanges++; continue; }
                if (!changedR) { chosen[id] = new(l, l); localChanges++; continue; }

                conflicts.Add(new MergeConflict(id, l.Path, "von dir und in der Datei unterschiedlich geändert"));
                if (resolution == MergeConflictResolution.KeepMine) { chosen[id] = new(l, l); localChanges++; }
                else { chosen[id] = new(r, l); remoteChanges++; }
            }
            else if (l is not null) // fehlt in der Datei
            {
                if (b is null) { chosen[id] = new(l, l); localChanges++; }                // lokal neu
                else if (changedL)
                {
                    chosen[id] = new(l, l); localChanges++;                                // gelöscht, aber lokal geändert ⇒ behalten
                    notes.Add($"„{l.Path}“ wurde in der Datei gelöscht, von dir aber geändert – bleibt erhalten.");
                }
                else remoteChanges++;                                                      // dort gelöscht ⇒ hier löschen
            }
            else if (r is not null) // fehlt lokal
            {
                if (b is null) { chosen[id] = new(r, null); remoteChanges++; }             // dort neu
                else if (changedR)
                {
                    chosen[id] = new(r, null); remoteChanges++;                            // lokal gelöscht, dort geändert ⇒ behalten
                    notes.Add($"„{r.Path}“ hast du gelöscht, in der Datei wurde es aber geändert – bleibt erhalten.");
                }
                else localChanges++;                                                       // lokal gelöscht ⇒ dort löschen
            }
        }

        // Elternordner müssen existieren und Ordner sein; Zyklen (gegenläufige Verschiebungen) auflösen.
        Guid? ParentOf(Guid id) => chosen[id].Source.ParentId;
        foreach (var id in chosen.Keys.ToList())
        {
            var parent = ParentOf(id);
            if (parent is { } p && (!chosen.TryGetValue(p, out var pp) || pp.Source.Node is not FolderNode))
            {
                // Ordner wurde gelöscht, Inhalt aber geändert ⇒ Ordner mit behalten, sonst nach oben
                var restore = local.GetValueOrDefault(p) ?? remote.GetValueOrDefault(p);
                if (restore is { Node: FolderNode } && !chosen.ContainsKey(p))
                {
                    RestoreChain(p);
                }
                else
                {
                    chosen[id] = chosen[id] with { Source = chosen[id].Source with { ParentId = null } };
                    notes.Add($"„{chosen[id].Source.Path}“ lag in einem gelöschten Ordner und wurde nach oben verschoben.");
                }
            }
        }

        void RestoreChain(Guid folderId)
        {
            for (Guid? cur = folderId; cur is { } c && !chosen.ContainsKey(c);)
            {
                var entry = local.GetValueOrDefault(c) ?? remote.GetValueOrDefault(c);
                if (entry is null) break;
                chosen[c] = new(entry, local.GetValueOrDefault(c));
                notes.Add($"Ordner „{entry.Path}“ blieb erhalten, weil darin noch Änderungen liegen.");
                cur = entry.ParentId;
            }
        }

        foreach (var id in chosen.Keys.ToList())
        {
            var seen = new HashSet<Guid> { id };
            for (var p = ParentOf(id); p is { } pid; p = ParentOf(pid))
            {
                if (!chosen.ContainsKey(pid)) break;
                if (!seen.Add(pid))
                {
                    // Zyklus ⇒ Stand aus der Datei gilt
                    var fallback = remote.GetValueOrDefault(id)?.ParentId;
                    chosen[id] = chosen[id] with { Source = chosen[id].Source with { ParentId = fallback is { } f && chosen.ContainsKey(f) ? f : null } };
                    break;
                }
            }
        }

        // Kopien aufbauen, Reihenfolge: Datei-Reihenfolge, lokal neue/verschobene hinter ihrem lokalen Vorgänger.
        var copies = chosen.ToDictionary(kv => kv.Key, kv => CopyNode(kv.Value));
        var topLevel = new List<TreeNode>();
        List<Guid> OrderedChildren(Guid? parent)
        {
            var list = remote.Values.Where(e => e.ParentId == parent && chosen.ContainsKey(e.Id) && ParentOf(e.Id) == parent)
                                    .OrderBy(e => e.Order).Select(e => e.Id).ToList();
            var localSiblings = local.Values.Where(e => e.ParentId == parent).OrderBy(e => e.Order).Select(e => e.Id).ToList();
            for (var i = 0; i < localSiblings.Count; i++)
            {
                var id = localSiblings[i];
                if (list.Contains(id) || !chosen.ContainsKey(id) || ParentOf(id) != parent) continue;
                var predecessor = localSiblings.Take(i).LastOrDefault(list.Contains);
                list.Insert(predecessor == Guid.Empty ? 0 : list.IndexOf(predecessor) + 1, id);
            }
            // alles Übrige (z. B. nach oben verschobene Waisen) hinten anhängen
            list.AddRange(chosen.Keys.Where(id => ParentOf(id) == parent && !list.Contains(id)));
            return list;
        }
        void Build(Guid? parent, Action<TreeNode> add)
        {
            foreach (var id in OrderedChildren(parent))
            {
                var node = copies[id];
                add(node);
                if (node is FolderNode folder) Build(id, child => folder.Add(child));
            }
        }
        Build(null, topLevel.Add);

        return new MergeResult
        {
            Children = topLevel,
            NewBase = remote.ToDictionary(kv => kv.Key, kv => new BaseEntry(kv.Value.ParentId, kv.Value.Fingerprint)),
            Conflicts = conflicts,
            LocalChanges = localChanges,
            RemoteChanges = remoteChanges,
            Notes = notes,
        };
    }

    /// <summary>Persönliches (Zugangsdaten-Zuordnung, Aufklapp-Zustand) per Id von <paramref name="source"/> übernehmen.</summary>
    public static List<TreeNode> WithPersonalFrom(IEnumerable<TreeNode> target, IEnumerable<TreeNode> source)
    {
        var byId = Index(source);
        var result = target.ToList();
        foreach (var (node, _) in Flatten(result))
        {
            if (!byId.TryGetValue(node.Id, out var s)) continue;
            node.Settings.CredentialId = s.Node.Settings.CredentialId;
            if (node is FolderNode f && s.Node is FolderNode sf) f.IsExpanded = sf.IsExpanded;
        }
        return result;
    }

    /// <summary>Tiefe Kopie mit gleichen Ids (für den Export eines Teilbaums in einen eigenen Baum).</summary>
    public static TreeNode CopyWithIds(TreeNode node, bool includePersonal)
    {
        var copy = CopyShallow(node);
        if (!includePersonal) copy.Settings.CredentialId = null;
        if (node is FolderNode f)
            foreach (var child in f.Children)
                ((FolderNode)copy).Add(CopyWithIds(child, includePersonal));
        return copy;
    }

    private static TreeNode CopyShallow(TreeNode node)
    {
        TreeNode copy = node switch
        {
            FolderNode f => new FolderNode(f.Name, f.Id) { IsExpanded = f.IsExpanded },
            ConnectionNode c => new ConnectionNode(c.Name, c.Id) { Hostname = c.Hostname },
            _ => throw new NotSupportedException(node.GetType().Name),
        };
        copy.Settings = node.Settings.Clone();
        foreach (var (k, v) in node.Extensions) copy.Extensions[k] = v;
        return copy;
    }

    private static TreeNode CopyNode(Pick pick)
    {
        var copy = CopyShallow(pick.Source.Node);
        // Persönliches bleibt lokal: Zugangsdaten-Zuordnung und Aufklapp-Zustand
        copy.Settings.CredentialId = pick.Local?.Node.Settings.CredentialId;
        if (copy is FolderNode f && pick.Local?.Node is FolderNode lf) f.IsExpanded = lf.IsExpanded;
        return copy;
    }

    private sealed record Entry(Guid Id, Guid? ParentId, int Order, string Fingerprint, TreeNode Node, string Path);
    private sealed record Pick(Entry Source, Entry? Local);

    private static Dictionary<Guid, Entry> Index(IEnumerable<TreeNode> topLevel)
    {
        var result = new Dictionary<Guid, Entry>();
        var order = 0;
        foreach (var (node, parent) in Flatten(topLevel))
            result.TryAdd(node.Id, new Entry(node.Id, parent, order++, Fingerprint(node), node, RelativePath(node, parent is null)));
        return result;
    }

    private static string RelativePath(TreeNode node, bool topLevel) =>
        topLevel ? node.Name : string.Join('/', node.Ancestors().TakeWhile(a => a.Parent is not null && !IsTeamContainer(a)).Reverse().Select(a => a.Name).Append(node.Name));

    private static bool IsTeamContainer(FolderNode folder) => folder.Extensions.ContainsKey(TeamContainerKey);

    /// <summary>Markiert den lokalen Ordner, der die Team-Bibliothek enthält.</summary>
    public const string TeamContainerKey = "hostdeck:team";

    private static IEnumerable<(TreeNode Node, Guid? Parent)> Flatten(IEnumerable<TreeNode> topLevel)
    {
        foreach (var node in topLevel)
        {
            yield return (node, null);
            if (node is FolderNode folder)
                foreach (var item in FlattenChildren(folder))
                    yield return item;
        }
    }

    private static IEnumerable<(TreeNode Node, Guid? Parent)> FlattenChildren(FolderNode folder)
    {
        foreach (var child in folder.Children)
        {
            yield return (child, folder.Id);
            if (child is FolderNode sub)
                foreach (var item in FlattenChildren(sub))
                    yield return item;
        }
    }
}
