using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;

namespace HostDeck.Protocols.Rdp;

/// <summary>Auf welchem Objekt des RDP-ActiveX-Controls eine Eigenschaft gesetzt wird.</summary>
public enum RdpScope
{
    /// <summary>IMsRdpClient (Server, DesktopWidth, ColorDepth …)</summary>
    Client,
    /// <summary>IMsRdpClientAdvancedSettingsN</summary>
    Advanced,
    /// <summary>IMsRdpClientSecuredSettingsN</summary>
    Secured,
    /// <summary>IMsRdpClientTransportSettingsN (RD Gateway)</summary>
    Transport,
    /// <summary>IMsRdpExtendedSettings (z. B. DesktopScaleFactor, RestrictedLogon)</summary>
    Extended,
}

public sealed record RdpProperty(RdpScope Scope, string Name, object Value, bool Secret = false)
{
    public override string ToString() => $"{Scope}.{Name} = {(Secret ? "***" : Value)}";
}

/// <summary>Einstellungen der nicht-skriptbaren Schnittstelle IMsRdpClientNonScriptable5.</summary>
public sealed record RdpNonScriptable(
    bool PromptForCredentials,
    bool NegotiateSecurityLayer,
    bool EnableCredSspSupport,
    bool RedirectDynamicDrives,
    bool ShowRedirectionWarningDialog,
    bool WarnAboutSendingCredentials,
    bool WarnAboutClipboardRedirection);

/// <summary>
/// Vollständige, geprüfte Beschreibung einer RDP-Verbindung – unabhängig vom ActiveX-Control und
/// damit plattformneutral testbar. Die Windows-Seite wendet den Plan nur noch mechanisch an.
/// </summary>
public sealed class RdpPlan
{
    public required string Server { get; init; }
    public required int Port { get; init; }
    public required string UserName { get; init; }
    public required string Domain { get; init; }
    /// <summary>Nur für IMsTscNonScriptable.ClearTextPassword; nie protokollieren, nie in .rdp-Dateien.</summary>
    public string? Password { get; init; }

    public required RdpResolutionMode ResolutionMode { get; init; }
    /// <summary>Feste Auflösung (Fixed); sonst wird die Größe des Tab-Bereichs verwendet.</summary>
    public int? FixedWidth { get; init; }
    public int? FixedHeight { get; init; }
    /// <summary>Auflösung bei Größenänderung des Fensters nachführen (UpdateSessionDisplaySettings).</summary>
    public required bool DynamicResolution { get; init; }

    public required int DesktopScaleFactor { get; init; }
    public required int DeviceScaleFactor { get; init; }

    public required IReadOnlyList<RdpProperty> Properties { get; init; }
    public required RdpNonScriptable NonScriptable { get; init; }

    /// <summary>Nur bei RdpDiskDrives.Custom: zu verbindende Laufwerksbuchstaben (z. B. "C", "D").</summary>
    public IReadOnlyList<string> RedirectDriveLetters { get; init; } = [];

    public override string ToString() => $"{UserName}@{Server}:{Port} ({Properties.Count} properties)";
}

public sealed class RdpPlanException(string message) : Exception(message);

/// <summary>Übersetzt aufgelöste HostDeck-/mRemoteNG-Einstellungen in einen <see cref="RdpPlan"/>.</summary>
public static class RdpPlanBuilder
{
    // TS_PERF_* (mstsax.h)
    internal const int PerfDisableWallpaper = 0x1;
    internal const int PerfDisableFullWindowDrag = 0x2;
    internal const int PerfDisableMenuAnimations = 0x4;
    internal const int PerfDisableTheming = 0x8;
    internal const int PerfDisableCursorShadow = 0x20;
    internal const int PerfDisableCursorBlinking = 0x40;
    internal const int PerfEnableFontSmoothing = 0x80;
    internal const int PerfEnableDesktopComposition = 0x100;

    /// <param name="dpiScalePercent">Skalierung des Monitors in Prozent (100, 125, 150, 200 …).</param>
    public static RdpPlan Build(ConnectionNode connection, ConnectionSettings resolved, ResolvedCredentials credentials, int dpiScalePercent = 100)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var host = connection.Hostname.Trim();
        if (host.Length == 0) throw new RdpPlanException($"“{connection.Name}” has no hostname.");

        var rdp = resolved.Rdp;
        var gw = rdp.Gateway;
        var props = new List<RdpProperty>();
        void Add(RdpScope scope, string name, object value, bool secret = false) => props.Add(new RdpProperty(scope, name, value, secret));

        // ---- Anzeige ----------------------------------------------------------------------------------
        Add(RdpScope.Client, "ColorDepth", (int)(rdp.ColorDepth ?? RdpColorDepth.Colors32Bit));
        var mode = rdp.ResolutionMode ?? RdpResolutionMode.FitToWindow;
        Add(RdpScope.Advanced, "SmartSizing", mode == RdpResolutionMode.SmartSize);
        Add(RdpScope.Client, "FullScreen", mode == RdpResolutionMode.Fullscreen);
        Add(RdpScope.Advanced, "ContainerHandledFullScreen", 0);  // Control verwaltet Vollbild selbst (Verbindungsleiste)
        Add(RdpScope.Advanced, "DisplayConnectionBar", true);
        Add(RdpScope.Advanced, "GrabFocusOnConnect", true);

