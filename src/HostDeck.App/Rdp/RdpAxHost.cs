using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HostDeck.App.Rdp;

/// <summary>WinForms-Hülle für das RDP-ActiveX mit Event-Anbindung (Connection Point).</summary>
internal sealed class RdpAxHost : AxHost
{
    private ConnectionPointCookie? _cookie;

    public RdpAxHost(string clsid) : base(clsid) { }

    public object Ocx => GetOcx() ?? throw new InvalidOperationException("RDP control not created.");

    public event Action? Connecting;
    public event Action? Connected;
    public event Action? LoginComplete;
    public event Action<int>? Disconnected;
    public event Action<int>? FatalError;
    public event Action<int>? Warning;
    public event Action<int>? LogonError;
    public event Action? EnteredFullScreen;
    public event Action? LeftFullScreen;
    public event Action<int, int>? AutoReconnecting;
    public event Action? AutoReconnected;

    protected override void CreateSink()
    {
        try { _cookie = new ConnectionPointCookie(GetOcx()!, new Sink(this), typeof(IMsTscAxEvents)); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { _cookie = null; }
    }

    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
    }

    /// <summary>Muss COM-sichtbar sein, damit das Control die Events per IDispatch zustellen kann.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Sink(RdpAxHost host) : IMsTscAxEvents
    {
        public void OnConnecting() => host.Connecting?.Invoke();
        public void OnConnected() => host.Connected?.Invoke();
        public void OnLoginComplete() => host.LoginComplete?.Invoke();
        public void OnDisconnected(int discReason) => host.Disconnected?.Invoke(discReason);
        public void OnEnterFullScreenMode() => host.EnteredFullScreen?.Invoke();
        public void OnLeaveFullScreenMode() => host.LeftFullScreen?.Invoke();
        public void OnFatalError(int errorCode) => host.FatalError?.Invoke(errorCode);
        public void OnWarning(int warningCode) => host.Warning?.Invoke(warningCode);
        public void OnRemoteDesktopSizeChange(int width, int height) { }
        public void OnAutoReconnecting(int disconnectReason, int attemptCount, ref int pArcContinueStatus) => host.AutoReconnecting?.Invoke(disconnectReason, attemptCount);
        public void OnLogonError(int lError) => host.LogonError?.Invoke(lError);
        public void OnAutoReconnected() => host.AutoReconnected?.Invoke();
    }
}
