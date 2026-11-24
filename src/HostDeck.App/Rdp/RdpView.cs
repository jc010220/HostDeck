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

    private static readonly List<WeakReference<RdpView>> Instances = [];

    /// <summary>Alle RDP-Sitzungen aus dem Vollbild holen (z. B. vor dem Sperren – das Vollbild ist ein eigenes natives Fenster).</summary>
    public static void LeaveFullScreenAll()
    {
        lock (Instances)
        {
            Instances.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Instances)
                if (weak.TryGetTarget(out var view) && view._host is not null && view._connected)
                    Dispatch.TrySet(view._host.Ocx, "FullScreen", false);
        }
    }

    public RdpView()
    {
        lock (Instances) Instances.Add(new WeakReference<RdpView>(this));
        _formsHost.Child = _panel;
        var connectButton = new Wpf.Ui.Controls.Button { Content = "Connect", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, HorizontalAlignment = HorizontalAlignment.Center };
        connectButton.Click += (_, _) => Connect();
        _statusPanel = new Border
        {
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { _statusText, connectButton } },
        };
        Children.Add(_formsHost);
        Children.Add(_statusPanel);
        ShowStatus("Ready.");

        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); UpdateResolution(); };
        _panel.SizeChanged += (_, _) => { if (_connected) { _resizeTimer.Stop(); _resizeTimer.Start(); } };
        DataContextChanged += (_, _) => Attach();
        // Auto-Verbinden beim ersten Anzeigen – auch nach Verschieben in ein anderes Fenster. Hintergrund-Priorität,
        // damit die alte Ansicht (Unloaded) ihr Control vorher abbaut.
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_vm is not null && _host is null && !_everConnected && IsLoaded && _vm.Tab.State != SessionState.Failed) Connect();
        });
        Unloaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsLoaded || _host is null) return;
            Teardown();               // ActiveX nicht verwaist weiterlaufen lassen
            _vm?.ReportMoved();
        });
        IsVisibleChanged += (_, _) => { if (IsVisible && _connected) _host?.Focus(); };
    }

    private bool _everConnected;

    private void Attach()
    {
        if (_vm is not null || DataContext is not SessionTabViewModel { Rdp: { } vm } tab) return;
        _vm = vm;
        vm.ConnectRequested += (_, _) => Connect();
        vm.DisconnectRequested += (_, _) => Disconnect();
        vm.CancelRequested += (_, _) => Safe("Cancel", CancelConnect);
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
        _cancelled = false;
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
            const string msg = "The Windows RDP client (mstscax.dll) is not available.";
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
        catch (Exception ex)
        {
            HostDeck.App.Services.Log.Error($"RDP connection start {_plan?.Server}", ex);
            var msg = $"RDP connection could not be started (client version {version}): {(ex.InnerException ?? ex).Message}";
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
        Dispatch.TrySet(ocx, "ConnectingText", $"Connecting to {plan.Server} …");

        var (width, height) = DesktopSize(plan);
        Dispatch.Set(ocx, "DesktopWidth", width);
        Dispatch.Set(ocx, "DesktopHeight", height);

        var advanced = FirstAvailable(ocx, "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6", "AdvancedSettings5");
        var secured = FirstAvailable(ocx, "SecuredSettings3", "SecuredSettings2");
        var transport = FirstAvailable(ocx, "TransportSettings4", "TransportSettings3", "TransportSettings2", "TransportSettings");

        foreach (var p in plan.Properties)
        {
            var applied = p.Scope switch
            {
                RdpScope.Client => Dispatch.TrySet(ocx, p.Name, p.Value),
                RdpScope.Advanced => advanced is not null && Dispatch.TrySet(advanced, p.Name, p.Value),
                RdpScope.Secured => secured is not null && Dispatch.TrySet(secured, p.Name, p.Value),
                RdpScope.Transport => transport is not null && Dispatch.TrySet(transport, p.Name, p.Value),
                RdpScope.Extended => TrySetExtended(ocx, p.Name, p.Value),
                _ => false,
            };
            // Ausdrücklich angeforderte Schutzoptionen dürfen nicht still entfallen ⇒ lieber nicht verbinden
            if (!applied && SecurityCritical.Contains(p.Name))
                throw new InvalidOperationException(
                    $"The security option “{p.Name}” could not be enabled on this Windows RDP client, so the connection was not started. " +
                    "Update Windows/the RDP client or turn the option off in the connection's properties.");
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

    /// <summary>Optionen, deren Ausfall die Sicherheit senkt (Zertifikatsprüfung, Restricted Admin, Remote Credential Guard).</summary>
    private static readonly HashSet<string> SecurityCritical = new(StringComparer.Ordinal)
    {
        "AuthenticationLevel", "RestrictedLogon", "DisableCredentialsDelegation", "RedirectedAuthentication",
    };

    private static bool TrySetExtended(object ocx, string name, object value)
    {
        try { ((IMsRdpExtendedSettings)ocx).SetProperty(name, ref value); return true; }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { return false; } // ältere Clients
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
        if (plan.ResolutionMode == RdpResolutionMode.Fullscreen || _fullScreen)
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

    /// <summary>Event-Handler des ActiveX nie mit einer Ausnahme verlassen (COM-Rückruf ⇒ sonst Absturzgefahr).</summary>
    private void Safe(string context, Action action)
    {
        try { action(); }
        catch (Exception ex) { HostDeck.App.Services.Log.Error($"RDP {context}", ex); }
    }

    /// <summary>Gerade im Vollbild (Vollbild-Button, Strg+Alt+Pause) – Auflösung dann auf den ganzen Monitor.</summary>
    private bool _fullScreen;

    /// <summary>
    /// Vollbild betreten/verlassen: Das Control vergrößert nur sein Fenster, die Sitzung behält die alte Auflösung (kleines
    /// Bild mit schwarzem Rand). Daher die Auflösung an den Monitor bzw. zurück an den Tab anpassen – oder, wenn der Server
    /// das nicht kann bzw. eine feste Auflösung eingestellt ist, für die Dauer des Vollbilds skalieren.
    /// </summary>
    private void OnFullScreenChanged(bool fullScreen)
    {
        _fullScreen = fullScreen;
        if (_host is null || _plan is null || !_connected) return;
        var advanced = FirstAvailable(_host.Ocx, "AdvancedSettings9", "AdvancedSettings2") ?? _host.Ocx;
        if (_plan.DynamicResolution)
        {
            // kurz warten, bis das Vollbildfenster seine endgültige Größe hat
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _resizeTimer.Stop(); _resizeTimer.Start(); });
        }
        else
        {
            Dispatch.TrySet(advanced, "SmartSizing", fullScreen || _plan.ResolutionMode == RdpResolutionMode.SmartSize);
        }
    }

    private void WireEvents(RdpAxHost host)
    {
        host.EnteredFullScreen += () => Safe("EnterFullScreen", () => OnFullScreenChanged(true));
        host.LeftFullScreen += () => Safe("LeaveFullScreen", () => OnFullScreenChanged(false));
        host.Connecting += () => Safe("Connecting", () => ApplyCustomDrives(host));
        host.LoginComplete += () => Safe("LoginComplete", () => { _connected = true; _vm?.ReportConnected(); });
        host.Connected += () => Safe("Connected", () => { _connected = true; _vm?.ReportConnected(); });
        host.AutoReconnecting += (_, _) => Safe("AutoReconnecting", () => _vm?.ReportConnecting());
        host.AutoReconnected += () => Safe("AutoReconnected", () => _vm?.ReportConnected());
        host.Disconnected += reason => Safe("Disconnected", () =>
        {
            _connected = false;
            string? description = null;
            // Normale Trennung (lokal, in der Sitzung abgemeldet) ⇒ keine Fehlerbeschreibung; mstscax liefert dafür sonst
            // irreführend „An internal error has occurred“
            if (!HostDeck.Protocols.Rdp.RdpPlanBuilder.IsNormalDisconnect(reason)) try
            {
                var extended = Convert.ToInt64(Dispatch.Get(host.Ocx, "ExtendedDisconnectReason") ?? 0) & 0xFFFFFFFF;
                description = Dispatch.Call(host.Ocx, "GetErrorDescription", unchecked((uint)reason), (uint)extended) as string;
            }
            catch (Exception ex) { HostDeck.App.Services.Log.Error($"RDP GetErrorDescription (reason {reason})", ex); }
            HostDeck.App.Services.Log.Info($"RDP disconnected: {_plan?.Server} reason={reason}  {description}");
            if (_cancelled)
            {
                _cancelled = false; // vom Benutzer abgebrochen – Meldung „abgebrochen“ beibehalten
                return;
            }
            _vm?.ReportDisconnected(reason, description);
            var window = Window.GetWindow(this);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => HostDeck.App.Behaviors.FocusRecovery.Restore(window));
            // WICHTIG: Das Control hier NICHT zerstören – mstscax verarbeitet die Trennung noch (nativer Absturz).
            // Es wird nur ausgeblendet und erst beim nächsten Verbinden bzw. Schließen des Tabs (verzögert) freigegeben.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Safe("Status", () => ShowStatus(_vm?.Message ?? "Connection closed.")));
        });
        host.FatalError += code => Safe("FatalError", () =>
        {
            _connected = false;
            HostDeck.App.Services.Log.Info($"RDP fatal error: {_plan?.Server} Code={code}");
            _vm?.ReportError($"Fatal RDP error (code {code}).");
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Safe("Status", () => ShowStatus(_vm?.Message ?? "")));
        });
        host.LogonError += code => Safe("LogonError", () => HostDeck.App.Services.Log.Info($"RDP logon error: {_plan?.Server} Code={code}"));
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

    private bool _cancelled;

    /// <summary>
    /// Verbindungsaufbau abbrechen: Disconnect beendet auch einen laufenden Aufbau. Kommt keine Trennungsmeldung,
    /// wird das Control nach kurzer Zeit verzögert abgebaut.
    /// </summary>
    private void CancelConnect()
    {
        if (_host is null) return;
        _cancelled = true;
        _connected = false;
        try { Dispatch.Call(_host.Ocx, "Disconnect"); }
        catch (Exception ex) { HostDeck.App.Services.Log.Error("RDP cancel", ex); }
        ShowStatus("Connecting cancelled.");
        var host = _host;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_host == host && !_connected) Teardown(); // keine Trennungsmeldung erhalten
        };
        timer.Start();
    }

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
        var window = Window.GetWindow(this);
        var host = _host;
        _host = null;
        try
        {
            if (Convert.ToInt32(Dispatch.Get(host.Ocx, "Connected")) != 0) Dispatch.Call(host.Ocx, "Disconnect");
        }
        catch (Exception ex) { HostDeck.App.Services.Log.Error("RDP disconnect during teardown", ex); }

        // Control erst freigeben, wenn die Anwendung untätig ist (nie im Rückruf des Controls selbst).
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                _panel.Controls.Remove(host);
                host.Dispose();
            }
            catch (Exception ex) { HostDeck.App.Services.Log.Error("RDP control release", ex); }
            // Das RDP-Fenster hatte den Tastaturfokus – ohne Zurückholen kämen keine Tasten (Strg+K …) mehr an
            HostDeck.App.Behaviors.FocusRecovery.Restore(window ?? Application.Current?.MainWindow);
        });
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
