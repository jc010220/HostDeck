using System.Globalization;
using System.Text.RegularExpressions;
using HostDeck.Core.Model;

namespace HostDeck.Presentation.QuickConnect;

/// <summary>Ergebnis einer Quick-Connect-Eingabe.</summary>
public sealed record QuickConnectTarget(ProtocolKind Protocol, string Host, int? Port, string? Username, string? Domain)
{
    public int EffectivePort => Port ?? ProtocolDefaults.DefaultPort(Protocol);

    public string DisplayName => Username is { Length: > 0 } u ? $"{u}@{Host}" : Host;

    /// <summary>Normierte Form für den Verlauf – nur geprüfte Felder, niemals ein Passwort.</summary>
    public string HistoryText
    {
        get
        {
            var scheme = Protocol switch
            {
                ProtocolKind.Ssh2 or ProtocolKind.Ssh1 => "ssh", ProtocolKind.Rdp => "rdp", ProtocolKind.Telnet => "telnet",
                ProtocolKind.Vnc => "vnc", ProtocolKind.Http => "http", ProtocolKind.Https => "https",
                ProtocolKind.Rlogin => "rlogin", ProtocolKind.Raw => "raw", _ => Protocol.ToString().ToLowerInvariant(),
            };
            var user = Username is { Length: > 0 } u ? (Domain is { Length: > 0 } d ? $"{d}\\{u}@" : $"{u}@") : "";
            var host = Host.Contains(':') ? $"[{Host}]" : Host;
            return $"{scheme} {user}{host}" + (Port is { } p ? $":{p}" : "");
        }
    }

    /// <summary>Erzeugt eine temporäre (nicht gespeicherte) Verbindung.</summary>
    public ConnectionNode ToConnection()
    {
        var node = new ConnectionNode(DisplayName) { Hostname = Host };
        node.Settings.Protocol = Protocol;
        node.Settings.Port = Port;
        if (Username is not null) node.Settings.Username = Username;
        if (Domain is not null) node.Settings.Domain = Domain;
        return node;
    }
}

