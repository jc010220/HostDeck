namespace HostDeck.Core.Model;

/// <summary>
/// Markiert eine verschachtelte Einstellungsgruppe. Der <see cref="Inheritance.SettingsResolver"/>
/// löst solche Gruppen rekursiv auf (feldweise Vererbung auch innerhalb der Gruppe).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SettingsGroupAttribute : Attribute;

/// <summary>
/// Vererbbare Verbindungseinstellungen.
///
/// <para><b>Vererbungsregel (übersichtlicher als mRemoteNG):</b> Jede Eigenschaft ist nullable.
/// <c>null</c> = "vom übergeordneten Ordner erben", jeder andere Wert = "hier explizit gesetzt".
/// mRemoteNG speichert dagegen Wert <i>und</i> separates <c>InheritXyz</c>-Flag; der Importer
/// übersetzt <c>InheritXyz="True"</c> in <c>null</c>.</para>
///
/// Ordner und Verbindungen tragen dasselbe Settings-Objekt; Ordner liefern so Defaults für alle
/// Kinder. Ganz oben steht <see cref="Defaults"/>.
/// </summary>
[SettingsGroup]
public sealed class ConnectionSettings
{
    // -- Allgemein ---------------------------------------------------------
    public ProtocolKind? Protocol { get; set; }

    /// <summary>Port; ist er nach der Auflösung noch null, gilt der Standardport des Protokolls.</summary>
    public int? Port { get; set; }

    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Panel { get; set; }
    /// <summary>Farbe des Knotens im Session-Tree.</summary>
    public string? Color { get; set; }
    /// <summary>Farbe des Tabs einer geöffneten Session.</summary>
    public string? TabColor { get; set; }
    public string? EnvironmentTags { get; set; }
    public bool? Favorite { get; set; }
    public string? UserField { get; set; }
    public string? MacAddress { get; set; }

    // -- Zugangsdaten ------------------------------------------------------
    /// <summary>Verweis auf einen Eintrag im <see cref="CredentialVault"/>.</summary>
    public Guid? CredentialId { get; set; }

    public string? Username { get; set; }
    public string? Domain { get; set; }

    /// <summary>Klartext nur im Arbeitsspeicher; persistiert wird ausschließlich verschlüsselt.</summary>
    public string? Password { get; set; }

    // -- Externe Tools -----------------------------------------------------
    public string? PreExtApp { get; set; }
    public string? PostExtApp { get; set; }
    public string? ExtApp { get; set; }

    // -- Protokollgruppen --------------------------------------------------
    public SshSettings Ssh { get; set; } = new();
    public RdpSettings Rdp { get; set; } = new();
    public VncSettings Vnc { get; set; } = new();

    /// <summary>Eingebaute Standardwerte (Wurzel der Vererbungskette).</summary>
    public static ConnectionSettings Defaults { get; } = CreateDefaults();

    public static ConnectionSettings CreateDefaults() => new()
    {
        Protocol = ProtocolKind.Rdp,
        Port = null, // => Protokoll-Standardport
        Description = "",
        Icon = "mRemoteNG",
        Panel = "General",
        Color = "",
        TabColor = "",
        EnvironmentTags = "",
        Favorite = false,
        UserField = "",
        MacAddress = "",
        CredentialId = null,
        Username = "",
        Domain = "",
        Password = "",
        PreExtApp = "",
        PostExtApp = "",
        ExtApp = "",
        Ssh = new SshSettings
        {
            PuttySession = "Default Settings",
            OpeningCommand = "",
            Options = "",
            TunnelConnectionName = "",
        },
        Rdp = new RdpSettings
        {
            UseConsoleSession = false,
            UseCredSsp = true,
            UseRestrictedAdmin = false,
            UseRemoteCredentialGuard = false,
            UseRedirectionServerName = false,
            // Sicherer als mRemoteNG (NoAuth): bei ungültigem Server-Zertifikat warnen
            AuthenticationLevel = RdpAuthenticationLevel.WarnOnFailedAuth,
            LoadBalanceInfo = "",
            Version = RdpVersion.Highest,
            ColorDepth = RdpColorDepth.Colors16Bit,
            ResolutionMode = RdpResolutionMode.FitToWindow,
            DesktopWidth = 0,
            DesktopHeight = 0,
            AutomaticResize = true,
            DisplayWallpaper = false,
            DisplayThemes = false,
            EnableFontSmoothing = false,
            EnableDesktopComposition = false,
            DisableFullWindowDrag = false,
            DisableMenuAnimations = false,
            DisableCursorShadow = false,
            DisableCursorBlinking = false,
            CacheBitmaps = false,
            RedirectDiskDrives = RdpDiskDrives.None,
            RedirectDiskDrivesCustom = "",
            RedirectPorts = false,
            RedirectPrinters = false,
            RedirectSmartCards = false,
            RedirectClipboard = true,
            RedirectKeys = false,
            RedirectWebAuthn = false,
            RedirectSound = RdpSoundRedirection.DoNotPlay,
            SoundQuality = RdpSoundQuality.Dynamic,
            RedirectAudioCapture = false,
            StartProgram = "",
            StartProgramWorkDir = "",
            MinutesToIdleTimeout = 0,
            AlertIdleTimeout = false,
            UseVmId = false,
            VmId = "",
            UseEnhancedMode = false,
            Gateway = new RdGatewaySettings
            {
                Usage = RdGatewayUsage.Never,
                Hostname = "",
                CredentialMode = RdGatewayCredentialMode.Yes,
                Username = "",
                Password = "",
                Domain = "",
                AccessToken = "",
            },
        },
        Vnc = new VncSettings
        {
            Compression = VncCompression.CompNone,
            Encoding = VncEncoding.EncHextile,
            AuthMode = VncAuthMode.AuthVNC,
            ProxyType = VncProxyType.ProxyNone,
            ProxyHost = "",
            ProxyPort = 0,
            ProxyUsername = "",
            ProxyPassword = "",
            Colors = VncColors.ColNormal,
            SmartSizeMode = VncSmartSizeMode.SmartSAspect,
            ViewOnly = false,
        },
    };
}

