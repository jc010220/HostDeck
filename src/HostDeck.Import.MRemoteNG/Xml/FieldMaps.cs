using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using HostDeck.Core.Model;

namespace HostDeck.Import.MRemoteNG.Xml;

/// <summary>Kontext für das Lesen eines einzelnen Knotens.</summary>
internal sealed class MapContext(double confVersion, string nodePath, Action<string, string, Action<string?>> deferDecrypt, Action<string> warn)
{
    public double ConfVersion { get; } = confVersion;
    public string NodePath { get; } = nodePath;

    /// <summary>
    /// Registriert einen verschlüsselten Wert. Entschlüsselt wird gesammelt und parallel, nachdem alle
    /// Knoten gelesen wurden; <paramref name="apply"/> erhält den Klartext bzw. <c>null</c> bei Fehler.
    /// </summary>
    public void DeferDecrypt(string attribute, string value, Action<string?> apply) => deferDecrypt(attribute, value, apply);
    public void Warn(string message) => warn($"{NodePath}: {message}");
}

/// <summary>Kontext für das Schreiben (Export).</summary>
internal sealed class WriteContext(double targetVersion, Func<string, string> encrypt)
{
    public double TargetVersion { get; } = targetVersion;
    public string Encrypt(string plain) => encrypt(plain);
}

/// <summary>
/// Abbildung eines mRemoteNG-Attributpaars (Wert + <c>InheritXyz</c>) auf ein Feld in <see cref="ConnectionSettings"/>.
/// </summary>
internal sealed class FieldMap
{
    public required string Attribute { get; init; }
    public string? InheritAttribute { get; init; }
    public bool Encrypted { get; init; }

    /// <summary>Wert parsen und setzen.</summary>
    public required Action<ConnectionSettings, string, MapContext> Read { get; init; }

    /// <summary>Eigenen (nicht aufgelösten) Wert als mRemoteNG-String; <c>null</c> = geerbt.</summary>
    public required Func<ConnectionSettings, WriteContext, string?> Write { get; init; }

    /// <summary>Weitere Attributnamen, die dieses Mapping "verbraucht" (landen nicht in Extensions).</summary>
    public IReadOnlyList<string> AlsoConsumes { get; init; } = [];
}

internal static partial class FieldMaps
{
    public static IReadOnlyList<FieldMap> All { get; } = Build();

