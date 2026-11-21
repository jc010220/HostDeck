using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostDeck.Presentation.Services;
using HostDeck.Protocols.Vnc;

namespace HostDeck.Presentation.ViewModels;

/// <summary>VNC-Session eines Tabs (eigener RFB-Client). Die View zeichnet <see cref="Client"/>.Framebuffer.</summary>
public sealed partial class VncSessionViewModel(SessionTabViewModel tab, IDialogService dialogs) : ObservableObject, IAsyncDisposable
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public SessionTabViewModel Tab { get; } = tab;
    public bool ViewOnly => Tab.Settings.Vnc.ViewOnly == true;

    [ObservableProperty] private RfbClient? _client;
    [ObservableProperty] private string? _message;
    private CancellationTokenSource? _connectCts;

    public void CancelConnect()
    {
        if (Tab.State != SessionState.Connecting) return;
        try { _connectCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        if (Tab.State is SessionState.Connecting or SessionState.Connected) return;
        await DisposeAsync();
        var host = Tab.Connection.Hostname;
        var port = Tab.Settings.Port ?? 5900;
        Tab.State = SessionState.Connecting;
        Message = $"Connecting to {host}:{port} …";

        var client = new RfbClient { ViewOnly = ViewOnly };
        using var cts = new CancellationTokenSource();
        _connectCts = cts;
        client.Closed += reason => Post(() =>
        {
            if (Client != client) return;
            Tab.State = SessionState.Disconnected;
            Message = "Connection closed" + (reason is null ? "." : $": {reason}");
            Client = null;
        });
        try
        {
            await client.ConnectAsync(host, port, AskPassword, ct: cts.Token);
            Client = client;
            Tab.State = SessionState.Connected;
            Message = null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await client.DisposeAsync();
            Tab.State = SessionState.Disconnected;
            Message = "Connecting cancelled.";
        }
        catch (Exception ex)
        {
            if (ex is not (VncAuthenticationException or IOException or System.Net.Sockets.SocketException or TimeoutException))
                AppLog.Error($"VNC {host}:{port}", ex);
            await client.DisposeAsync();
            Tab.State = SessionState.Failed;
            Message = ex is VncAuthenticationException ? $"Authentication failed: {ex.Message}" : $"Connection failed: {ex.Message}";
        }
    }

    /// <summary>Gespeichertes Passwort oder Abfrage (läuft ggf. im Netzwerk-Thread ⇒ Dialog im UI-Thread).</summary>
    private string? AskPassword()
    {
        if (!string.IsNullOrEmpty(Tab.Credentials.Password)) return Tab.Credentials.Password;
        string? result = null;
        void Ask() => result = dialogs.Prompt(Tab.Connection.Hostname, "VNC password:", secret: true);
        if (_ui is null || SynchronizationContext.Current == _ui) Ask();
        else _ui.Send(_ => Ask(), null);
        return result;
    }

    [RelayCommand]
    private Task CtrlAltDel() => Client?.SendCtrlAltDelAsync() ?? Task.CompletedTask;

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    public async ValueTask DisposeAsync()
    {
        try { _connectCts?.Cancel(); } catch (ObjectDisposedException) { }
        var client = Client;
        Client = null;
        if (client is not null) await client.DisposeAsync();
    }
}