[SettingsGroup]
public sealed class SshSettings
{
    /// <summary>Name einer PuTTY-Session (Kompatibilität zu mRemoteNG).</summary>
    public string? PuttySession { get; set; }
    public string? OpeningCommand { get; set; }
    public string? Options { get; set; }
    /// <summary>Name einer anderen Verbindung, über die getunnelt wird (Jump-Host).</summary>
    public string? TunnelConnectionName { get; set; }
}

[SettingsGroup]
public sealed class RdpSettings
{
    public bool? UseConsoleSession { get; set; }
    public bool? UseCredSsp { get; set; }
    public bool? UseRestrictedAdmin { get; set; }
    public bool? UseRemoteCredentialGuard { get; set; }
    public bool? UseRedirectionServerName { get; set; }
    public RdpAuthenticationLevel? AuthenticationLevel { get; set; }
    public string? LoadBalanceInfo { get; set; }
    public RdpVersion? Version { get; set; }

    public RdpColorDepth? ColorDepth { get; set; }
    public RdpResolutionMode? ResolutionMode { get; set; }
    /// <summary>Nur bei <see cref="RdpResolutionMode.Fixed"/>.</summary>
    public int? DesktopWidth { get; set; }
    public int? DesktopHeight { get; set; }
    public bool? AutomaticResize { get; set; }

    public bool? DisplayWallpaper { get; set; }
    public bool? DisplayThemes { get; set; }
    public bool? EnableFontSmoothing { get; set; }
    public bool? EnableDesktopComposition { get; set; }
    public bool? DisableFullWindowDrag { get; set; }
    public bool? DisableMenuAnimations { get; set; }
    public bool? DisableCursorShadow { get; set; }
    public bool? DisableCursorBlinking { get; set; }
    public bool? CacheBitmaps { get; set; }

    public RdpDiskDrives? RedirectDiskDrives { get; set; }
    public string? RedirectDiskDrivesCustom { get; set; }
    public bool? RedirectPorts { get; set; }
    public bool? RedirectPrinters { get; set; }
    public bool? RedirectSmartCards { get; set; }
    public bool? RedirectClipboard { get; set; }
    public bool? RedirectKeys { get; set; }
    public bool? RedirectWebAuthn { get; set; }
    public RdpSoundRedirection? RedirectSound { get; set; }
    public RdpSoundQuality? SoundQuality { get; set; }
    public bool? RedirectAudioCapture { get; set; }

    public string? StartProgram { get; set; }
    public string? StartProgramWorkDir { get; set; }
    public int? MinutesToIdleTimeout { get; set; }
    public bool? AlertIdleTimeout { get; set; }

    public bool? UseVmId { get; set; }
    public string? VmId { get; set; }
    public bool? UseEnhancedMode { get; set; }

    public RdGatewaySettings Gateway { get; set; } = new();
}

[SettingsGroup]
public sealed class RdGatewaySettings
{
    public RdGatewayUsage? Usage { get; set; }
    public string? Hostname { get; set; }
    public RdGatewayCredentialMode? CredentialMode { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }
    public string? AccessToken { get; set; }
}

[SettingsGroup]
public sealed class VncSettings
{
    public VncCompression? Compression { get; set; }
    public VncEncoding? Encoding { get; set; }
    public VncAuthMode? AuthMode { get; set; }
    public VncProxyType? ProxyType { get; set; }
    public string? ProxyHost { get; set; }
    public int? ProxyPort { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }
    public VncColors? Colors { get; set; }
    public VncSmartSizeMode? SmartSizeMode { get; set; }
    public bool? ViewOnly { get; set; }
}
