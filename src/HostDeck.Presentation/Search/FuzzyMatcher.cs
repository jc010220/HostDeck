namespace HostDeck.Presentation.Search;

/// <summary>
/// Unscharfe Suche wie in VS Code / Sublime („Strg+P“): Die Zeichen der Eingabe müssen in Reihenfolge
/// vorkommen, aber nicht zusammenhängend. Bewertet werden Wortanfänge, zusammenhängende Treffer und
/// Präfixe – so findet „acw1“ den Server „acme-web-01“.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Score &gt; 0 bei Treffer, sonst 0. Höher = besser.</summary>
    public static int Score(string query, string text) => Match(query, text, out _);

    /// <summary>Wie <see cref="Score"/>, liefert zusätzlich die getroffenen Zeichenpositionen (für Hervorhebung).</summary>
    public static int Match(string query, string text, out int[] positions)
    {
        positions = [];
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text) || query.Length > text.Length) return 0;

        // Exakter Teilstring schlägt immer verstreute Treffer
        var substring = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (substring >= 0)
        {
            positions = Enumerable.Range(substring, query.Length).ToArray();
            var score = 1000 + query.Length * 20;
            if (substring == 0) score += 500;                            // Präfix
            else if (IsWordStart(text, substring)) score += 250;          // Wortanfang
            if (query.Length == text.Length) score += 1000;               // exakt
            return score - Math.Min(substring, 50);
        }

        // Verstreuter Treffer: gierig, aber Wortanfänge bevorzugen
        var result = new int[query.Length];
        var total = 0;
        var t = 0;
        var previous = -2;
        for (var q = 0; q < query.Length; q++)
        {
            var c = char.ToLowerInvariant(query[q]);
            var found = -1;
            // bevorzugt: nächster Wortanfang mit passendem Zeichen
            for (var i = t; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != c) continue;
                if (found < 0) found = i;
                if (i == previous + 1 || IsWordStart(text, i)) { found = i; break; }
            }
            if (found < 0) return 0;

            var charScore = 10;
            if (found == previous + 1) charScore += 15;                   // zusammenhängend
            if (IsWordStart(text, found)) charScore += 20;                // Wortanfang
            if (found == 0) charScore += 15;
            total += charScore - Math.Min(found - t, 10);                 // Lücken kosten
            result[q] = found;
            previous = found;
            t = found + 1;
        }
        positions = result;
        return Math.Max(total, 1);
    }

    private static bool IsWordStart(string text, int i)
    {
        if (i == 0) return true;
        var prev = text[i - 1];
        var cur = text[i];
        return !char.IsLetterOrDigit(prev)
               || (char.IsLower(prev) && char.IsUpper(cur))      // camelCase
               || (char.IsLetter(prev) && char.IsDigit(cur));    // web01 → "0"
    }
}
