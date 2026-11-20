using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Protocols.Rdp;

namespace HostDeck.Presentation.ViewModels;

/// <summary>
/// RDP-Session eines Tabs. Das ActiveX-Control lebt in der Windows-View; dieses ViewModel liefert den
/// geprüften <see cref="RdpPlan"/> und nimmt Zustandsmeldungen der View entgegen.
/// </summary>
public sealed partial class RdpSessionViewModel(SessionTabViewModel tab) : ObservableObject
{
    public SessionTabViewModel Tab { get; } = tab;

    /// <summary>Letzte Fehlermeldung (für die Anzeige im Tab).</summary>
    [ObservableProperty] private string? _message;

    /// <summary>Die View soll (neu) verbinden.</summary>
    public event EventHandler? ConnectRequested;
    /// <summary>Die View soll trennen.</summary>
    public event EventHandler? DisconnectRequested;
    /// <summary>Die View soll in den Vollbildmodus wechseln.</summary>
    public event EventHandler? FullScreenRequested;

    /// <exception cref="RdpPlanException">Unvollständige Verbindung (z. B. kein Hostname).</exception>
    public RdpPlan CreatePlan(int dpiScalePercent) =>
        RdpPlanBuilder.Build(Tab.Connection, Tab.Settings, Tab.Credentials, dpiScalePercent);

    [RelayCommand] private void Connect() => ConnectRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Disconnect() => DisconnectRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void FullScreen() => FullScreenRequested?.Invoke(this, EventArgs.Empty);

    // ---- Rückmeldungen der View -----------------------------------------------------------------------

    public void ReportConnecting()
    {
        Tab.State = SessionState.Connecting;
        Message = $"Verbinde mit {Tab.Connection.Hostname} …";
    }

    public void ReportConnected()
    {
        Tab.State = SessionState.Connected;
        Message = null;
    }

    public void ReportDisconnected(int reason, string? description)
    {
        var normal = RdpPlanBuilder.IsNormalDisconnect(reason);
        Tab.State = normal ? SessionState.Disconnected : SessionState.Failed;
        Message = normal ? "Verbindung beendet." : $"Verbindung getrennt: {description ?? $"Code 0x{reason:X}"}";
    }

    /// <summary>Tab wurde in ein anderes Fenster verschoben – die neue Ansicht verbindet neu.</summary>
    public void ReportMoved()
    {
        Tab.State = SessionState.Disconnected;
        Message = "Wird im neuen Fenster neu verbunden …";
    }

    public void ReportError(string message)
    {
        Tab.State = SessionState.Failed;
        Message = message;
    }
}
