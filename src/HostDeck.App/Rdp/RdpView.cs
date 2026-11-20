using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;
using HostDeck.Protocols.Rdp;
using WinForms = System.Windows.Forms;

namespace HostDeck.App.Rdp;

/// <summary>
/// RDP-Session im Tab: natives ActiveX in WindowsFormsHost. Status/Fehler werden <i>statt</i> des
/// Controls angezeigt (WPF kann nicht über HWND-Inhalte zeichnen).
/// </summary>
public sealed class RdpView : Grid
{
    private readonly WindowsFormsHost _formsHost = new();
    private readonly WinForms.Panel _panel = new() { Dock = WinForms.DockStyle.Fill, BackColor = System.Drawing.Color.Black };
    private readonly Border _statusPanel;
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 16) };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private RdpAxHost? _host;
    private RdpPlan? _plan;
    private RdpSessionViewModel? _vm;
    private bool _connected;

    public RdpView()
    {
        _formsHost.Child = _panel;
        var connectButton = new Wpf.Ui.Controls.Button { Content = "Verbinden", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, HorizontalAlignment = HorizontalAlignment.Center };
        connectButton.Click += (_, _) => Connect();
        _statusPanel = new Border
        {
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { _statusText, connectButton } },
        };
        Children.Add(_formsHost);
        Children.Add(_statusPanel);
        ShowStatus("Bereit.");

        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); UpdateResolution(); };
        _panel.SizeChanged += (_, _) => { if (_connected) { _resizeTimer.Stop(); _resizeTimer.Start(); } };
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => { if (_vm is not null && _host is null && _vm.Tab.State == SessionState.Disconnected && !_everConnected) Connect(); };
        IsVisibleChanged += (_, _) => { if (IsVisible && _connected) _host?.Focus(); };
    }

    private bool _everConnected;

    private void Attach()
    {
        if (_vm is not null || DataContext is not SessionTabViewModel { Rdp: { } vm } tab) return;
        _vm = vm;
        vm.ConnectRequested += (_, _) => Connect();
        vm.DisconnectRequested += (_, _) => Disconnect();
        vm.FullScreenRequested += (_, _) => { if (_host is not null && _connected) Dispatch.TrySet(_host.Ocx, "FullScreen", true); };
        tab.Closed += (_, _) => Teardown();
    }

    private int DpiPercent => (int)Math.Round(VisualTreeHelper.GetDpi(this).DpiScaleX * 100);

    // ---- Verbinden ----------------------------------------------------------------------------------

    private void Connect()
    {
        if (_vm is null) return;
        Teardown();
        // Erst einblenden und Layout abwarten: das Panel braucht seine echte Größe (Startauflösung)
        // und der WindowsFormsHost ein Fenster-Handle, bevor das ActiveX erzeugt wird.
        ShowHost();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ConnectCore);
    }

    private void ConnectCore()
    {
        if (_vm is null) return;
        try
        {
            _plan = _vm.CreatePlan(DpiPercent);
        }
        catch (RdpPlanException ex)
        {
            _vm.ReportError(ex.Message);
            ShowStatus(ex.Message);
            return;
        }

        _host = CreateHost(out var version);
        if (_host is null)
        {
            const string msg = "Der Windows-RDP-Client (mstscax.dll) ist nicht verfügbar.";
            _vm.ReportError(msg);
            ShowStatus(msg);
            return;
        }

        try
        {
            Apply(_host, _plan);
            ShowHost();
            _vm.ReportConnecting();
            _everConnected = true;
            Dispatch.Call(_host.Ocx, "Connect");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or System.Reflection.TargetInvocationException)
        {
            var msg = $"RDP-Verbindung konnte nicht gestartet werden (Client-Version {version}): {(ex.InnerException ?? ex).Message}";
            _vm.ReportError(msg);
            ShowStatus(msg);
            Teardown();
        }
    }

    /// <summary>Neueste verfügbare Control-Version erzeugen (11 → 7).</summary>
    private RdpAxHost? CreateHost(out int version)
    {
        foreach (var (v, clsid) in RdpClsid.Candidates)
        {
            var host = new RdpAxHost(clsid) { Dock = WinForms.DockStyle.Fill };
            try
            {
                _panel.Controls.Add(host);
                host.CreateControl();
                _ = host.Ocx;
                WireEvents(host);
                version = v;
                return host;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or System.IO.FileNotFoundException
                                           or WinForms.AxHost.InvalidActiveXStateException)
            {
                _panel.Controls.Remove(host);
                host.Dispose();
            }
        }
        version = 0;
        return null;
    }

    private void Apply(RdpAxHost host, RdpPlan plan)
    {
        var ocx = host.Ocx;
        Dispatch.Set(ocx, "Server", plan.Server);
        Dispatch.Set(ocx, "UserName", plan.UserName);
        Dispatch.Set(ocx, "Domain", plan.Domain);
        Dispatch.TrySet(ocx, "ConnectingText", $"Verbinde mit {plan.Server} …");

        var (width, height) = DesktopSize(plan);
        Dispatch.Set(ocx, "DesktopWidth", width);
        Dispatch.Set(ocx, "DesktopHeight", height);

        var advanced = FirstAvailable(ocx, "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6", "AdvancedSettings5");
        var secured = FirstAvailable(ocx, "SecuredSettings3", "SecuredSettings2");
        var transport = FirstAvailable(ocx, "TransportSettings4", "TransportSettings3", "TransportSettings2", "TransportSettings");

        foreach (var p in plan.Properties)
        {
            switch (p.Scope)
            {
                case RdpScope.Client: Dispatch.TrySet(ocx, p.Name, p.Value); break;
                case RdpScope.Advanced when advanced is not null: Dispatch.TrySet(advanced, p.Name, p.Value); break;
                case RdpScope.Secured when secured is not null: Dispatch.TrySet(secured, p.Name, p.Value); break;
                case RdpScope.Transport when transport is not null: Dispatch.TrySet(transport, p.Name, p.Value); break;
                case RdpScope.Extended:
                    try { var value = p.Value; ((IMsRdpExtendedSettings)ocx).SetProperty(p.Name, ref value); }
                    catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { /* ältere Clients */ }
                    break;
            }
        }

        if (ocx is IMsRdpClientNonScriptable5 ns)
        {
            var n = plan.NonScriptable;
            ns.SetPromptForCredentials(n.PromptForCredentials);
            ns.SetNegotiateSecurityLayer(n.NegotiateSecurityLayer);
            ns.SetEnableCredSspSupport(n.EnableCredSspSupport);
            ns.SetRedirectDynamicDrives(n.RedirectDynamicDrives);
            ns.SetShowRedirectionWarningDialog(n.ShowRedirectionWarningDialog);
            ns.SetWarnAboutSendingCredentials(n.WarnAboutSendingCredentials);
            ns.SetWarnAboutClipboardRedirection(n.WarnAboutClipboardRedirection);
        }

        // Passwort ausschließlich über die nicht-skriptbare Schnittstelle (nie in .rdp-Dateien/Logs)
        if (plan.Password is { } password)
            ((IMsTscNonScriptable)ocx).SetClearTextPassword(password);
    }

    private static object? FirstAvailable(object ocx, params string[] names)
    {
        foreach (var name in names)
        {
            try { if (Dispatch.Get(ocx, name) is { } obj) return obj; }
            catch (Exception ex) when (ex is MissingMethodException or COMException or System.Reflection.TargetInvocationException) { }
        }
        return null;
    }

    private (int Width, int Height) DesktopSize(RdpPlan plan)
    {
        if (plan.ResolutionMode == RdpResolutionMode.Fixed && plan.FixedWidth is > 0 && plan.FixedHeight is > 0)
            return (plan.FixedWidth.Value, plan.FixedHeight.Value);
        if (plan.ResolutionMode == RdpResolutionMode.Fullscreen)
        {
            var screen = WinForms.Screen.FromControl(_panel).Bounds;
            return (screen.Width, screen.Height);
        }
        // Größe des Tab-Bereichs in physischen Pixeln (WinForms ist PerMonitorV2 ⇒ echte Pixel)
        var size = _panel.ClientSize;
        return (Math.Clamp(size.Width, 640, 8192) & ~1, Math.Clamp(size.Height, 480, 8192)); // gerade Breite
    }

    /// <summary>Auflösung live nachführen (IMsRdpClient9+ UpdateSessionDisplaySettings).</summary>
    private void UpdateResolution()
    {
        if (_host is null || _plan is not { DynamicResolution: true } plan || !_connected) return;
        var (w, h) = DesktopSize(plan);
        var dpi = Math.Max(VisualTreeHelper.GetDpi(this).PixelsPerInchX, 96);
        try
        {
            Dispatch.Call(_host.Ocx, "UpdateSessionDisplaySettings",
                (uint)w, (uint)h, (uint)(w / dpi * 25.4), (uint)(h / dpi * 25.4), 0u, (uint)plan.DesktopScaleFactor, (uint)plan.DeviceScaleFactor);
        }
        catch (Exception ex) when (ex is MissingMethodException or COMException or System.Reflection.TargetInvocationException)
        {
            // Älterer Client oder Server ohne Display-Update ⇒ ab jetzt skalieren statt neu verbinden
            Dispatch.TrySet(FirstAvailable(_host.Ocx, "AdvancedSettings9", "AdvancedSettings2") ?? _host.Ocx, "SmartSizing", true);
        }
    }

    // ---- Events ------------------------------------------------------------------------------------

    private void WireEvents(RdpAxHost host)
    {
        host.Connecting += () => ApplyCustomDrives(host);
        host.LoginComplete += () => { _connected = true; _vm?.ReportConnected(); };
        host.Connected += () => { _connected = true; _vm?.ReportConnected(); };
        host.AutoReconnecting += (_, attempt) => _vm?.ReportConnecting();
        host.AutoReconnected += () => _vm?.ReportConnected();
        host.Disconnected += reason =>
        {
            _connected = false;
            string? description = null;
            try
            {
                var extended = Convert.ToUInt32(Dispatch.Get(host.Ocx, "ExtendedDisconnectReason") ?? 0);
                description = Dispatch.Call(host.Ocx, "GetErrorDescription", (uint)reason, extended) as string;
            }
            catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or InvalidCastException) { }
            _vm?.ReportDisconnected(reason, description);
            // Control abbauen (nicht im Event-Handler selbst – COM-Reentrancy)
            Dispatcher.BeginInvoke(() => { Teardown(); ShowStatus(_vm?.Message ?? "Verbindung beendet."); });
        };
        host.FatalError += code =>
        {
            _connected = false;
            _vm?.ReportError($"Schwerer RDP-Fehler (Code {code}).");
            Dispatcher.BeginInvoke(() => { Teardown(); ShowStatus(_vm?.Message ?? ""); });
        };
    }

    private void ApplyCustomDrives(RdpAxHost host)
    {
        if (_plan is not { RedirectDriveLetters.Count: > 0 } plan || host.Ocx is not IMsRdpClientNonScriptable5 ns) return;
        try
        {
            var drives = ns.GetDriveCollection();
            var count = Convert.ToInt32(Dispatch.Get(drives, "DriveCount"));
            for (var i = 0; i < count; i++)
            {
                var drive = drives.GetType().InvokeMember("DriveByIndex", System.Reflection.BindingFlags.GetProperty, null, drives, [(uint)i])!;
                var name = (Dispatch.Get(drive, "Name") as string ?? "").TrimEnd(':', '\\', ' ').ToUpperInvariant();
                if (plan.RedirectDriveLetters.Contains(name)) Dispatch.TrySet(drive, "RedirectionState", true);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or System.Reflection.TargetInvocationException or MissingMethodException) { }
    }

    // ---- Trennen / Aufräumen -----------------------------------------------------------------------

    private void Disconnect()
    {
        if (_host is null) return;
        try { if (Convert.ToInt32(Dispatch.Get(_host.Ocx, "Connected")) != 0) Dispatch.Call(_host.Ocx, "Disconnect"); }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or InvalidOperationException) { Teardown(); }
    }

    private void Teardown()
    {
        _resizeTimer.Stop();
        _connected = false;
        if (_host is null) return;
        var host = _host;
        _host = null;
        try { if (Convert.ToInt32(Dispatch.Get(host.Ocx, "Connected")) != 0) Dispatch.Call(host.Ocx, "Disconnect"); }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or InvalidOperationException) { }
        _panel.Controls.Remove(host);
        host.Dispose();
    }

    private void ShowHost()
    {
        _formsHost.Visibility = Visibility.Visible;
        _statusPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string text)
    {
        _statusText.Text = text;
        _formsHost.Visibility = Visibility.Collapsed;
        _statusPanel.Visibility = Visibility.Visible;
    }
}
