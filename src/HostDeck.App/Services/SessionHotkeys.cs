using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HostDeck.App.Services;

/// <summary>
/// Hat eine RDP-Sitzung den Tastaturfokus, gehen alle Tasten an den entfernten Rechner – HostDeck-Kürzel kommen nie an.
/// Daher meldet HostDeck einige Kürzel bei Windows an (RegisterHotKey), aber nur solange ein HostDeck-Fenster aktiv ist.
/// So wirken sie auch aus einer Sitzung heraus, ohne andere Programme zu stören:
/// Strg+Umschalt+K (Schnellsuche), Strg+Umschalt+W (Tab schließen), Strg+Alt+L (Sperren), Strg+Alt+→ (nächster Bereich).
/// Dazu wie bei VMware: linke Strg + linke Alt drücken und loslassen (ohne andere Taste) ⇒ Tastatur aus der Sitzung lösen.
/// AltGr (intern Strg + rechte Alt) löst dabei nie aus.
/// </summary>
internal sealed class SessionHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;

    private static readonly (int Id, uint Mods, uint Key, string Action)[] Keys =
    [
        (2, ModControl | ModShift, 0x4B, "ctrl+shift+k"),      // Strg+Umschalt+K
        (3, ModControl | ModAlt, 0x4C, "ctrl+alt+l"),          // Strg+Alt+L
        (4, ModControl | ModAlt, 0x27, "ctrl+alt+arrowright"), // Strg+Alt+→
        (5, ModControl | ModShift, 0x57, "ctrl+shift+w"),      // Strg+Umschalt+W: aktuellen Tab schließen – auch aus RDP/Terminal
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

    /// <summary>
    /// Nur im Haupt- bzw. Sitzungsfenster und ohne offenen Dialog (Host-Key-, Passwort-, Bestätigungsdialog …) und nie
    /// während der Sperre – sonst ließe sich z. B. der Tab unter einer offenen Rückfrage schließen.
    /// </summary>
    private static bool MayAct()
    {
        if (Application.Current is not { } app) return false;
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return false;
        if (app.Windows.OfType<Views.LockWindow>().Any(w => w.IsVisible)) return false;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) is Views.MainWindow or Views.SessionWindow;
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // Windows entfernt einen Low-Level-Hook still, wenn er einmal zu langsam war ⇒ bei jeder Aktivierung neu setzen
        RemoveReleaseHook();
        Register();
        InstallReleaseHook();
    }
    private void OnDeactivated(object? sender, EventArgs e) => Unregister();

    private void Register()
    {
        if (_registered) return;
        _registered = true;
        InstallReleaseHook();
        foreach (var k in Keys)
            if (!RegisterHotKey(_hwnd, k.Id, k.Mods | ModNoRepeat, k.Key))
                Log.Info($"Hotkey {k.Action} could not be registered (used by another program).");
    }

    private void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        RemoveReleaseHook();
        foreach (var k in Keys) UnregisterHotKey(_hwnd, k.Id);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        var match = Keys.FirstOrDefault(k => k.Id == id);
        if (match.Action is null) return IntPtr.Zero;
        handled = true;
        if (!MayAct()) return IntPtr.Zero;
        _handler(match.Action);
        return IntPtr.Zero;
    }

    // ---- Strg+Alt (links) allein: Tastatur freigeben -----------------------------------------------------
    // Low-Level-Hook nur, solange HostDeck aktiv ist. Er wertet ausschließlich linke Strg/Alt aus, speichert nichts und
    // reicht jede Taste unverändert weiter.

    private const int WhKeyboardLl = 13, WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    private const int VkLControl = 0xA2, VkLMenu = 0xA4;
    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);
    private LowLevelProc? _proc;
    private IntPtr _hook;
    private bool _ctrl, _alt, _spoiled;

    private void InstallReleaseHook()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = KeyboardProc;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        _ctrl = _alt = _spoiled = false;
    }

    private void RemoveReleaseHook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var injected = (Marshal.ReadInt32(lParam, 8) & 0x10) != 0; // LLKHF_INJECTED: von Programmen erzeugt ⇒ ignorieren
            if (injected) return CallNextHookEx(_hook, code, wParam, lParam);
            var msg = wParam.ToInt32();
            var down = msg is WmKeyDown or WmSysKeyDown;
            var up = msg is WmKeyUp or WmSysKeyUp;
            if (vk == VkLControl || vk == VkLMenu)
            {
                if (down)
                {
                    if (!_ctrl && !_alt) _spoiled = false; // neue Kombination beginnt
                    if (vk == VkLControl) _ctrl = true; else _alt = true;
                }
                else if (up)
                {
                    var complete = _ctrl && _alt && !_spoiled;
                    if (vk == VkLControl) _ctrl = false; else _alt = false;
                    if (complete)
                    {
                        _spoiled = true; // nur einmal pro Druck
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            if (MayAct()) _handler("release");
                        });
                    }
                }
            }
            else if (down && (_ctrl || _alt))
            {
                _spoiled = true; // andere Taste dazwischen (z. B. Strg+Alt+Entf, AltGr-Zeichen, Strg+Alt+L) ⇒ nicht auslösen
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

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
