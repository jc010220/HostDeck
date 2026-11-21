using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HostDeck.Presentation.Snippets;

/// <summary>Ein gespeicherter Befehl / ein Makro.</summary>
public sealed class Snippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Text, der ins Terminal geschrieben wird. Mehrzeilig erlaubt; Platzhalter {{Name}} werden abgefragt.</summary>
    public string Command { get; set; } = "";
    /// <summary>Enter automatisch senden.</summary>
    public bool Execute { get; set; } = true;
    public string Category { get; set; } = "";
    /// <summary>Gehört zur Team-Bibliothek (wird nicht in der persönlichen Liste gespeichert).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTeam { get; set; }
}

/// <summary>Speicherort für Snippets (in der App: verschlüsselte Datenbank).</summary>
public interface ISnippetStore
{
    string? Load();
    void Save(string json);
    /// <summary>Stand der mitgelieferten Vorlagen (für einmalige Aktualisierung bestehender Listen).</summary>
    int DefaultsVersion { get => 0; set { } }
}

internal sealed class DocumentSnippetStore(Services.IConnectionStore store) : ISnippetStore
{
    public string? Load() => store.ReadDocument("snippets");
    public void Save(string json) => store.WriteDocument("snippets", json);
    public int DefaultsVersion
    {
        get => int.TryParse(store.ReadDocument("snippets-defaults"), out var v) ? v : 0;
        set => store.WriteDocument("snippets-defaults", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Seitenleiste „Snippets“ (MobaXterm „Macros“): Klick sendet an das aktive Terminal bzw. mit Multi-Exec an alle.
/// Platzhalter wie <c>{{Dienst}}</c> werden vor dem Senden abgefragt.
/// </summary>
public sealed partial class SnippetsViewModel : ObservableObject
{
    private readonly ISnippetStore _store;
    private readonly Func<string, string, string?> _prompt;
    private readonly Action<string> _send;

    public SnippetsViewModel(ISnippetStore store, Func<string, string, string?> prompt, Action<string> send)
    {
        _store = store;
        _prompt = prompt;
        _send = send;
        Reload();
    }

    public ObservableCollection<Snippet> Items { get; } = [];
    [ObservableProperty] private Snippet? _selected;
    [ObservableProperty] private string _filter = "";

    public IEnumerable<Snippet> Visible => Filter.Length == 0 ? Items
        : Items.Where(s => Search.FuzzyMatcher.Score(Filter, s.Name) > 0 || s.Command.Contains(Filter, StringComparison.OrdinalIgnoreCase) || s.Category.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    partial void OnFilterChanged(string value) => OnPropertyChanged(nameof(Visible));

    public void Reload()
    {
        var team = Items.Where(s => s.IsTeam).ToList();
        Items.Clear();
        var json = _store.Load();
        var list = json is null ? DefaultSnippets() : JsonSerializer.Deserialize<List<Snippet>>(json) ?? [];
        if (_store.DefaultsVersion < CurrentDefaultsVersion)
        {
            list = UpgradeDefaults(list, fresh: json is null);
            _store.DefaultsVersion = CurrentDefaultsVersion;
            _store.Save(JsonSerializer.Serialize(list));
        }
        foreach (var s in list) Items.Add(s);
        foreach (var s in team) Items.Add(s);
        Sort();
    }

    /// <summary>Persönliche Snippets speichern; ist <paramref name="changed"/> ein Team-Snippet, geht es an die Team-Datei.</summary>
    public void Save(Snippet? changed = null)
    {
        _store.Save(JsonSerializer.Serialize(Items.Where(s => !s.IsTeam).ToList()));
        if (changed is { IsTeam: true }) TeamChanged?.Invoke(Team.TeamSnippetEntry.From(changed));
        Sort();
    }

    // ---- Team-Snippets ----------------------------------------------------------------------------

    /// <summary>Team-Bibliothek verbunden (Snippets können geteilt werden).</summary>
    [ObservableProperty] private bool _teamAvailable;
    /// <summary>Team-Bibliothek nur lesend (Jumphost) – Team-Snippets nicht änderbar.</summary>
    [ObservableProperty] private bool _teamReadOnly;

    /// <summary>Änderung/Löschung eines Team-Snippets (MainViewModel schreibt sie in die Team-Datei).</summary>
    public event Action<Team.TeamSnippetEntry>? TeamChanged;

    /// <summary>Team-Snippets durch den aktuellen Stand ersetzen (nach dem Lesen der Team-Datei).</summary>
    public void SetTeamSnippets(IEnumerable<Snippet> team, bool available, bool readOnly)
    {
        TeamAvailable = available;
        TeamReadOnly = readOnly;
        var selected = Selected?.Id;
        foreach (var s in Items.Where(s => s.IsTeam).ToList()) Items.Remove(s);
        foreach (var s in team) { s.IsTeam = true; Items.Add(s); }
        Sort();
        Selected = Items.FirstOrDefault(s => s.Id == selected);
    }

    public bool CanEdit(Snippet snippet) => !(snippet.IsTeam && TeamReadOnly);

    /// <summary>Zwischen „nur für mich“ und „Team“ verschieben.</summary>
    public void SetShared(Snippet snippet, bool team)
    {
        if (snippet.IsTeam == team) { Save(snippet); return; }
        if (team)
        {
            snippet.IsTeam = true;
            Save(snippet);                                         // aus der persönlichen Liste, in die Team-Datei
        }
        else
        {
            TeamChanged?.Invoke(Tombstone(snippet));               // aus dem Team entfernen …
            snippet.IsTeam = false;
            snippet.Id = Guid.NewGuid();                           // … und als eigenes, neues Snippet behalten
            Save();
        }
    }

    private static Team.TeamSnippetEntry Tombstone(Snippet s)
    {
        var e = Team.TeamSnippetEntry.From(s);
        e.DeletedUtc = DateTime.UtcNow;
        return e;
    }

    private void Sort()
    {
        var ordered = Items.OrderByDescending(s => s.IsTeam).ThenBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Items.IndexOf(ordered[i]);
            if (current != i) Items.Move(current, i);
        }
        OnPropertyChanged(nameof(Visible));
    }

    [RelayCommand]
    public void Run(Snippet? snippet)
    {
        if (snippet is null) return;
        var text = ResolvePlaceholders(snippet.Command);
        if (text is null) return; // abgebrochen
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (snippet.Execute && !text.EndsWith('\r')) text += "\r";
        _send(text);
    }

    /// <summary>{{Name}} ⇒ Eingabe abfragen. null = abgebrochen.</summary>
    internal string? ResolvePlaceholders(string command)
    {
        var result = command;
        foreach (var name in System.Text.RegularExpressions.Regex.Matches(command, @"\{\{([^{}]+)\}\}").Select(m => m.Groups[1].Value).Distinct())
        {
            var value = _prompt(name, $"Value for “{name}”:");
            if (value is null) return null;
            result = result.Replace("{{" + name + "}}", value);
        }
        return result;
    }

    public Snippet Add(string name, string command, string category = "", bool execute = true, bool team = false)
    {
        var s = new Snippet { Name = name, Command = command, Category = category, Execute = execute, IsTeam = team && TeamAvailable };
        Items.Add(s);
        Save(s);
        return s;
    }

    public void Remove(Snippet snippet)
    {
        Items.Remove(snippet);
        if (snippet.IsTeam) TeamChanged?.Invoke(Tombstone(snippet));
        Save();
    }

    private const int CurrentDefaultsVersion = 2;

    /// <summary>
    /// Vorlagen aktualisieren: alte Cisco-Vorlagen entfernen (nur wenn unverändert) und neue Linux-Vorlagen ergänzen,
    /// die es unter diesem Namen noch nicht gibt. Eigene oder geänderte Snippets bleiben unangetastet.
    /// </summary>
    private static List<Snippet> UpgradeDefaults(List<Snippet> list, bool fresh)
    {
        if (fresh) return DefaultSnippets();
        list.RemoveAll(s => (s.Name, s.Command) is ("Show interfaces (Cisco)", "show ip interface brief")
                                                 or ("Show running-config (Cisco)", "terminal length 0\rshow running-config"));
        var names = list.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // bisherige Linux-Vorlagen hatten die Kategorie „Linux“ ⇒ an die neue Einteilung angleichen, sofern unverändert
        var byName = DefaultSnippets().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var s in list.Where(s => s.Category == "Linux" && byName.TryGetValue(s.Name, out var d) && d.Command == s.Command))
            s.Category = byName[s.Name].Category;
        list.AddRange(DefaultSnippets().Where(d => !names.Contains(d.Name)));
        return list;
    }

    private static List<Snippet> DefaultSnippets() =>
    [
        // System
        new() { Category = "Linux · System", Name = "System load", Command = "uptime && free -h && df -h -x tmpfs -x devtmpfs" },
        new() { Category = "Linux · System", Name = "OS and kernel version", Command = "cat /etc/os-release && uname -r" },
        new() { Category = "Linux · System", Name = "Top processes (CPU)", Command = "ps aux --sort=-%cpu | head -15" },
        new() { Category = "Linux · System", Name = "Top processes (memory)", Command = "ps aux --sort=-%mem | head -15" },
        new() { Category = "Linux · System", Name = "Who is logged in", Command = "w" },
        new() { Category = "Linux · System", Name = "Last logins", Command = "last -n 20" },
        new() { Category = "Linux · System", Name = "Pending updates (Debian/Ubuntu)", Command = "apt list --upgradable 2>/dev/null" },
        new() { Category = "Linux · System", Name = "Pending updates (RHEL/Rocky)", Command = "dnf check-update" },
        // Dienste & Logs
        new() { Category = "Linux · Services", Name = "Failed services", Command = "systemctl --failed" },
        new() { Category = "Linux · Services", Name = "Service status", Command = "systemctl status {{Service}} --no-pager" },
        new() { Category = "Linux · Services", Name = "Restart service", Command = "sudo systemctl restart {{Service}} && systemctl status {{Service}} --no-pager", Execute = false },
        new() { Category = "Linux · Services", Name = "Follow log", Command = "journalctl -u {{Service}} -f" },
        new() { Category = "Linux · Services", Name = "Errors since boot", Command = "journalctl -p err -b --no-pager | tail -50" },
        new() { Category = "Linux · Services", Name = "Follow file", Command = "tail -f {{File}}" },
        // Speicherplatz
        new() { Category = "Linux · Disk", Name = "Disk usage", Command = "df -hT -x tmpfs -x devtmpfs" },
        new() { Category = "Linux · Disk", Name = "Largest directories", Command = "du -xh --max-depth=1 . 2>/dev/null | sort -rh | head -15" },
        new() { Category = "Linux · Disk", Name = "Largest files (> 100 MB)", Command = "find . -xdev -type f -size +100M -exec ls -lh {} + 2>/dev/null | sort -k5 -rh | head -20" },
        new() { Category = "Linux · Disk", Name = "Recently changed files", Command = "find {{Path}} -xdev -type f -mmin -60 2>/dev/null | head -50" },
        // Netzwerk
        new() { Category = "Linux · Network", Name = "Open ports", Command = "ss -tulpn" },
        new() { Category = "Linux · Network", Name = "IP addresses", Command = "ip -br addr" },
        new() { Category = "Linux · Network", Name = "Routes", Command = "ip route" },
        new() { Category = "Linux · Network", Name = "Ping", Command = "ping -c 4 {{Host}}" },
        new() { Category = "Linux · Network", Name = "Port reachable?", Command = "nc -zv -w 3 {{Host}} {{Port}}" },
        new() { Category = "Linux · Network", Name = "DNS lookup", Command = "dig +short {{Hostname}} || getent hosts {{Hostname}}" },
        new() { Category = "Linux · Network", Name = "TLS certificate dates", Command = "echo | openssl s_client -connect {{Host}}:443 -servername {{Host}} 2>/dev/null | openssl x509 -noout -subject -dates" },
    ];
}
