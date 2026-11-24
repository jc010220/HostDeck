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
        InheritedFrom = node.Parent is { Parent: not null } p ? p.Name : "Defaults";

        var s = node.Settings;
        _name = node.Name;
        _hostname = (node as ConnectionNode)?.Hostname ?? "";
        // Nur Protokolle zur Auswahl, die HostDeck wirklich verbindet (aus mRemoteNG übernommene andere bleiben sichtbar,
        // solange der Eintrag sie nutzt). SSH1 ist veraltet und unsicher – HostDeck verbindet es ohnehin per SSH (Version 2).
        ProtocolOptions = Options(_inherited.Protocol)
            .Where(o => o.Value is null || SupportedProtocols.Contains(o.Value.Value) || o.Value == s.Protocol)
            .Select(o => o.Value == ProtocolKind.Ssh1 ? new Option<ProtocolKind>(o.Value, "SSH1 (from mRemoteNG – connects as SSH)") : o)
            .ToList();
        _protocol = Find(ProtocolOptions, s.Protocol);
        _port = s.Port?.ToString(CultureInfo.InvariantCulture) ?? "";
        _description = s.Description ?? "";
        _tags = s.EnvironmentTags ?? "";
        _favorite = s.Favorite;

        CredentialOptions = [new CredentialOption(null, "(none – username/password below or inherited)"),
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
    public string Title => IsFolder ? $"Folder “{_node.Name}” – defaults for all entries inside" : $"Connection “{_node.Name}”";

    // ---- Allgemein --------------------------------------------------------------------------------
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _hostname;
    [ObservableProperty] private Option<ProtocolKind> _protocol;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private bool? _favorite;

    public IReadOnlyList<Option<ProtocolKind>> ProtocolOptions { get; }

    private static readonly HashSet<ProtocolKind> SupportedProtocols =
        [ProtocolKind.Rdp, ProtocolKind.Ssh2, ProtocolKind.Telnet, ProtocolKind.Raw, ProtocolKind.Vnc, ProtocolKind.Http, ProtocolKind.Https];
    public string PortPlaceholder => _inherited.Port is { } p && IsInheritedProtocolPort && Protocol.Value is null ? $"inherited: {p}" : $"default: {ProtocolDefaults.DefaultPort(EffectiveProtocol)}";
    private ProtocolKind EffectiveProtocol => Protocol.Value ?? _inherited.Protocol ?? ProtocolKind.Rdp;

    /// <summary>RDP-Reiter nur bei RDP-Verbindungen; Ordner zeigen ihn immer (Vorgaben für die Einträge darin).</summary>
    public bool ShowRdpTab => IsFolder || EffectiveProtocol == ProtocolKind.Rdp;
    private bool IsInheritedProtocolPort => _node.Parent is not null && SettingsResolver.FindOrigin(_node.Parent, x => x.Port) is not null;
    public string DescriptionPlaceholder => Hint(_inherited.Description);
    public string TagsPlaceholder => Hint(_inherited.EnvironmentTags);

    // ---- Protokoll ⇄ Port ----
    // Protokoll gewählt ⇒ dessen Standardport eintragen (nur wenn das Feld leer ist oder den Standardport des bisherigen
    // Protokolls enthält – eigene Ports bleiben). Bekannter Port eines anderen Protokolls eingetippt (22, 3389 …) ⇒ Protokoll
    // passend umstellen; tippt man weiter („22“ → „2200“), wird die automatische Umstellung wieder zurückgenommen.
    private bool _syncing;
    private Option<ProtocolKind>? _autoSwitchedFrom;

    private static readonly Dictionary<int, ProtocolKind> WellKnownPorts = new()
    {
        [22] = ProtocolKind.Ssh2, [23] = ProtocolKind.Telnet, [80] = ProtocolKind.Http, [443] = ProtocolKind.Https,
        [513] = ProtocolKind.Rlogin, [3389] = ProtocolKind.Rdp, [5900] = ProtocolKind.Vnc,
    };

    partial void OnProtocolChanged(Option<ProtocolKind>? oldValue, Option<ProtocolKind> newValue)
    {
        OnPropertyChanged(nameof(PortPlaceholder));
        OnPropertyChanged(nameof(ShowRdpTab));
        if (_syncing || oldValue is null) return;
        _autoSwitchedFrom = null; // bewusst gewählt
        var oldProtocol = oldValue.Value ?? _inherited.Protocol ?? ProtocolKind.Rdp;
        var newProtocol = EffectiveProtocol;
        if (oldProtocol == newProtocol) return;
        var current = Port.Trim();
        if (current.Length > 0 && current != ProtocolDefaults.DefaultPort(oldProtocol).ToString(CultureInfo.InvariantCulture)) return;
        var port = ProtocolDefaults.DefaultPort(newProtocol);
        _syncing = true;
        // Protokoll wieder auf „geerbt“ ⇒ auch den Port erben; sonst Standardport sichtbar eintragen
        try { Port = newValue.Value is null || port == 0 ? "" : port.ToString(CultureInfo.InvariantCulture); }
        finally { _syncing = false; }
    }

    partial void OnPortChanged(string value)
    {
        if (_syncing) return;
        var known = int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    && WellKnownPorts.TryGetValue(port, out var p) ? p : (ProtocolKind?)null;
        _syncing = true;
        try
        {
            if (known is { } protocol && protocol != EffectiveProtocol
                && !(protocol == ProtocolKind.Ssh2 && EffectiveProtocol == ProtocolKind.Ssh1))
            {
                _autoSwitchedFrom ??= Protocol;
                Protocol = ProtocolOptions.First(o => o.Value == protocol);
            }
            else if (known is null && _autoSwitchedFrom is { } previous)
            {
                // „22“ war nur der Anfang von „2200“ ⇒ wieder das vorherige Protokoll
                Protocol = previous;
                _autoSwitchedFrom = null;
            }
        }
        finally { _syncing = false; }
    }

    // ---- Anmeldung --------------------------------------------------------------------------------
    public IReadOnlyList<CredentialOption> CredentialOptions { get; }
    [ObservableProperty] private CredentialOption _credential;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _domain;
    /// <summary>Neues Passwort (aus der PasswordBox); <c>null</c> = unverändert.</summary>
    public string? NewPassword { get; set; }
    public bool HasOwnPassword { get; }
    [ObservableProperty] private bool _inheritPassword;
    /// <summary>Gespeichertes Passwort löschen (danach: kein Passwort, beim Verbinden wird gefragt).</summary>
    [ObservableProperty] private bool _clearPassword;
    public bool CanClearPassword => HasOwnPassword && !InheritPassword;
    partial void OnInheritPasswordChanged(bool value) { OnPropertyChanged(nameof(CanClearPassword)); OnPropertyChanged(nameof(PasswordHint)); }
    partial void OnClearPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordHint));

    public string UsernamePlaceholder => Hint(InheritedCredentials.Username);
    public string DomainPlaceholder => Hint(InheritedCredentials.Domain);
    public string PasswordHint => ClearPassword && CanClearPassword ? "The stored password will be deleted – you will be asked for it when connecting."
        : HasOwnPassword ? "A password is stored – leave empty to keep it."
        : InheritPassword && InheritedCredentials.Password.Length > 0 ? $"Password is inherited from “{InheritedFrom}”." : "No password stored.";
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
        ? "Insecure: the server certificate is not verified (vulnerable to man-in-the-middle). Recommended: “Warn”."
        : (UseCredSsp ?? _inherited.Rdp.UseCredSsp) == false ? "Insecure: NLA/CredSSP is disabled." : null;

    partial void OnAuthLevelChanged(Option<RdpAuthenticationLevel> value) => OnPropertyChanged(nameof(SecurityWarning));
    partial void OnUseCredSspChanged(bool? value) => OnPropertyChanged(nameof(SecurityWarning));

    // ---- Übernehmen -------------------------------------------------------------------------------

    /// <summary>Prüft und schreibt zurück. Liefert eine Fehlermeldung oder <c>null</c>.</summary>
    public string? Apply()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Please enter a name.";
        int? port = null;
        if (Port.Trim().Length > 0)
        {
            if (!int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
                return "The port must be a number between 1 and 65535.";
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
        else if (ClearPassword) s.Password = "";                      // gelöscht, nicht erben
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
    private string Hint(string? inherited) => string.IsNullOrEmpty(inherited) ? "" : $"inherited from {InheritedFrom}: {inherited}";

    private static IReadOnlyList<Option<T>> Options<T>(T? inherited) where T : struct, Enum =>
        [new Option<T>(null, $"(inherited: {Label(inherited)})"), .. Enum.GetValues<T>().Select(v => new Option<T>(v, Label<T>(v)))];

    private static Option<T> Find<T>(IReadOnlyList<Option<T>> options, T? value) where T : struct =>
        options.First(o => EqualityComparer<T?>.Default.Equals(o.Value, value));

    /// <summary>Deutsche Bezeichnungen für Enum-Werte.</summary>
    internal static string Label<T>(T? value) where T : struct, Enum => value switch
    {
        null => "–",
        RdpResolutionMode m => m switch { RdpResolutionMode.FitToWindow => "Fit to window (dynamic)", RdpResolutionMode.SmartSize => "Scale (smart sizing)", RdpResolutionMode.Fullscreen => "Full screen", _ => "Fixed resolution" },
        RdpColorDepth d => $"{(int)d} Bit",
        RdpAuthenticationLevel a => a switch { RdpAuthenticationLevel.NoAuth => "Don't verify (insecure)", RdpAuthenticationLevel.AuthRequired => "Verify, abort on error", _ => "Verify, warn on error" },
        RdpSoundRedirection snd => snd switch { RdpSoundRedirection.BringToThisComputer => "On this PC", RdpSoundRedirection.LeaveAtRemoteComputer => "On the server", _ => "Don't play" },
        RdpDiskDrives dd => dd switch { RdpDiskDrives.None => "None", RdpDiskDrives.Local => "Local drives", RdpDiskDrives.All => "All (incl. USB)", _ => "Custom" },
        RdGatewayUsage g => g switch { RdGatewayUsage.Never => "Never", RdGatewayUsage.Always => "Always", _ => "Detect automatically" },
        ProtocolKind p => p.DisplayName(),
        _ => value.ToString()!,
    };
}
