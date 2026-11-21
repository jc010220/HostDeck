using System.Text;
using System.Text.RegularExpressions;

namespace HostDeck.Protocols.Ssh;

/// <summary>
/// Erkennt OSC-7-Sequenzen (<c>ESC ] 7 ; file://host/pfad BEL|ESC \</c>), mit denen die Shell ihr
/// aktuelles Verzeichnis meldet. Robust gegen Sequenzen, die über mehrere Datenblöcke verteilt sind.
/// Die Daten selbst werden nicht verändert (xterm.js ignoriert OSC 7).
/// </summary>
public sealed class OscWorkingDirectoryParser
{
    private const int MaxSequenceLength = 4096;
    private readonly StringBuilder _pending = new();
    private bool _inOsc;

    /// <summary>Liefert alle in diesem Block abgeschlossenen Verzeichnisse (meist 0 oder 1).</summary>
    public IReadOnlyList<string> Feed(string chunk)
    {
        List<string>? found = null;
        for (var i = 0; i < chunk.Length; i++)
        {
            var c = chunk[i];
            if (!_inOsc)
            {
                if (c == '\u001b' && (i + 1 < chunk.Length ? chunk[i + 1] == ']' : true))
                {
                    _inOsc = true;
                    _pending.Clear();
                    _pending.Append(c);
                }
                continue;
            }

            _pending.Append(c);
            var terminated = c == '\a' || (c == '\\' && _pending.Length >= 2 && _pending[^2] == '\u001b');
            if (terminated)
            {
                if (TryExtract(_pending.ToString(), out var path)) (found ??= []).Add(path);
                _inOsc = false;
                _pending.Clear();
            }
            else if (_pending.Length > MaxSequenceLength || (_pending.Length == 2 && c != ']'))
            {
                _inOsc = false; // kein OSC bzw. Müll
                _pending.Clear();
            }
        }
        return (IReadOnlyList<string>?)found ?? [];
    }