        // ---- Sicherheit -------------------------------------------------------------------------------
        var credSsp = rdp.UseCredSsp ?? true;
        Add(RdpScope.Advanced, "EnableCredSspSupport", credSsp);
        // 0 = verbinden ohne Warnung, 1 = Abbruch bei Fehler, 2 = warnen. Sicherer Standard: warnen.
        Add(RdpScope.Advanced, "AuthenticationLevel", (int)(rdp.AuthenticationLevel ?? RdpAuthenticationLevel.WarnOnFailedAuth));
        if (rdp.UseRestrictedAdmin == true)
        {
            Add(RdpScope.Extended, "RestrictedLogon", true);
            Add(RdpScope.Extended, "DisableCredentialsDelegation", true);
        }
        if (rdp.UseRemoteCredentialGuard == true)
            Add(RdpScope.Extended, "RedirectedAuthentication", true);

        // ---- Verbindung -------------------------------------------------------------------------------
        var port = resolved.Port is > 0 and <= 65535 ? resolved.Port.Value : 3389;
        Add(RdpScope.Advanced, "RDPPort", port);
        Add(RdpScope.Advanced, "ConnectToServerConsole", rdp.UseConsoleSession == true);
        Add(RdpScope.Advanced, "EnableAutoReconnect", true);
        Add(RdpScope.Advanced, "MaxReconnectAttempts", 20);
        Add(RdpScope.Advanced, "keepAliveInterval", 60_000);
        if (!string.IsNullOrEmpty(rdp.LoadBalanceInfo)) Add(RdpScope.Advanced, "LoadBalanceInfo", rdp.LoadBalanceInfo);
        if (rdp.MinutesToIdleTimeout is > 0 and <= 240) Add(RdpScope.Advanced, "MinutesToIdleTimeout", rdp.MinutesToIdleTimeout.Value);
        Add(RdpScope.Advanced, "BandwidthDetection", true);
        Add(RdpScope.Advanced, "NetworkConnectionType", 7); // CONNECTION_TYPE_AUTODETECT
        Add(RdpScope.Advanced, "BitmapPersistence", rdp.CacheBitmaps == true ? 1 : 0);
        Add(RdpScope.Advanced, "PerformanceFlags", PerformanceFlags(rdp));

        if (!string.IsNullOrEmpty(rdp.StartProgram))
        {
            Add(RdpScope.Secured, "StartProgram", rdp.StartProgram);
            if (!string.IsNullOrEmpty(rdp.StartProgramWorkDir)) Add(RdpScope.Secured, "WorkDir", rdp.StartProgramWorkDir);
        }

        // ---- Umleitungen ------------------------------------------------------------------------------
        var drives = rdp.RedirectDiskDrives ?? RdpDiskDrives.None;
        Add(RdpScope.Advanced, "RedirectDrives", drives is RdpDiskDrives.All or RdpDiskDrives.Local);
        Add(RdpScope.Advanced, "RedirectPrinters", rdp.RedirectPrinters == true);
        Add(RdpScope.Advanced, "RedirectPorts", rdp.RedirectPorts == true);
        Add(RdpScope.Advanced, "RedirectSmartCards", rdp.RedirectSmartCards == true);
        Add(RdpScope.Advanced, "RedirectClipboard", rdp.RedirectClipboard ?? true);
        Add(RdpScope.Advanced, "AudioRedirectionMode", (int)(rdp.RedirectSound ?? RdpSoundRedirection.BringToThisComputer));
        Add(RdpScope.Advanced, "AudioQualityMode", (int)(rdp.SoundQuality ?? RdpSoundQuality.Dynamic));
        Add(RdpScope.Advanced, "AudioCaptureRedirectionMode", rdp.RedirectAudioCapture == true);
        // 0 = Windows-Tastenkombinationen lokal, 1 = immer an den Server, 2 = nur im Vollbild
        Add(RdpScope.Secured, "KeyboardHookMode", rdp.RedirectKeys == true ? 1 : 2);

