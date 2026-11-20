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
}

/// <summary>Speicherort für Snippets (in der App: verschlüsselte Datenbank).</summary>
public interface ISnippetStore
{
    string? Load();
    void Save(string json);
}

internal sealed class DocumentSnippetStore(Services.IConnectionStore store) : ISnippetStore
{
    public string? Load() => store.ReadDocument("snippets");
    public void Save(string json) => store.WriteDocument("snippets", json);
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
        Items.Clear();
        var json = _store.Load();
        var list = json is null ? DefaultSnippets() : JsonSerializer.Deserialize<List<Snippet>>(json) ?? [];
        foreach (var s in list.OrderBy(s => s.Category).ThenBy(s => s.Name)) Items.Add(s);
        OnPropertyChanged(nameof(Visible));
    }

    public void Save()
    {
        _store.Save(JsonSerializer.Serialize(Items.ToList()));
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
            var value = _prompt(name, $"Wert für „{name}“:");
            if (value is null) return null;
            result = result.Replace("{{" + name + "}}", value);
        }
        return result;
    }

    public Snippet Add(string name, string command, string category = "", bool execute = true)
    {
        var s = new Snippet { Name = name, Command = command, Category = category, Execute = execute };
        Items.Add(s);
        Save();
        return s;
    }

    public void Remove(Snippet snippet)
    {
        Items.Remove(snippet);
        Save();
    }

    private static List<Snippet> DefaultSnippets() =>
    [
        new() { Category = "Linux", Name = "Systemlast", Command = "uptime && free -h && df -h -x tmpfs -x devtmpfs" },
        new() { Category = "Linux", Name = "Fehlgeschlagene Dienste", Command = "systemctl --failed" },
        new() { Category = "Linux", Name = "Dienst-Status", Command = "systemctl status {{Dienst}} --no-pager" },
        new() { Category = "Linux", Name = "Log verfolgen", Command = "journalctl -u {{Dienst}} -f" },
        new() { Category = "Linux", Name = "Offene Ports", Command = "ss -tulpn" },
        new() { Category = "Linux", Name = "Größte Verzeichnisse", Command = "du -xh --max-depth=1 . 2>/dev/null | sort -rh | head -15" },
        new() { Category = "Netzwerk", Name = "Show interfaces (Cisco)", Command = "show ip interface brief" },
        new() { Category = "Netzwerk", Name = "Show running-config (Cisco)", Command = "terminal length 0\rshow running-config" },
    ];
}
