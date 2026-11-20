using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;
using HostDeck.Import.MRemoteNG;
using HostDeck.Protocols.Rdp;

namespace HostDeck.Tests.Rdp;

public class RdpPlanTests
{
    private static (ConnectionTree Tree, ConnectionNode Conn) Simple(Action<ConnectionSettings>? configure = null)
    {
        var tree = new ConnectionTree();
        var conn = tree.Root.Add(new ConnectionNode("ts01") { Hostname = " ts01.corp.local " });
        conn.Settings.Protocol = ProtocolKind.Rdp;
        conn.Settings.Username = "admin";
        conn.Settings.Domain = "CORP";
        conn.Settings.Password = "Geheim!";
        configure?.Invoke(conn.Settings);
        return (tree, conn);
    }

    private static object? Value(RdpPlan plan, RdpScope scope, string name) =>
        plan.Properties.SingleOrDefault(p => p.Scope == scope && p.Name == name)?.Value;

    [Fact]
    public void Secure_defaults()
    {
        var (tree, conn) = Simple();
        var plan = RdpPlanBuilder.Build(conn, tree.Vault);

        Assert.Equal("ts01.corp.local", plan.Server);
        Assert.Equal(3389, plan.Port);
        Assert.Equal(("admin", "CORP", "Geheim!"), (plan.UserName, plan.Domain, plan.Password));
        Assert.Equal(true, Value(plan, RdpScope.Advanced, "EnableCredSspSupport"));        // NLA an
        Assert.Equal(2, Value(plan, RdpScope.Advanced, "AuthenticationLevel"));            // Zertifikatsfehler: warnen
        Assert.True(plan.NonScriptable.NegotiateSecurityLayer);
        Assert.False(plan.NonScriptable.PromptForCredentials);                               // Passwort vorhanden
        Assert.Equal(false, Value(plan, RdpScope.Advanced, "RedirectDrives"));              // nichts umleiten ohne Auftrag
        Assert.Equal(true, Value(plan, RdpScope.Advanced, "RedirectClipboard"));
        Assert.True(plan.DynamicResolution);
        Assert.DoesNotContain("Geheim", plan.ToString());
        Assert.DoesNotContain(plan.Properties, p => p.ToString().Contains("Geheim"));
    }

    [Fact]
    public void Missing_password_uses_windows_credential_prompt()
    {
        var (tree, conn) = Simple(s => s.Password = "");
        var plan = RdpPlanBuilder.Build(conn, tree.Vault);
        Assert.Null(plan.Password);
        Assert.True(plan.NonScriptable.PromptForCredentials);
    }

    [Fact]
    public void Missing_hostname_is_rejected()
    {
        var tree = new ConnectionTree();
        var conn = tree.Root.Add(new ConnectionNode("leer"));
        Assert.Throws<RdpPlanException>(() => RdpPlanBuilder.Build(conn, tree.Vault));
    }

    [Fact]
    public void Performance_flags_match_mstsc_bits()
    {
        var rdp = new RdpSettings { DisplayWallpaper = false, DisplayThemes = true, EnableFontSmoothing = true, EnableDesktopComposition = true, DisableCursorBlinking = true };
        Assert.Equal(0x1 | 0x80 | 0x100 | 0x40, RdpPlanBuilder.PerformanceFlags(rdp));
        Assert.Equal(0x1 | 0x8, RdpPlanBuilder.PerformanceFlags(new RdpSettings()));
    }

    [Fact]
    public void Gateway_restricted_admin_and_redirections()
    {
        var (tree, conn) = Simple(s =>
        {
            s.Port = 3390;
            s.Rdp.Gateway.Usage = RdGatewayUsage.Always;
            s.Rdp.Gateway.Hostname = "gw.corp.local";
            s.Rdp.Gateway.CredentialMode = RdGatewayCredentialMode.No;
            s.Rdp.Gateway.Username = "gwuser";
            s.Rdp.Gateway.Password = "gwpass";
            s.Rdp.UseRestrictedAdmin = true;
            s.Rdp.RedirectDiskDrives = RdpDiskDrives.Custom;
            s.Rdp.RedirectDiskDrivesCustom = "c:, D, e:\\, 1, d";
            s.Rdp.RedirectSound = RdpSoundRedirection.LeaveAtRemoteComputer;
            s.Rdp.RedirectKeys = true;
        });
        var plan = RdpPlanBuilder.Build(conn, tree.Vault, dpiScalePercent: 150);

        Assert.Equal(3390, plan.Port);
        Assert.Equal(3390, Value(plan, RdpScope.Advanced, "RDPPort"));
        Assert.Equal("gw.corp.local", Value(plan, RdpScope.Transport, "GatewayHostname"));
        Assert.Equal(1, Value(plan, RdpScope.Transport, "GatewayUsageMethod"));
        Assert.Equal(0, Value(plan, RdpScope.Transport, "GatewayCredSharing"));
        Assert.True(plan.Properties.Single(p => p.Name == "GatewayPassword").Secret);
        Assert.Equal(true, Value(plan, RdpScope.Extended, "RestrictedLogon"));
        Assert.Equal(["C", "D", "E"], plan.RedirectDriveLetters);
        Assert.Equal(1, Value(plan, RdpScope.Advanced, "AudioRedirectionMode"));
        Assert.Equal(1, Value(plan, RdpScope.Secured, "KeyboardHookMode"));
        Assert.Equal((150, 140), (plan.DesktopScaleFactor, plan.DeviceScaleFactor));
        Assert.Equal(150u, Value(plan, RdpScope.Extended, "DesktopScaleFactor"));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(125, 100)]
    [InlineData(175, 140)]
    [InlineData(200, 180)]
    [InlineData(900, 180)]
    public void Device_scale_uses_allowed_values(int dpi, int device)
    {
        var (tree, conn) = Simple();
        Assert.Equal(device, RdpPlanBuilder.Build(conn, tree.Vault, dpi).DeviceScaleFactor);
    }

    [Fact]
    public void Fixed_and_smart_size_modes()
    {
        var (tree, conn) = Simple(s => { s.Rdp.ResolutionMode = RdpResolutionMode.Fixed; s.Rdp.DesktopWidth = 1920; s.Rdp.DesktopHeight = 1080; });
        var plan = RdpPlanBuilder.Build(conn, tree.Vault);
        Assert.Equal((1920, 1080), (plan.FixedWidth, plan.FixedHeight));
        Assert.False(plan.DynamicResolution);

        conn.Settings.Rdp.ResolutionMode = RdpResolutionMode.SmartSize;
        plan = RdpPlanBuilder.Build(conn, tree.Vault);
        Assert.Equal(true, Value(plan, RdpScope.Advanced, "SmartSizing"));
        Assert.False(plan.DynamicResolution);
    }

    [Fact]
    public void Inherited_mRemoteNG_settings_flow_into_plan()
    {
        var tree = new ConfConsImporter().ImportFile(Fixture.PathOf("confCons_v2_6.xml")).Tree;
        var conn = (ConnectionNode)tree.FindByPath("Folder2/Folder2.2/InheritUsernameConnection")!;
        conn.Hostname = "srv";
        var plan = RdpPlanBuilder.Build(conn, tree.Vault);
        var c = CredentialResolver.Resolve(conn, tree.Vault);
        Assert.Equal((c.Username, c.Domain, c.Password), (plan.UserName, plan.Domain, plan.Password));
        Assert.Equal("userFolder2", plan.UserName);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0xB08, true)]
    [InlineData(0x904, false)]
    public void Normal_disconnects(int reason, bool normal) => Assert.Equal(normal, RdpPlanBuilder.IsNormalDisconnect(reason));
}
