using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HostDeck.App.Behaviors;

/// <summary>
/// Ruhigeres, kontrollierbares Scrollen in allen Listen (Baum, Vault, SFTP, Snippets …).
///
/// WPF scrollt pro Mausrad-Ereignis pauschal 3 Zeilen – unabhängig davon, wie weit das Rad gedreht wurde.
/// Mäuse mit fein gerastertem/freilaufendem Rad und Touchpads senden aber viele kleine Ereignisse
/// (z. B. Delta 10 statt 120) ⇒ die Liste „rast“. Hier wird proportional zum tatsächlichen Delta gescrollt:
/// eine normale Raste = 2 Einträge bzw. 40 Pixel, kleine Bewegungen entsprechend weniger.
/// </summary>
public static class GentleWheelScroll
{
    private const double NotchDelta = 120;      // eine Raste laut Windows
    private const double ItemsPerNotch = 2;     // WPF-Standard: 3
    private const double PixelsPerNotch = 40;   // WPF-Standard: 48

    private sealed class Remainder { public double Value; }
    private static readonly ConditionalWeakTable<ScrollViewer, Remainder> Remainders = new();

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer || Keyboard.Modifiers != ModifierKeys.None) return;

        // Der Vorschau-Ereignisweg läuft von außen nach innen – nur der innerste scrollbare Bereich unter
        // der Maus reagiert (sonst würde z. B. das Fenster statt der Liste scrollen).
        if (FindTarget(e.OriginalSource as DependencyObject) != viewer) return;
        e.Handled = true;

        if (viewer.CanContentScroll)
        {
            // Logisches Scrollen (Einträge): Delta sammeln, ganze Einträge weiterschalten
            var remainder = Remainders.GetOrCreateValue(viewer);
            remainder.Value += e.Delta * ItemsPerNotch / NotchDelta;
            while (remainder.Value >= 1) { viewer.LineUp(); remainder.Value -= 1; }
            while (remainder.Value <= -1) { viewer.LineDown(); remainder.Value += 1; }
        }
        else
        {
            var offset = viewer.VerticalOffset - e.Delta * PixelsPerNotch / NotchDelta;
            viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, viewer.ScrollableHeight));
        }
    }

    private static ScrollViewer? FindTarget(DependencyObject? source)
    {
        for (var current = source; current is not null; current = Parent(current))
        {
            if (current is ScrollViewer { ScrollableHeight: > 0, VerticalScrollBarVisibility: not ScrollBarVisibility.Disabled } sv)
                return sv;
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject child) =>
        child is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child); // z. B. Run in einem TextBlock
}
