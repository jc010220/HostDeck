using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using HostDeck.App.Views;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.App.Services;

/// <summary>
/// Sperre wie bei KeePass: nach einstellbarer Inaktivität (Standard: aus), auf Wunsch zusammen mit Windows (Win+L)
/// oder manuell. Alle HostDeck-Fenster werden ausgeblendet, bis das Master-Passwort eingegeben wird; laufende
/// Sessions bleiben verbunden. Inaktivität = keine Tastatur-/Mauseingabe am PC (GetLastInputInfo).
/// </summary>
public sealed class LockService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Func<string, bool> _verify;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _locked;

    public LockService(MainViewModel vm, Func<string, bool> verify)
    {
        _vm = vm;
        _verify = verify;
        _timer.Tick += (_, _) => CheckIdle();
        _timer.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public bool IsLocked => _locked;

    private void CheckIdle()
    {
        var minutes = _vm.Settings.AutoLockMinutes;
        if (minutes <= 0 || _locked) return;
        if (IdleTime() >= TimeSpan.FromMinutes(minutes))
            Lock($"Locked after {minutes} minute(s) without activity. Enter your master password to continue – open sessions keep running.");
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock && _vm.Settings.LockWithWindows)
            Application.Current?.Dispatcher.BeginInvoke(() => Lock("Locked together with Windows. Enter your master password to continue – open sessions keep running."));
    }

    /// <summary>Sperren: Fenster ausblenden, Sperrbildschirm zeigen; „Exit“ beendet HostDeck.</summary>
    public void Lock(string? reason = null)
    {
        if (_locked || Application.Current is not { } app) return;
        _locked = true;
        _vm.IsLocked = true;
        try
        {
            // RDP-Vollbild verlassen, dann WPF-Fenster und alle übrigen sichtbaren Fenster dieses Threads (offene Meldungen,
            // natives Vollbildfenster) ausblenden – nichts darf während der Sperre sichtbar oder bedienbar bleiben
            Rdp.RdpView.LeaveFullScreenAll();
            var hidden = app.Windows.OfType<Window>().Where(w => w.IsVisible && w is not LockWindow).ToList();
            foreach (var w in hidden) w.Hide();
            var native = HideNativeWindows();
            var dialog = new LockWindow(_verify, reason ?? "Enter your master password to continue. Open sessions keep running.");
            if (dialog.ShowDialog() == true)
            {
                foreach (var w in hidden) w.Show();
                foreach (var h in native) ShowWindow(h, SwShowNoActivate);
                (app.MainWindow)?.Activate();
                Log.Info("Unlocked");
            }
            else
            {
                Log.Info("Exit from lock screen");
                app.MainWindow?.Close();
            }
        }
        finally
        {
            _locked = false;
            _vm.IsLocked = false;
        }
    }

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    private static List<IntPtr> HideNativeWindows()
    {
        var found = new List<IntPtr>();
        EnumThreadWindows(GetCurrentThreadId(), (h, _) => { if (IsWindowVisible(h)) found.Add(h); return true; }, IntPtr.Zero);
        foreach (var h in found) ShowWindow(h, SwHide);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    public void Dispose()
    {
        _timer.Stop();
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