/// <summary>
/// Versteht MobaXterm-/Shell-artige Eingaben:
/// <list type="bullet">
/// <item><c>ssh admin@10.0.0.1</c>, <c>ssh -p 2222 admin@host</c>, <c>ssh -l admin host</c>, <c>admin@host:2222</c></item>
/// <item><c>rdp server01</c>, <c>mstsc /v:server01:3390</c>, <c>rdp://CORP\admin@server01</c></item>
/// <item><c>telnet switch01 2323</c>, <c>vnc host:5901</c>, <c>https://portal</c></item>
/// <item><c>server01</c> / <c>10.0.0.1</c> ⇒ Standardprotokoll</item>
/// </list>
/// </summary>
public static partial class QuickConnectParser
{
    private static readonly Dictionary<string, ProtocolKind> Schemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssh"] = ProtocolKind.Ssh2,
        ["sftp"] = ProtocolKind.Ssh2,
        ["rdp"] = ProtocolKind.Rdp,
        ["mstsc"] = ProtocolKind.Rdp,
        ["telnet"] = ProtocolKind.Telnet,
        ["vnc"] = ProtocolKind.Vnc,
        ["http"] = ProtocolKind.Http,
        ["https"] = ProtocolKind.Https,
        ["rlogin"] = ProtocolKind.Rlogin,
        ["raw"] = ProtocolKind.Raw,
    };

    [GeneratedRegex(@"^(?<scheme>[a-z]+)://(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    // [user@]host[:port] – host darf [IPv6] in eckigen Klammern sein
    [GeneratedRegex(@"^(?:(?<user>[^@\s]+)@)?(?<host>\[[0-9a-fA-F:.]+\]|[^\s:/@\[\]]+)(?::(?<port>\d{1,5}))?/?$")]
    private static partial Regex TargetRegex();

    /// <summary>Erkennt „user:passwort@host“ (auch als URL) – solche Eingaben werden abgelehnt und nicht gespeichert.</summary>
    public static bool LooksLikeInlinePassword(string? input) =>
        input is not null && System.Text.RegularExpressions.Regex.IsMatch(input, @"(^|[\s/])[^\s@/:]+:[^\s@/]*@");

    /// <summary>Bekannte Ports ⇒ Protokoll („server01:3389“ ist RDP, auch wenn der Standard SSH ist).</summary>
    public static ProtocolKind? ProtocolFromPort(int port) => port switch
    {
        3389 => ProtocolKind.Rdp,
        22 => ProtocolKind.Ssh2,
        23 => ProtocolKind.Telnet,
        >= 5900 and <= 5999 => ProtocolKind.Vnc,
        443 or 8443 => ProtocolKind.Https,
        80 or 8080 => ProtocolKind.Http,
        _ => null,
    };

    /// <param name="inferFromPort">Ohne ausdrückliches Protokoll anhand eines bekannten Ports wählen (Standard).</param>
    public static bool TryParse(string? input, ProtocolKind defaultProtocol, out QuickConnectTarget? target, bool inferFromPort = true)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();

        // 1) URL-Form: scheme://[user@]host[:port]
        if (UrlRegex().Match(text) is { Success: true } url && Schemes.TryGetValue(url.Groups["scheme"].Value, out var urlProtocol))
            return TryParseTarget(url.Groups["rest"].Value, urlProtocol, null, null, out target);

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var protocol = defaultProtocol;
        var index = 0;
        var explicitProtocol = false;
        if (Schemes.TryGetValue(tokens[0], out var commandProtocol))
        {
            protocol = commandProtocol;
            index = 1;
            explicitProtocol = true;
        }

        // 2) Optionen (ssh -p 2222 -l user, mstsc /v:host)
        int? port = null;
        string? user = null;
        string? hostToken = null;
        for (; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token is "-p" or "-P" && index + 1 < tokens.Length && TryPort(tokens[index + 1], out var p)) { port = p; index++; }
            else if (token == "-l" && index + 1 < tokens.Length) { user = tokens[++index]; }
            else if (token.StartsWith("/v:", StringComparison.OrdinalIgnoreCase)) { hostToken = token[3..]; }
            else if (token.StartsWith('-') || token.StartsWith('/')) { /* unbekannte Option ignorieren */ }
            else if (hostToken is null) { hostToken = token; }
            else if (port is null && TryPort(token, out var trailingPort)) { port = trailingPort; } // "telnet host 23"
            else return false;
        }

        if (hostToken is null || !TryParseTarget(hostToken, protocol, port, user, out target)) return false;
        if (!explicitProtocol && inferFromPort && target!.Port is { } knownPort && ProtocolFromPort(knownPort) is { } inferred)
            target = target with { Protocol = inferred };
        return true;
    }

    private static bool TryParseTarget(string text, ProtocolKind protocol, int? port, string? user, out QuickConnectTarget? target)
    {
        target = null;
        var m = TargetRegex().Match(text);
        if (!m.Success) return false;

        var host = m.Groups["host"].Value.Trim('[', ']');
        if (m.Groups["port"].Success)
        {
            if (!TryPort(m.Groups["port"].Value, out var p)) return false;
            port = p;
        }

        string? domain = null;
        if (m.Groups["user"].Success)
        {
            user = Uri.UnescapeDataString(m.Groups["user"].Value);
            // „user:passwort@host“ wird bewusst nicht unterstützt – das Passwort landete sonst im Verlauf (settings.json)
            if (user.Contains(':')) return false;
            // CORP\admin  bzw.  CORP;admin (URL-Schreibweise von rdp://)
            var sep = user.IndexOfAny(['\\', ';']);
            if (sep > 0)
            {
                domain = user[..sep];
                user = user[(sep + 1)..];
            }
        }

        target = new QuickConnectTarget(protocol, host, port, user, domain);
        return true;
    }

    private static bool TryPort(string s, out int port)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
}