        // ---- RD Gateway -------------------------------------------------------------------------------
        var gatewayUsage = gw.Usage ?? RdGatewayUsage.Never;
        if (gatewayUsage != RdGatewayUsage.Never && !string.IsNullOrWhiteSpace(gw.Hostname))
        {
            Add(RdpScope.Transport, "GatewayHostname", gw.Hostname.Trim());
            Add(RdpScope.Transport, "GatewayUsageMethod", (int)gatewayUsage);   // 1 = immer, 2 = automatisch erkennen
            Add(RdpScope.Transport, "GatewayProfileUsageMethod", 1);           // explizit (nicht aus Gruppenrichtlinie)
            switch (gw.CredentialMode ?? RdGatewayCredentialMode.Yes)
            {
                case RdGatewayCredentialMode.SmartCard:
                    Add(RdpScope.Transport, "GatewayCredsSource", 1);
                    break;
                case RdGatewayCredentialMode.Yes: // Zugangsdaten der Verbindung verwenden
                    Add(RdpScope.Transport, "GatewayCredsSource", 0);
                    Add(RdpScope.Transport, "GatewayCredSharing", 1);
                    break;
                default: // eigene Gateway-Zugangsdaten
                    Add(RdpScope.Transport, "GatewayCredsSource", 0);
                    Add(RdpScope.Transport, "GatewayCredSharing", 0);
                    if (!string.IsNullOrEmpty(gw.Username)) Add(RdpScope.Transport, "GatewayUsername", gw.Username);
                    if (!string.IsNullOrEmpty(gw.Domain)) Add(RdpScope.Transport, "GatewayDomain", gw.Domain);
                    if (!string.IsNullOrEmpty(gw.Password)) Add(RdpScope.Transport, "GatewayPassword", gw.Password, secret: true);
                    break;
            }
        }

        // ---- DPI (scharf auf 4K) ------------------------------------------------------------------------
        var desktopScale = Math.Clamp(dpiScalePercent, 100, 500);
        var deviceScale = desktopScale >= 180 ? 180 : desktopScale >= 140 ? 140 : 100; // erlaubte Werte laut MS: 100/140/180
        Add(RdpScope.Extended, "DesktopScaleFactor", (uint)desktopScale);
        Add(RdpScope.Extended, "DeviceScaleFactor", (uint)deviceScale);

        var hasPassword = !string.IsNullOrEmpty(credentials.Password);
        return new RdpPlan
        {
            Server = host,
            Port = port,
            UserName = credentials.Username,
            Domain = credentials.Domain,
            Password = hasPassword ? credentials.Password : null,
            ResolutionMode = mode,
            FixedWidth = mode == RdpResolutionMode.Fixed ? rdp.DesktopWidth : null,
            FixedHeight = mode == RdpResolutionMode.Fixed ? rdp.DesktopHeight : null,
            DynamicResolution = mode == RdpResolutionMode.FitToWindow && (rdp.AutomaticResize ?? true),
            DesktopScaleFactor = desktopScale,
            DeviceScaleFactor = deviceScale,
            Properties = props,
            NonScriptable = new RdpNonScriptable(
                // Kein gespeichertes Passwort ⇒ sicherer Windows-Anmeldedialog (CredUI) statt Fehlschlag
                PromptForCredentials: !hasPassword,
                NegotiateSecurityLayer: true,
                EnableCredSspSupport: credSsp,
                RedirectDynamicDrives: drives == RdpDiskDrives.All,
                ShowRedirectionWarningDialog: false, // Umleitungen wurden bewusst konfiguriert
                WarnAboutSendingCredentials: false,
                WarnAboutClipboardRedirection: false),
            RedirectDriveLetters = drives == RdpDiskDrives.Custom ? ParseDriveLetters(rdp.RedirectDiskDrivesCustom) : [],
        };
    }

    public static RdpPlan Build(ConnectionNode connection, CredentialVault vault, int dpiScalePercent = 100) =>
        Build(connection, SettingsResolver.Resolve(connection), CredentialResolver.Resolve(connection, vault), dpiScalePercent);

    internal static int PerformanceFlags(RdpSettings rdp)
    {
        var flags = 0;
        if (rdp.DisplayWallpaper != true) flags |= PerfDisableWallpaper;
        if (rdp.DisplayThemes != true) flags |= PerfDisableTheming;
        if (rdp.DisableFullWindowDrag == true) flags |= PerfDisableFullWindowDrag;
        if (rdp.DisableMenuAnimations == true) flags |= PerfDisableMenuAnimations;
        if (rdp.DisableCursorShadow == true) flags |= PerfDisableCursorShadow;
        if (rdp.DisableCursorBlinking == true) flags |= PerfDisableCursorBlinking;
        if (rdp.EnableFontSmoothing == true) flags |= PerfEnableFontSmoothing;
        if (rdp.EnableDesktopComposition == true) flags |= PerfEnableDesktopComposition;
        return flags;
    }

    internal static IReadOnlyList<string> ParseDriveLetters(string? custom) =>
        (custom ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimEnd(':', '\\').ToUpperInvariant())
            .Where(s => s.Length == 1 && char.IsAsciiLetter(s[0]))
            .Distinct()
            .ToList();

    /// <summary>
    /// Normale, vom Benutzer bzw. Server gewollte Trennungen (kein Fehlerdialog). Alle anderen Codes
    /// beschreibt Windows selbst über GetErrorDescription(reason, ExtendedDisconnectReason).
    /// </summary>
    public static bool IsNormalDisconnect(int reason) => reason is 1 or 2 or 3 or 0xB08;
}