    /// <summary>Attribute, die strukturell bzw. separat behandelt werden.</summary>
    public static readonly IReadOnlySet<string> StructuralAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Name", "Id", "Type", "Expanded", "Hostname", "Inherit", "IsRootGroup",
    };

    /// <summary>Alle von typisierten Mappings verbrauchten Attributnamen.</summary>
    public static IReadOnlySet<string> MappedAttributes { get; } = All
        .SelectMany(m => new[] { m.Attribute, m.InheritAttribute }.Concat(m.AlsoConsumes))
        .Where(a => a is not null).Select(a => a!).ToHashSet(StringComparer.Ordinal);

    private static List<FieldMap> Build() =>
    [
        // ---- Allgemein --------------------------------------------------------------------------
        Str("Descr", "InheritDescription", s => s.Description),
        Custom("Icon", "InheritIcon",
            read: (s, v, _) => s.Icon = v.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? v[..^4] : v,
            write: (s, _) => s.Icon),
        Str("Panel", "InheritPanel", s => s.Panel),
        Str("Color", "InheritColor", s => s.Color),
        Str("TabColor", "InheritTabColor", s => s.TabColor),
        Str("EnvironmentTags", "InheritEnvironmentTags", s => s.EnvironmentTags),
        Bool("Favorite", "InheritFavorite", s => s.Favorite),
        Str("UserField", "InheritUserField", s => s.UserField),
        Str("MacAddress", "InheritMacAddress", s => s.MacAddress),
        Custom("Protocol", "InheritProtocol",
            read: (s, v, ctx) =>
            {
                if (ProtocolNames.TryGetValue(v, out var p)) s.Protocol = p;
                else ctx.Warn($"Unknown protocol '{v}' – the inherited/default protocol applies.");
            },
            write: (s, _) => s.Protocol is { } p ? ProtocolNames.First(kv => kv.Value == p).Key : null),
        Custom("Port", "InheritPort",
            read: (s, v, ctx) =>
            {
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0) s.Port = port;
                else if (v is not ("" or "0")) ctx.Warn($"Invalid port '{v}'.");
            },
            write: (s, _) => s.Port?.ToString(CultureInfo.InvariantCulture)),

        // ---- Zugangsdaten -----------------------------------------------------------------------
        Str("Username", "InheritUsername", s => s.Username),
        Str("Domain", "InheritDomain", s => s.Domain),
        Secret("Password", "InheritPassword", s => s.Password),

        // ---- Externe Tools ----------------------------------------------------------------------
        Str("PreExtApp", "InheritPreExtApp", s => s.PreExtApp),
        Str("PostExtApp", "InheritPostExtApp", s => s.PostExtApp),
        Str("ExtApp", "InheritExtApp", s => s.ExtApp),

        // ---- SSH / PuTTY ------------------------------------------------------------------------
        Str("PuttySession", "InheritPuttySession", s => s.Ssh.PuttySession),
        Str("OpeningCommand", "InheritOpeningCommand", s => s.Ssh.OpeningCommand),
        Str("SSHOptions", "InheritSSHOptions", s => s.Ssh.Options),
        Str("SSHTunnelConnectionName", "InheritSSHTunnelConnectionName", s => s.Ssh.TunnelConnectionName),

        // ---- RDP --------------------------------------------------------------------------------
        Bool("ConnectToConsole", "InheritUseConsoleSession", s => s.Rdp.UseConsoleSession),
        Bool("UseCredSsp", "InheritUseCredSsp", s => s.Rdp.UseCredSsp),
        Bool("UseRestrictedAdmin", "InheritUseRestrictedAdmin", s => s.Rdp.UseRestrictedAdmin),
        Bool("UseRCG", "InheritUseRCG", s => s.Rdp.UseRemoteCredentialGuard),
        Bool("UseRedirectionServerName", "InheritUseRedirectionServerName", s => s.Rdp.UseRedirectionServerName),
        Enum("RDPAuthenticationLevel", "InheritRDPAuthenticationLevel", s => s.Rdp.AuthenticationLevel),
        Str("LoadBalanceInfo", "InheritLoadBalanceInfo", s => s.Rdp.LoadBalanceInfo),
        Enum("RdpVersion", "InheritRdpVersion", s => s.Rdp.Version),
        Enum("Colors", "InheritColors", s => s.Rdp.ColorDepth),
        Custom("Resolution", "InheritResolution", ReadResolution, WriteResolution),
        Bool("AutomaticResize", "InheritAutomaticResize", s => s.Rdp.AutomaticResize),
        Bool("DisplayWallpaper", "InheritDisplayWallpaper", s => s.Rdp.DisplayWallpaper),
        Bool("DisplayThemes", "InheritDisplayThemes", s => s.Rdp.DisplayThemes),
        Bool("EnableFontSmoothing", "InheritEnableFontSmoothing", s => s.Rdp.EnableFontSmoothing),
        Bool("EnableDesktopComposition", "InheritEnableDesktopComposition", s => s.Rdp.EnableDesktopComposition),
        Bool("DisableFullWindowDrag", "InheritDisableFullWindowDrag", s => s.Rdp.DisableFullWindowDrag),
        Bool("DisableMenuAnimations", "InheritDisableMenuAnimations", s => s.Rdp.DisableMenuAnimations),
        Bool("DisableCursorShadow", "InheritDisableCursorShadow", s => s.Rdp.DisableCursorShadow),
        Bool("DisableCursorBlinking", "InheritDisableCursorBlinking", s => s.Rdp.DisableCursorBlinking),
        Bool("CacheBitmaps", "InheritCacheBitmaps", s => s.Rdp.CacheBitmaps),
        Custom("RedirectDiskDrives", "InheritRedirectDiskDrives", ReadDiskDrives, WriteDiskDrives),
        Str("RedirectDiskDrivesCustom", "InheritRedirectDiskDrivesCustom", s => s.Rdp.RedirectDiskDrivesCustom),
        Bool("RedirectPorts", "InheritRedirectPorts", s => s.Rdp.RedirectPorts),
        Bool("RedirectPrinters", "InheritRedirectPrinters", s => s.Rdp.RedirectPrinters),
        Bool("RedirectSmartCards", "InheritRedirectSmartCards", s => s.Rdp.RedirectSmartCards),
        Bool("RedirectClipboard", "InheritRedirectClipboard", s => s.Rdp.RedirectClipboard),
        Bool("RedirectKeys", "InheritRedirectKeys", s => s.Rdp.RedirectKeys),
        Bool("RedirectWebAuthn", "InheritRedirectWebAuthn", s => s.Rdp.RedirectWebAuthn),
        Enum("RedirectSound", "InheritRedirectSound", s => s.Rdp.RedirectSound),
        Enum("SoundQuality", "InheritSoundQuality", s => s.Rdp.SoundQuality),
        Bool("RedirectAudioCapture", "InheritRedirectAudioCapture", s => s.Rdp.RedirectAudioCapture),
        Str("StartProgram", null, s => s.Rdp.StartProgram),
        Str("StartProgramWorkDir", null, s => s.Rdp.StartProgramWorkDir),
        Int("RDPMinutesToIdleTimeout", "InheritRDPMinutesToIdleTimeout", s => s.Rdp.MinutesToIdleTimeout),
        Bool("RDPAlertIdleTimeout", "InheritRDPAlertIdleTimeout", s => s.Rdp.AlertIdleTimeout),
        Bool("UseVmId", "InheritUseVmId", s => s.Rdp.UseVmId),
        Str("VmId", "InheritVmId", s => s.Rdp.VmId),
        Bool("UseEnhancedMode", "InheritUseEnhancedMode", s => s.Rdp.UseEnhancedMode),

        // ---- RD Gateway -------------------------------------------------------------------------
        Enum("RDGatewayUsageMethod", "InheritRDGatewayUsageMethod", s => s.Rdp.Gateway.Usage),
        Str("RDGatewayHostname", "InheritRDGatewayHostname", s => s.Rdp.Gateway.Hostname),
        Enum("RDGatewayUseConnectionCredentials", "InheritRDGatewayUseConnectionCredentials", s => s.Rdp.Gateway.CredentialMode),
        Str("RDGatewayUsername", "InheritRDGatewayUsername", s => s.Rdp.Gateway.Username),
        Secret("RDGatewayPassword", "InheritRDGatewayPassword", s => s.Rdp.Gateway.Password),
        Str("RDGatewayDomain", "InheritRDGatewayDomain", s => s.Rdp.Gateway.Domain),
        Secret("RDGatewayAccessToken", "InheritRDGatewayAccessToken", s => s.Rdp.Gateway.AccessToken),

        // ---- VNC --------------------------------------------------------------------------------
        Enum("VNCCompression", "InheritVNCCompression", s => s.Vnc.Compression),
        Enum("VNCEncoding", "InheritVNCEncoding", s => s.Vnc.Encoding),
        Enum("VNCAuthMode", "InheritVNCAuthMode", s => s.Vnc.AuthMode),
        Enum("VNCProxyType", "InheritVNCProxyType", s => s.Vnc.ProxyType),
        Str("VNCProxyIP", "InheritVNCProxyIP", s => s.Vnc.ProxyHost),
        Int("VNCProxyPort", "InheritVNCProxyPort", s => s.Vnc.ProxyPort),
        Str("VNCProxyUsername", "InheritVNCProxyUsername", s => s.Vnc.ProxyUsername),
        Secret("VNCProxyPassword", "InheritVNCProxyPassword", s => s.Vnc.ProxyPassword),
        Enum("VNCColors", "InheritVNCColors", s => s.Vnc.Colors),
        Enum("VNCSmartSizeMode", "InheritVNCSmartSizeMode", s => s.Vnc.SmartSizeMode),
        Bool("VNCViewOnly", "InheritVNCViewOnly", s => s.Vnc.ViewOnly),
    ];

    /// <summary>mRemoteNG <c>ProtocolType</c> ⇄ HostDeck <see cref="ProtocolKind"/>.</summary>
    public static readonly IReadOnlyDictionary<string, ProtocolKind> ProtocolNames =
        new Dictionary<string, ProtocolKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["RDP"] = ProtocolKind.Rdp,
            ["VNC"] = ProtocolKind.Vnc,
            ["SSH1"] = ProtocolKind.Ssh1,
            ["SSH2"] = ProtocolKind.Ssh2,
            ["Telnet"] = ProtocolKind.Telnet,
            ["Rlogin"] = ProtocolKind.Rlogin,
            ["RAW"] = ProtocolKind.Raw,
            ["HTTP"] = ProtocolKind.Http,
            ["HTTPS"] = ProtocolKind.Https,
            ["ICA"] = ProtocolKind.Ica,
            ["PowerShell"] = ProtocolKind.PowerShell,
            ["ARD"] = ProtocolKind.Ard,
            ["Terminal"] = ProtocolKind.Terminal,
            ["WSL"] = ProtocolKind.Wsl,
            ["AnyDesk"] = ProtocolKind.AnyDesk,
            ["IntApp"] = ProtocolKind.ExternalApp,
        };

    // ---- Spezialfälle -----------------------------------------------------------------------------

    [GeneratedRegex(@"^Res(\d+)x(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FixedResolutionRegex();

    private static void ReadResolution(ConnectionSettings s, string v, MapContext ctx)
    {
        if (System.Enum.TryParse<RdpResolutionMode>(v, true, out var mode) && mode != RdpResolutionMode.Fixed)
        {
            s.Rdp.ResolutionMode = mode;
        }
        else if (FixedResolutionRegex().Match(v) is { Success: true } m) // ältere mRemoteNG-Versionen: "Res1920x1080"
        {
            s.Rdp.ResolutionMode = RdpResolutionMode.Fixed;
            s.Rdp.DesktopWidth = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            s.Rdp.DesktopHeight = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (v.Length > 0)
        {
            ctx.Warn($"Unknown RDP resolution '{v}'.");
        }
    }

    private static string? WriteResolution(ConnectionSettings s, WriteContext _) => s.Rdp.ResolutionMode switch
    {
        null => null,
        // Aktuelle mRemoteNG-Versionen kennen keine festen Auflösungen mehr.
        RdpResolutionMode.Fixed => nameof(RdpResolutionMode.FitToWindow),
        var m => m.ToString(),
    };

    private static void ReadDiskDrives(ConnectionSettings s, string v, MapContext ctx)
    {
        // Bis ConfVersion 2.7 boolesch ("True" ⇒ lokale Laufwerke), ab 2.8 Enum.
        if (bool.TryParse(v, out var b)) s.Rdp.RedirectDiskDrives = b ? RdpDiskDrives.Local : RdpDiskDrives.None;
        else if (System.Enum.TryParse<RdpDiskDrives>(v, true, out var d)) s.Rdp.RedirectDiskDrives = d;
        else ctx.Warn($"Unknown value for RedirectDiskDrives: '{v}'.");
    }

    private static string? WriteDiskDrives(ConnectionSettings s, WriteContext ctx) => s.Rdp.RedirectDiskDrives switch
    {
        null => null,
        var d when ctx.TargetVersion < 2.8 => d == RdpDiskDrives.None ? "false" : "true",
        var d => d.ToString(),
    };

    // ---- Builder ----------------------------------------------------------------------------------

    private static FieldMap Custom(string attr, string? inherit,
        Action<ConnectionSettings, string, MapContext> read, Func<ConnectionSettings, WriteContext, string?> write)
        => new() { Attribute = attr, InheritAttribute = inherit, Read = read, Write = write };

    private static FieldMap Str(string attr, string? inherit, Expression<Func<ConnectionSettings, string?>> property)
    {
        var (get, set) = Accessor.Create(property);
        return Custom(attr, inherit, (s, v, _) => set(s, v), (s, _) => get(s));
    }

    private static FieldMap Secret(string attr, string? inherit, Expression<Func<ConnectionSettings, string?>> property)
    {
        var (get, set) = Accessor.Create(property);
        return new FieldMap
        {
            Attribute = attr,
            InheritAttribute = inherit,
            Encrypted = true,
            // Fehlgeschlagene Entschlüsselung ⇒ explizit leer (nicht erben!), Warnung kommt aus dem Kontext.
            Read = (s, v, ctx) =>
            {
                if (v.Length == 0) set(s, "");
                else ctx.DeferDecrypt(attr, v, plain => set(s, plain ?? ""));
            },
            Write = (s, ctx) => get(s) is { } plain ? ctx.Encrypt(plain) : null,
        };
    }

    private static FieldMap Bool(string attr, string? inherit, Expression<Func<ConnectionSettings, bool?>> property)
    {
        var (get, set) = Accessor.Create(property);
        return Custom(attr, inherit,
            (s, v, ctx) =>
            {
                if (TryParseBool(v, out var b)) set(s, b);
                else if (v.Length > 0) ctx.Warn($"{attr}: '{v}' is not a boolean.");
            },
            (s, _) => get(s) is { } b ? (b ? "true" : "false") : null);
    }

    private static FieldMap Int(string attr, string? inherit, Expression<Func<ConnectionSettings, int?>> property)
    {
        var (get, set) = Accessor.Create(property);
        return Custom(attr, inherit,
            (s, v, ctx) =>
            {
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) set(s, i);
                else if (v.Length > 0) ctx.Warn($"{attr}: '{v}' is not a number.");
            },
            (s, _) => get(s)?.ToString(CultureInfo.InvariantCulture));
    }

    private static FieldMap Enum<TEnum>(string attr, string? inherit, Expression<Func<ConnectionSettings, TEnum?>> property)
        where TEnum : struct, System.Enum
    {
        var (get, set) = Accessor.Create(property);
        return Custom(attr, inherit,
            (s, v, ctx) =>
            {
                if (System.Enum.TryParse<TEnum>(v, true, out var e) && System.Enum.IsDefined(e)) set(s, e);
                else if (v.Length > 0) ctx.Warn($"{attr}: unknown value '{v}'.");
            },
            (s, _) => get(s)?.ToString());
    }

    internal static bool TryParseBool(string? value, out bool result)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes": result = true; return true;
            case "false" or "0" or "no": result = false; return true;
            default: result = false; return false;
        }
    }
}

/// <summary>Erzeugt kompilierte Getter/Setter aus Ausdrücken wie <c>s =&gt; s.Rdp.Gateway.Hostname</c>.</summary>
internal static class Accessor
{
    public static (Func<ConnectionSettings, T?> Get, Action<ConnectionSettings, T?> Set) Create<T>(Expression<Func<ConnectionSettings, T?>> expr)
    {
        if (expr.Body is not MemberExpression member)
            throw new ArgumentException("Only property chains are supported.", nameof(expr));

        var value = Expression.Parameter(typeof(T), "value");
        var assign = Expression.Assign(member, value);
        var setter = Expression.Lambda<Action<ConnectionSettings, T?>>(assign, expr.Parameters[0], value).Compile();
        return (expr.Compile(), setter);
    }
}
