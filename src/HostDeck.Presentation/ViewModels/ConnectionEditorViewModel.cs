using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;

namespace HostDeck.Presentation.ViewModels;

/// <summary>Auswahl in einer Liste; <c>Value == null</c> bedeutet „erben“.</summary>
public sealed record Option<T>(T? Value, string Label) where T : struct
{
    public override string ToString() => Label;
}

public sealed record CredentialOption(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Eigenschaften eines Ordners oder einer Verbindung bearbeiten. Leeres Feld / „(geerbt …)“ / dritter
/// Checkbox-Zustand = vom Elternordner erben. Änderungen werden erst mit <see cref="Apply"/> übernommen.
/// </summary>
public sealed partial class ConnectionEditorViewModel : ObservableObject
{
    private readonly TreeNode _node;
    private readonly ConnectionSettings _inherited; // effektive Werte des Elternknotens

    public ConnectionEditorViewModel(TreeNode node, CredentialVault vault)
    {
        _node = node;
        Vault = vault;
        _inherited = node.Parent is { } parent ? SettingsResolver.Resolve(parent) : SettingsResolver.Resolve(new FolderNode("_"));
        InheritedFrom = node.Parent is { Parent: not null } p ? p.Name : "Standard";

        var s = node.Settings;
        _name = node.Name;
        _hostname = (node as ConnectionNode)?.Hostname ?? "";
        _protocol = Find(ProtocolOptions = Options(_inherited.Protocol), s.Protocol);
        _port = s.Port?.ToString(CultureInfo.InvariantCulture) ?? "";
        _description = s.Description ?? "";
        _tags = s.EnvironmentTags ?? "";
        _favorite = s.Favorite;

        CredentialOptions = [new CredentialOption(null, "(keiner – Benutzer/Passwort unten bzw. geerbt)"),
            .. vault.Items.OrderBy(c => c.Name).Select(c => new CredentialOption(c.Id, $"{c.Name}  ({c.DisplayUser})"))];
        _credential = CredentialOptions.FirstOrDefault(o => o.Id == s.CredentialId) ?? CredentialOptions[0];
        _username = s.Username ?? "";
        _domain = s.Domain ?? "";
        HasOwnPassword = s.Password is { Length: > 0 };
        _inheritPassword = s.Password is null;

        var r = s.Rdp;
        _resolution = Find(ResolutionOptions = Options(_inherited.Rdp.ResolutionMode), r.ResolutionMode);
        _colorDepth = Find(ColorOptions = Options(_inherited.Rdp.ColorDepth), r.ColorDepth);
        _authLevel = Find(AuthLevelOptions = Options(_inherited.Rdp.AuthenticationLevel), r.AuthenticationLevel);
        _sound = Find(SoundOptions = Options(_inherited.Rdp.RedirectSound), r.RedirectSound);
        _drives = Find(DriveOptions = Options(_inherited.Rdp.RedirectDiskDrives), r.RedirectDiskDrives);
        _gatewayUsage = Find(GatewayUsageOptions = Options(_inherited.Rdp.Gateway.Usage), r.Gateway.Usage);
        _gatewayHost = r.Gateway.Hostname ?? "";
        _useCredSsp = r.UseCredSsp;
        _redirectClipboard = r.RedirectClipboard;
        _redirectPrinters = r.RedirectPrinters;
        _redirectKeys = r.RedirectKeys;
        _consoleSession = r.UseConsoleSession;
        _restrictedAdmin = r.UseRestrictedAdmin;
    }

    public CredentialVault Vault { get; }
    public bool IsConnection => _node is ConnectionNode;
    public bool IsFolder => _node is FolderNode;
    public string InheritedFrom { get; }
    public string Title => IsFolder ? $"Ordner „{_node.Name}“ – Standardwerte für alle Einträge darin" : $"Verbindung „{_node.Name}“";

    // ---- Allgemein --------------------------------------------------------------------------------
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _hostname;
    [ObservableProperty] private Option<ProtocolKind> _protocol;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private bool? _favorite;

    public IReadOnlyList<Option<ProtocolKind>> ProtocolOptions { get; }
    public string PortPlaceholder => _inherited.Port is { } p && IsInheritedProtocolPort ? $"geerbt: {p}" : $"Standard: {ProtocolDefaults.DefaultPort(Protocol.Value ?? _inherited.Protocol ?? ProtocolKind.Rdp)}";
    private bool IsInheritedProtocolPort => _node.Parent is not null && SettingsResolver.FindOrigin(_node.Parent, x => x.Port) is not null;
    public string DescriptionPlaceholder => Hint(_inherited.Description);
    public string TagsPlaceholder => Hint(_inherited.EnvironmentTags);

    partial void OnProtocolChanged(Option<ProtocolKind> value) => OnPropertyChanged(nameof(PortPlaceholder));

    // ---- Anmeldung --------------------------------------------------------------------------------
    public IReadOnlyList<CredentialOption> CredentialOptions { get; }
    [ObservableProperty] private CredentialOption _credential;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _domain;
    /// <summary>Neues Passwort (aus der PasswordBox); <c>null</c> = unverändert.</summary>
    public string? NewPassword { get; set; }
    public bool HasOwnPassword { get; }
    [ObservableProperty] private bool _inheritPassword;

    public string UsernamePlaceholder => Hint(InheritedCredentials.Username);
    public string DomainPlaceholder => Hint(InheritedCredentials.Domain);
    public string PasswordHint => HasOwnPassword ? "Ein eigenes Passwort ist gespeichert – leer lassen, um es zu behalten."
        : InheritPassword && InheritedCredentials.Password.Length > 0 ? $"Passwort wird von „{InheritedFrom}“ geerbt." : "Kein Passwort gespeichert.";
    private ResolvedCredentials InheritedCredentials => _node.Parent is { } parent ? CredentialResolver.Resolve(parent, Vault)
        : new ResolvedCredentials("", "", "", null, null);

    // ---- RDP --------------------------------------------------------------------------------------
    public IReadOnlyList<Option<RdpResolutionMode>> ResolutionOptions { get; }
    public IReadOnlyList<Option<RdpColorDepth>> ColorOptions { get; }
    public IReadOnlyList<Option<RdpAuthenticationLevel>> AuthLevelOptions { get; }
    public IReadOnlyList<Option<RdpSoundRedirection>> SoundOptions { get; }
    public IReadOnlyList<Option<RdpDiskDrives>> DriveOptions { get; }
    public IReadOnlyList<Option<RdGatewayUsage>> GatewayUsageOptions { get; }
    [ObservableProperty] private Option<RdpResolutionMode> _resolution;
    [ObservableProperty] private Option<RdpColorDepth> _colorDepth;
    [ObservableProperty] private Option<RdpAuthenticationLevel> _authLevel;
    [ObservableProperty] private Option<RdpSoundRedirection> _sound;
    [ObservableProperty] private Option<RdpDiskDrives> _drives;
    [ObservableProperty] private Option<RdGatewayUsage> _gatewayUsage;
    [ObservableProperty] private string _gatewayHost;
    [ObservableProperty] private bool? _useCredSsp;
    [ObservableProperty] private bool? _redirectClipboard;
    [ObservableProperty] private bool? _redirectPrinters;
    [ObservableProperty] private bool? _redirectKeys;
    [ObservableProperty] private bool? _consoleSession;
    [ObservableProperty] private bool? _restrictedAdmin;
    public string GatewayHostPlaceholder => Hint(_inherited.Rdp.Gateway.Hostname);

    /// <summary>Warnung bei unsicherer Einstellung (z. B. Zertifikat nicht prüfen).</summary>
    public string? SecurityWarning => (AuthLevel.Value ?? _inherited.Rdp.AuthenticationLevel) == RdpAuthenticationLevel.NoAuth
        ? "Unsicher: Das Server-Zertifikat wird nicht geprüft (anfällig für Man-in-the-Middle). Empfohlen: „Warnen“."
        : (UseCredSsp ?? _inherited.Rdp.UseCredSsp) == false ? "Unsicher: NLA/CredSSP ist deaktiviert." : null;

    partial void OnAuthLevelChanged(Option<RdpAuthenticationLevel> value) => OnPropertyChanged(nameof(SecurityWarning));
    partial void OnUseCredSspChanged(bool? value) => OnPropertyChanged(nameof(SecurityWarning));

    // ---- Übernehmen -------------------------------------------------------------------------------

    /// <summary>Prüft und schreibt zurück. Liefert eine Fehlermeldung oder <c>null</c>.</summary>
    public string? Apply()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Bitte einen Namen eingeben.";
        int? port = null;
        if (Port.Trim().Length > 0)
        {
            if (!int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
                return "Der Port muss eine Zahl zwischen 1 und 65535 sein.";
            port = p;
        }

        _node.Name = Name.Trim();
        if (_node is ConnectionNode c) c.Hostname = Hostname.Trim();

        var s = _node.Settings;
        s.Protocol = Protocol.Value;
        s.Port = port;
        s.Description = NullIfEmpty(Description);
        s.EnvironmentTags = NullIfEmpty(Tags);
        s.Favorite = Favorite;

        s.CredentialId = Credential.Id;
        s.Username = NullIfEmpty(Username);
        s.Domain = NullIfEmpty(Domain);
        if (InheritPassword) s.Password = null;
        else if (!string.IsNullOrEmpty(NewPassword)) s.Password = NewPassword;
        else if (s.Password is null) s.Password = ""; // „nicht erben“ ohne neues Passwort ⇒ explizit leer

        var r = s.Rdp;
        r.ResolutionMode = Resolution.Value;
        r.ColorDepth = ColorDepth.Value;
        r.AuthenticationLevel = AuthLevel.Value;
        r.RedirectSound = Sound.Value;
        r.RedirectDiskDrives = Drives.Value;
        r.Gateway.Usage = GatewayUsage.Value;
        r.Gateway.Hostname = NullIfEmpty(GatewayHost);
        r.UseCredSsp = UseCredSsp;
        r.RedirectClipboard = RedirectClipboard;
        r.RedirectPrinters = RedirectPrinters;
        r.RedirectKeys = RedirectKeys;
        r.UseConsoleSession = ConsoleSession;
        r.UseRestrictedAdmin = RestrictedAdmin;
        return null;
    }

    private static string? NullIfEmpty(string value) => value.Trim().Length == 0 ? null : value.Trim();
    private string Hint(string? inherited) => string.IsNullOrEmpty(inherited) ? "" : $"geerbt von {InheritedFrom}: {inherited}";

    private static IReadOnlyList<Option<T>> Options<T>(T? inherited) where T : struct, Enum =>
        [new Option<T>(null, $"(geerbt: {Label(inherited)})"), .. Enum.GetValues<T>().Select(v => new Option<T>(v, Label<T>(v)))];

    private static Option<T> Find<T>(IReadOnlyList<Option<T>> options, T? value) where T : struct =>
        options.First(o => EqualityComparer<T?>.Default.Equals(o.Value, value));

    /// <summary>Deutsche Bezeichnungen für Enum-Werte.</summary>
    internal static string Label<T>(T? value) where T : struct, Enum => value switch
    {
        null => "–",
        RdpResolutionMode m => m switch { RdpResolutionMode.FitToWindow => "An Fenster anpassen (dynamisch)", RdpResolutionMode.SmartSize => "Skalieren (Smart Sizing)", RdpResolutionMode.Fullscreen => "Vollbild", _ => "Feste Auflösung" },
        RdpColorDepth d => $"{(int)d} Bit",
        RdpAuthenticationLevel a => a switch { RdpAuthenticationLevel.NoAuth => "Nicht prüfen (unsicher)", RdpAuthenticationLevel.AuthRequired => "Prüfen, bei Fehler abbrechen", _ => "Prüfen, bei Fehler warnen" },
        RdpSoundRedirection snd => snd switch { RdpSoundRedirection.BringToThisComputer => "Auf diesem PC", RdpSoundRedirection.LeaveAtRemoteComputer => "Auf dem Server", _ => "Nicht wiedergeben" },
        RdpDiskDrives dd => dd switch { RdpDiskDrives.None => "Keine", RdpDiskDrives.Local => "Lokale Laufwerke", RdpDiskDrives.All => "Alle (auch USB)", _ => "Benutzerdefiniert" },
        RdGatewayUsage g => g switch { RdGatewayUsage.Never => "Nie", RdGatewayUsage.Always => "Immer", _ => "Automatisch erkennen" },
        ProtocolKind p => p switch { ProtocolKind.Ssh2 => "SSH", ProtocolKind.Rdp => "RDP", _ => p.ToString().ToUpperInvariant() },
        _ => value.ToString()!,
    };
}
