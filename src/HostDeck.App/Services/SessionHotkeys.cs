using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HostDeck.App.Services;

/// <summary>
/// Hat eine RDP-Sitzung den Tastaturfokus, gehen alle Tasten an den entfernten Rechner – HostDeck-Kürzel kommen nie an.
/// Daher meldet HostDeck einige Kürzel bei Windows an (RegisterHotKey), aber nur solange ein HostDeck-Fenster aktiv ist.
/// So wirken sie auch aus einer Sitzung heraus, ohne andere Programme zu stören:
/// Strg+Alt+Esc (Tastatur aus der Sitzung lösen), Strg+Umschalt+K (Schnellsuche), Strg+Alt+L (Sperren), Strg+Alt+→ (nächster Bereich).
/// </summary>
internal sealed class SessionHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;

    private static readonly (int Id, uint Mods, uint Key, string Action)[] Keys =
    [
        (1, ModControl | ModAlt, 0x1B, "release"),             // Strg+Alt+Esc
        (2, ModControl | ModShift, 0x4B, "ctrl+shift+k"),      // Strg+Umschalt+K
        (3, ModControl | ModAlt, 0x4C, "ctrl+alt+l"),          // Strg+Alt+L
        (4, ModControl | ModAlt, 0x27, "ctrl+alt+arrowright"), // Strg+Alt+→
    ];

    private readonly IntPtr _hwnd;
    private readonly Action<string> _handler;
    private readonly HwndSource? _source;
    private bool _registered;

    public SessionHotkeys(Window owner, Action<string> handler)
    {
        _hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _handler = handler;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(Hook);
        Application.Current.Activated += OnActivated;
        Application.Current.Deactivated += OnDeactivated;
        if (Application.Current.Windows.OfType<Window>().Any(w => w.IsActive)) Register();
    }

    private void OnActivated(object? sender, EventArgs e) => Register();
    private void OnDeactivated(object? sender, EventArgs e) => Unregister();

    private void Register()
    {
        if (_registered) return;
        _registered = true;
        foreach (var k in Keys)
            if (!RegisterHotKey(_hwnd, k.Id, k.Mods | ModNoRepeat, k.Key))
                Log.Info($"Hotkey {k.Action} could not be registered (used by another program).");
    }

    private void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        foreach (var k in Keys) UnregisterHotKey(_hwnd, k.Id);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        var match = Keys.FirstOrDefault(k => k.Id == id);
        if (match.Action is null) return IntPtr.Zero;
        handled = true;
        // Gesperrt ⇒ nichts ausführen (Schnellsuche über dem Sperrbildschirm wäre eine Umgehung)
        if (Application.Current.Windows.OfType<Views.LockWindow>().Any(w => w.IsVisible)) return IntPtr.Zero;
        _handler(match.Action);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(Hook);
        Application.Current.Activated -= OnActivated;
        Application.Current.Deactivated -= OnDeactivated;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