    internal static bool TryExtract(string sequence, out string path)
    {
        path = "";
        // ESC ] 7 ; <uri> (BEL | ESC \)
        var body = sequence.TrimStart('\u001b').TrimStart(']');
        if (!body.StartsWith("7;", StringComparison.Ordinal)) return false;
        body = body[2..].TrimEnd('\a').TrimEnd('\\').TrimEnd('\u001b');
        if (!body.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return false;
        var afterScheme = body["file://".Length..];
        var slash = afterScheme.IndexOf('/');
        if (slash < 0) return false;
        path = Uri.UnescapeDataString(afterScheme[slash..]);
        return path.Length > 0;
    }
}

/// <summary>Farbregel für das Terminal-Highlighting.</summary>
public sealed record HighlightRule(string Name, Regex Pattern, string SgrColor);

/// <summary>
/// MobaXterm-artiges Syntax-Highlighting: färbt Fehler, Warnungen, Erfolgsmeldungen und IP-Adressen –
/// aber <b>nur</b> Text, den das Programm selbst nicht bereits eingefärbt hat (SGR-Zustand wird
/// verfolgt). Escape-Sequenzen werden nie zerteilt, auch nicht über Blockgrenzen hinweg.
/// </summary>
public sealed class TerminalHighlighter
{
    public static IReadOnlyList<HighlightRule> DefaultRules { get; } =
    [
        new("Error", new Regex(@"\b(?:error|errors|failed|failure|fatal|denied|refused|critical|panic|exception|fehler|fehlgeschlagen)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "91"),        // hellrot
        new("Warning", new Regex(@"\b(?:warn|warning|warnings|deprecated|warnung)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "93"),        // gelb
        new("Success", new Regex(@"\b(?:ok|success|successful|succeeded|done|passed|active \(running\)|erfolgreich)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), "92"),        // grün
        new("IP", new Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:/\d{1,2})?\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant), "96"),                                  // cyan
    ];

    private readonly IReadOnlyList<HighlightRule> _rules;
    private readonly StringBuilder _carry = new(); // unvollständige Escape-Sequenz vom letzten Block
    private bool _foregroundIsDefault = true;
    private bool _alternateScreen;                // Vollbild-Programme (vim, htop) nicht anfassen

    public TerminalHighlighter(IReadOnlyList<HighlightRule>? rules = null) => _rules = rules ?? DefaultRules;

    public bool Enabled { get; set; } = true;

    public string Process(string chunk)
    {
        var input = _carry.Length > 0 ? _carry + chunk : chunk;
        _carry.Clear();
        var output = new StringBuilder(input.Length + 32);
        var textStart = 0;
        var i = 0;

        while (i < input.Length)
        {
            if (input[i] != '\u001b') { i++; continue; }

            Flush(input, textStart, i, output);
            var end = FindSequenceEnd(input, i);
            if (end < 0)
            {
                _carry.Append(input, i, input.Length - i); // Rest kommt im nächsten Block
                return output.ToString();
            }
            var seq = input.AsSpan(i, end - i + 1);
            TrackState(seq);
            output.Append(seq);
            i = end + 1;
            textStart = i;
        }

        Flush(input, textStart, input.Length, output);
        return output.ToString();
    }

    private void Flush(string input, int start, int end, StringBuilder output)
    {
        if (end <= start) return;
        var text = input.AsSpan(start, end - start);
        if (!Enabled || !_foregroundIsDefault || _alternateScreen) { output.Append(text); return; }

        var s = text.ToString();
        var matches = _rules
            .SelectMany(rule => rule.Pattern.Matches(s).Select(m => (m.Index, m.Length, rule.SgrColor)))
            .OrderBy(m => m.Index).ThenByDescending(m => m.Length)
            .ToList();
        var pos = 0;
        foreach (var (index, length, color) in matches)
        {
            if (index < pos) continue; // Überlappung: erster/längster Treffer gewinnt
            output.Append(s, pos, index - pos);
            output.Append("\u001b[").Append(color).Append('m').Append(s, index, length).Append("\u001b[39m");
            pos = index + length;
        }
        output.Append(s, pos, s.Length - pos);
    }

    /// <summary>Ende einer CSI/OSC/sonstigen Escape-Sequenz; -1 wenn unvollständig.</summary>
    private static int FindSequenceEnd(string s, int start)
    {
        if (start + 1 >= s.Length) return -1;
        var kind = s[start + 1];
        switch (kind)
        {
            case '[': // CSI: Parameter … Endbyte 0x40–0x7E
                for (var j = start + 2; j < s.Length; j++)
                    if (s[j] is >= '@' and <= '~') return j;
                return -1;
            case ']': // OSC: bis BEL oder ESC \
            case 'P': // DCS
            case '_':
            case '^':
                for (var j = start + 2; j < s.Length; j++)
                {
                    if (s[j] == '\a') return j;
                    if (s[j] == '\u001b' && j + 1 < s.Length && s[j + 1] == '\\') return j + 1;
                }
                return -1;
            case '(' or ')' or '*' or '+' or '#' or '%': // Zeichensatz: ESC ( B
                return start + 2 < s.Length ? start + 2 : -1;
            default:
                return start + 1; // ESC 7, ESC M, …
        }
    }

    private void TrackState(ReadOnlySpan<char> seq)
    {
        if (seq.Length < 3 || seq[1] != '[') return;
        var final = seq[^1];
        var parameters = seq[2..^1].ToString();

        if (final is 'h' or 'l' && parameters is "?1049" or "?47" or "?1047")
        {
            _alternateScreen = final == 'h';
            return;
        }
        if (final != 'm') return;

        // SGR: Vordergrundfarbe verfolgen
        var parts = parameters.Length == 0 ? ["0"] : parameters.Split(';');
        for (var k = 0; k < parts.Length; k++)
        {
            if (!int.TryParse(parts[k], out var p)) continue;
            if (p is 0 or 39) _foregroundIsDefault = true;
            else if (p is >= 30 and <= 37 or >= 90 and <= 97) _foregroundIsDefault = false;
            else if (p == 38) { _foregroundIsDefault = false; k += k + 1 < parts.Length && parts[k + 1] == "5" ? 2 : 4; }
            else if (p is 48) { k += k + 1 < parts.Length && parts[k + 1] == "5" ? 2 : 4; } // Hintergrund überspringen
        }
    }
}
