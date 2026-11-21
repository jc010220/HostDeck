using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Presentation.QuickConnect;

namespace HostDeck.Presentation.Search;

public enum PaletteItemKind { Connection, QuickConnect, Action }

/// <summary>Ein Eintrag der Schnellsuche.</summary>
public sealed class PaletteItem
{
    public required PaletteItemKind Kind { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Badge { get; init; } = "";
    public ProtocolKind Protocol { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsRecent { get; init; }
    public ConnectionNode? Connection { get; init; }
    public QuickConnectTarget? QuickConnect { get; init; }
    public Action? Action { get; init; }
    internal int Score { get; init; }
}

/// <summary>
/// Schnellsuche (Strg+K): unscharfe Suche über Name, Host, Ordnerpfad, Beschreibung, Tags und Benutzer.
/// Leere Eingabe ⇒ Favoriten + zuletzt genutzt. „&gt;“ als erstes Zeichen ⇒ Befehle.
/// Passt nichts, aber die Eingabe ist z. B. „ssh root@10.0.0.5“ ⇒ Quick-Connect-Eintrag.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private const int MaxResults = 50;
    private readonly Func<ConnectionTree> _tree;
    private readonly Func<IReadOnlyList<Guid>> _recent;
    private readonly Func<ProtocolKind> _defaultProtocol;
    private readonly Func<IEnumerable<(string Title, string Keywords, Action Run)>> _actions;

    public CommandPaletteViewModel(Func<ConnectionTree> tree, Func<IReadOnlyList<Guid>> recent, Func<ProtocolKind> defaultProtocol,
        IReadOnlyList<(string Title, string Keywords, Action Run)> actions)
        : this(tree, recent, defaultProtocol, () => actions) { }

    public CommandPaletteViewModel(Func<ConnectionTree> tree, Func<IReadOnlyList<Guid>> recent, Func<ProtocolKind> defaultProtocol,
        Func<IEnumerable<(string Title, string Keywords, Action Run)>> actions)
    {
        _tree = tree;
        _recent = recent;
        _defaultProtocol = defaultProtocol;
        _actions = actions;
    }

    public ObservableCollection<PaletteItem> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteItem? _selected;

    partial void OnQueryChanged(string value) => Refresh();

    public void Refresh()
    {
        Results.Clear();
        foreach (var item in Compute(Query.Trim())) Results.Add(item);
        Selected = Results.FirstOrDefault();
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        var index = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
    }

    internal IEnumerable<PaletteItem> Compute(string query)
    {
        if (query.StartsWith('>'))
            return Actions(query[1..].Trim());

        var tree = _tree();
        var recent = _recent();
        var recentRank = recent.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var items = new List<PaletteItem>();

        foreach (var c in tree.Connections)
        {
            var resolved = SettingsResolver.Resolve(c);
            var favorite = resolved.Favorite == true;
            var isRecent = recentRank.TryGetValue(c.Id, out var rank);
            int score;
            if (query.Length == 0)
            {
                if (!favorite && !isRecent) continue;
                score = (isRecent ? 1000 - rank : 0) + (favorite ? 500 : 0);
            }
            else
            {
                score = ScoreConnection(query, c, resolved);
                if (score == 0) continue;
                if (isRecent) score += 150 - Math.Min(rank * 5, 150); // kürzlich genutzt leicht bevorzugen
                if (favorite) score += 100;
            }

            items.Add(new PaletteItem
            {
                Kind = PaletteItemKind.Connection,
                Title = c.Name,
                Subtitle = $"{ParentPath(c)}{(c.Hostname.Length > 0 ? "  ·  " + c.Hostname : "")}",
                Badge = resolved.Protocol?.DisplayName().ToUpperInvariant() ?? "",
                Protocol = resolved.Protocol ?? ProtocolKind.Rdp,
                IsFavorite = favorite,
                IsRecent = isRecent,
                Connection = c,
                Score = score,
            });
        }

        var result = items.OrderByDescending(i => i.Score).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).Take(MaxResults).ToList();

        // Kein exakter Treffer, aber eine verständliche Adresse ⇒ Quick-Connect anbieten
        if (query.Length > 0 && !result.Any(r => r.Score >= 2000)
            && QuickConnectParser.TryParse(query, _defaultProtocol(), out var target) && LooksLikeAddress(query, target!))
        {
            var qc = new PaletteItem
            {
                Kind = PaletteItemKind.QuickConnect,
                Title = $"Connect: {target!.DisplayName}",
                Subtitle = $"{target.Protocol.DisplayName()} → {target.Host}:{target.EffectivePort} (quick connect, not saved)",
                Badge = target.Protocol.DisplayName().ToUpperInvariant(),
                Protocol = target.Protocol,
                QuickConnect = target,
            };
            if (result.Count == 0) result.Add(qc);
            else result.Insert(Math.Min(1, result.Count), qc);
        }
        return result;
    }

    /// <summary>Name zählt am meisten, dann Host, dann Pfad/Beschreibung/Tags/Benutzer.</summary>
    internal static int ScoreConnection(string query, ConnectionNode c, ConnectionSettings resolved)
    {
        // Mehrere Wörter: jedes muss irgendwo passen („acme web“)
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var total = 0;
        foreach (var term in terms)
        {
            var best = Math.Max(
                FuzzyMatcher.Score(term, c.Name) * 3,
                Math.Max(FuzzyMatcher.Score(term, c.Hostname) * 2,
                Math.Max(FuzzyMatcher.Score(term, ParentPath(c)),
                Math.Max(FuzzyMatcher.Score(term, resolved.Description ?? ""),
                Math.Max(FuzzyMatcher.Score(term, resolved.EnvironmentTags ?? ""),
                         FuzzyMatcher.Score(term, resolved.Username ?? ""))))));
            if (best == 0) return 0;
            total += best;
        }
        return total;
    }

    private static bool LooksLikeAddress(string query, QuickConnectTarget target) =>
        query.Contains(' ') || query.Contains('@') || query.Contains("://", StringComparison.Ordinal)
        || target.Host.Contains('.') || target.Host.Contains(':');

    private static string ParentPath(TreeNode node)
    {
        var path = node.Path;
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private IEnumerable<PaletteItem> Actions(string query) =>
        _actions()
            .Select(a => (a, score: query.Length == 0 ? 1 : Math.Max(FuzzyMatcher.Score(query, a.Title) * 2, FuzzyMatcher.Score(query, a.Keywords))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => new PaletteItem { Kind = PaletteItemKind.Action, Title = x.a.Title, Subtitle = "Command", Action = x.a.Run, Score = x.score });
}
