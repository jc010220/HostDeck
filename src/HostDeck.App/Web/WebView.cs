using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HostDeck.Presentation.ViewModels;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.Wpf.WebView2;

namespace HostDeck.App.Web;

/// <summary>
/// HTTP/HTTPS-Tab: WebView2 mit Adressleiste, automatischer Basic/NTLM-Anmeldung (einmal pro Tab)
/// und bewusster Entscheidung bei Zertifikatsfehlern (typisch für Geräte-Weboberflächen).
/// </summary>
public sealed class WebView : DockPanel
{
    private static Task<CoreWebView2Environment>? _environment;
    private readonly TextBox _address = new() { VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    private WebView2Control? _web;
    private WebSessionViewModel? _vm;

    public WebView()
    {
        var bar = new DockPanel { Margin = new Thickness(4) };
        void Btn(string text, string tip, Action click, Dock dock = Dock.Left)
        {
            var b = new Wpf.Ui.Controls.Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 2, 0), Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent };
            b.Click += (_, _) => click();
            DockPanel.SetDock(b, dock);
            bar.Children.Add(b);
        }
        Btn("←", "Back", () => { if (_web?.CanGoBack == true) _web.GoBack(); });
        Btn("→", "Forward", () => { if (_web?.CanGoForward == true) _web.GoForward(); });
        Btn("⟳", "Reload", () => _web?.Reload());
        Btn("↗", "Open in default browser", () =>
        {
            if (Uri.TryCreate(_address.Text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.ToString()) { UseShellExecute = true });
        }, Dock.Right);
        bar.Children.Add(_address);
        _address.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _web?.CoreWebView2 is null) return;
            var text = _address.Text.Trim();
            if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") _web.CoreWebView2.Navigate(uri.ToString());
        };
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        DataContextChanged += (_, _) => _ = InitializeAsync();
        Unloaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { if (!IsLoaded) Release(); });
    }

    private static Task<CoreWebView2Environment> Environment => _environment ??= CoreWebView2Environment.CreateAsync(
        null, Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "HostDeck", "WebView2-Web"));

    private async Task InitializeAsync()
    {
        if (_web is not null || DataContext is not SessionTabViewModel { Web: { } vm } tab) return;
        _vm = vm;
        tab.Closed += (_, _) => Release();
        vm.CancelRequested += (_, _) => _web?.CoreWebView2?.Stop();
        _web = new WebView2Control();
        Children.Add(_web);
        try
        {
            await _web.EnsureCoreWebView2Async(await Environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            Children.Remove(_web);
            Children.Add(new TextBlock { Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap, Text = "Microsoft Edge WebView2 Runtime is missing.\n" + ex.Message });
            _web = null;
            return;
        }

        var core = _web.CoreWebView2;
        // Abschottung: Eine Webseite im Tab darf nicht mit HostDeck sprechen – kein WebMessage-Kanal, keine Host-Objekte,
        // keine Entwicklerwerkzeuge. (Eigene WebView2-Umgebung/Profil „WebView2-Web“, getrennt vom Terminal.)
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;      // Passwörter gehören in den HostDeck-Vault
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsWebUri(e.Uri)) core.Navigate(e.Uri);
        };
        // Nur http(s) im Tab; file:, ms-…:, andere Protokoll-Handler würden lokale Programme starten
        core.NavigationStarting += (_, e) => { if (!IsWebUri(e.Uri) && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.BasicAuthenticationRequested += (_, e) =>
        {
            if (vm.CredentialsUsed || vm.Username is null) return; // sonst fragt der Browser selbst
            // Gespeicherte Zugangsdaten nur an das eingetragene Gerät – nie an einen Server, auf den umgeleitet wurde
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
                || !string.Equals(target.Host, vm.StartUri.Host, StringComparison.OrdinalIgnoreCase)
                || target.Port != vm.StartUri.Port || target.Scheme != vm.StartUri.Scheme)
                return;
            if (target.Scheme == Uri.UriSchemeHttp)
            {
                // HTTP Basic überträgt das Passwort lesbar – nur nach ausdrücklicher Zustimmung
                var deferral = e.GetDeferral();
                var username = vm.Username;
                var password = vm.Password ?? "";
                Dispatcher.BeginInvoke(() =>
                {
                    using (deferral)
                    {
                        if (vm.ConfirmPlaintextCredentials?.Invoke() != true) return; // Browser fragt selbst
                        vm.CredentialsUsed = true;
                        e.Response.UserName = username;
                        e.Response.Password = password;
                    }
                });
                return;
            }
            vm.CredentialsUsed = true;
            e.Response.UserName = vm.Username;
            e.Response.Password = vm.Password ?? "";
        };
        core.ServerCertificateErrorDetected += OnCertificateError;
        core.SourceChanged += (_, _) => _address.Text = core.Source;
        core.NavigationCompleted += (_, e) => vm.ReportNavigated(core.Source, e.IsSuccess, core.DocumentTitle);
        vm.Tab.State = SessionState.Connecting;
        core.Navigate(vm.StartUri.ToString());
    }

    private static bool IsWebUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        if (_vm is null) return;
        var uri = new Uri(e.RequestUri);
        var host = uri.Host;
        string fingerprint;
        try
        {
            using var x509 = e.ServerCertificate.ToX509Certificate2();
            fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(x509.RawData));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel; // ohne prüfbares Zertifikat keine Ausnahme
            return;
        }
        // Ausnahme gilt nur für genau dieses Zertifikat an genau diesem Host und Port
        var key = $"{host}:{uri.Port}:{fingerprint}";
        if (WebSessionViewModel.AcceptedCertificates.Contains(key))
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            return;
        }
        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(() =>
        {
            using (deferral)
            {
                var cert = e.ServerCertificate;
                var message = $"The certificate of {host} is not trusted ({e.ErrorStatus}).\n\n" +
                              $"Issued to: {cert.Subject}\nIssuer: {cert.Issuer}\nValid until: {cert.ValidTo:d}\n" +
                              $"SHA-256: {fingerprint}\n\n" +
                              "This is common for devices with a self-signed certificate (switch, firewall, iLO/iDRAC). " +
                              "For public websites it can be an attack.\n\n" +
                              "Continue anyway? The exception applies to exactly this certificate on this host and port, " +
                              "in all web tabs until HostDeck is closed. It is never saved.";
                var ok = System.Windows.MessageBox.Show(Window.GetWindow(this)!, message, "Certificate warning",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
                if (ok) WebSessionViewModel.AcceptedCertificates.Add(key);
                e.Action = ok ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow : CoreWebView2ServerCertificateErrorAction.Cancel;
            }
        });
    }

    private void Release()
    {
        if (_web is null) return;
        Children.Remove(_web);
        _web.Dispose();
        _web = null;
        _vm = null;
    }
}
