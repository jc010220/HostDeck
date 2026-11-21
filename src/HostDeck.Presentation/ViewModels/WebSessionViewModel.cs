using CommunityToolkit.Mvvm.ComponentModel;
using HostDeck.Core.Model;

namespace HostDeck.Presentation.ViewModels;

/// <summary>HTTP/HTTPS-Tab (WebView2): Adresse, Zugangsdaten für Basic/NTLM, Zertifikatsentscheidung.</summary>
public sealed partial class WebSessionViewModel(SessionTabViewModel tab) : ObservableObject
{
    public SessionTabViewModel Tab { get; } = tab;

    public Uri StartUri { get; } = BuildUri(tab.Protocol, tab.Connection.Hostname, tab.Settings.Port);

    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string? _pageTitle;

    /// <summary>Zugangsdaten nur einmal automatisch senden – bei Ablehnung fragt der Browser selbst.</summary>
    public bool CredentialsUsed { get; set; }

    /// <summary>
    /// Akzeptierte ungültige Zertifikate in diesem Tab (nicht dauerhaft) – gebunden an Host, Port UND das konkrete
    /// Zertifikat (Fingerabdruck), damit ein anderes Zertifikat desselben Hosts erneut nachfragt.
    /// </summary>
    public HashSet<string> AcceptedCertificates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Zustimmung für das Senden gespeicherter Zugangsdaten über unverschlüsseltes HTTP (gesetzt vom MainViewModel).</summary>
    public Func<bool>? ConfirmPlaintextCredentials { get; init; }

    public string? Username => string.IsNullOrEmpty(Tab.Credentials.Username) ? null
        : string.IsNullOrEmpty(Tab.Credentials.Domain) ? Tab.Credentials.Username : $"{Tab.Credentials.Domain}\\{Tab.Credentials.Username}";
    public string? Password => string.IsNullOrEmpty(Tab.Credentials.Password) ? null : Tab.Credentials.Password;

    public event EventHandler? CancelRequested;

    public void CancelConnect()
    {
        if (Tab.State != SessionState.Connecting) return;
        CancelRequested?.Invoke(this, EventArgs.Empty);
        Tab.State = SessionState.Disconnected;
    }

    public void ReportNavigated(string url, bool success, string? title)
    {
        Address = url;
        PageTitle = title;
        if (!string.IsNullOrEmpty(title)) Tab.Title = $"{Tab.Connection.Name} – {title}";
        Tab.State = success ? SessionState.Connected : SessionState.Failed;
    }

    /// <summary>
    /// Hostname kann eine vollständige URL sein („https://fw01/admin“) oder nur ein Host.
    /// Standardports werden weggelassen.
    /// </summary>
    public static Uri BuildUri(ProtocolKind protocol, string hostname, int? port)
    {
        var host = hostname.Trim();
        if (Uri.TryCreate(host, UriKind.Absolute, out var full) && full.Scheme is "http" or "https")
            return full;

        var scheme = protocol == ProtocolKind.Http ? "http" : "https";
        var builder = new UriBuilder(scheme, host.Split('/')[0]);
        var defaultPort = scheme == "http" ? 80 : 443;
        builder.Port = port is { } p && p != defaultPort ? p : -1;
        var slash = host.IndexOf('/');
        if (slash > 0) builder.Path = host[slash..];
        return builder.Uri;
    }
}
