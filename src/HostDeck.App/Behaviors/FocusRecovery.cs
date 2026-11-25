using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace HostDeck.App.Behaviors;

/// <summary>
/// Eingebettete Fenster (RDP-ActiveX, WebView2) behalten den Windows-Tastaturfokus, auch wenn man daneben in die
/// WPF-Oberfläche klickt – und nach dem Schließen einer RDP-Sitzung liegt der Fokus „nirgends“. Dann kommen keine
/// Tasten mehr bei HostDeck an (Strg+K usw.). Klick in die Oberfläche bzw. Ende der Sitzung holt den Fokus zurück.
/// </summary>
internal static class FocusRecovery
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((sender, _) => { if (sender is Window w) Reclaim(w); }), handledEventsToo: true);

    /// <summary>Nach dem Schließen/Trennen einer eingebetteten Sitzung: Fokus zurück an das Fenster, falls er verloren ist.</summary>
    public static void Restore(Window? window)
    {
        if (window is null || !window.IsActive) return;
        // Nur wenn der Fokus wirklich verloren ist – ein inzwischen fokussiertes anderes Terminal nicht wegnehmen
        if (CurrentFocus() != IntPtr.Zero) return;
        Reclaim(window);
    }

    /// <summary>Hat wirklich ein eingebettetes Kindfenster (Terminal, RDP) den Windows-Tastaturfokus?</summary>
    public static bool FocusInEmbeddedChild(Window? window)
    {
        if (window is null) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        var focused = CurrentFocus();
        return hwnd != IntPtr.Zero && focused != IntPtr.Zero && focused != hwnd && IsChild(hwnd, focused);
    }

    /// <summary>Tastatur aus einer eingebetteten Sitzung (RDP …) zurück an HostDeck – die Sitzung bleibt offen.</summary>
    public static bool Release(Window window) => Reclaim(window);

    private static bool Reclaim(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        var focused = CurrentFocus();
        var lost = focused == IntPtr.Zero && window.IsActive;
        var embedded = focused != IntPtr.Zero && focused != hwnd && IsChild(hwnd, focused);
        if (!lost && !embedded) return false;      // WPF hat den Fokus bereits bzw. anderes Programm aktiv

        // WPF würde beim Zurückholen sonst wieder das eingebettete Element fokussieren (das gibt ihn an das Kindfenster weiter)
        if (Keyboard.FocusedElement is HwndHost || FocusManager.GetFocusedElement(window) is HwndHost)
            FocusManager.SetFocusedElement(window, null);
        SetFocus(hwnd);
        if (Keyboard.FocusedElement is null or HwndHost) window.Focus();
        return true;
    }

    /// <summary>
    /// Fenster mit Tastaturfokus – auch wenn es einem anderen Prozess gehört (WebView2/Chromium läuft in eigenen Prozessen;
    /// GetFocus sieht dann nichts).
    /// </summary>
    private static IntPtr CurrentFocus()
    {
        var focus = GetFocus();
        if (focus != IntPtr.Zero) return focus;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
}
