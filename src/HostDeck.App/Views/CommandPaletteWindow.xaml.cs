using System.Windows;
using System.Windows.Input;
using HostDeck.Presentation.Search;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.App.Views;

/// <summary>Schnellsuche (Strg+K). Eigenes Fenster ⇒ liegt auch über RDP-/Terminal-HWNDs.</summary>
public partial class CommandPaletteWindow : Window
{
    private readonly MainViewModel _main;
    private bool _closing;

    public CommandPaletteWindow(MainViewModel main, Window owner)
    {
        _main = main;
        Owner = owner;
        DataContext = main.Palette;
        InitializeComponent();

        main.Palette.Query = "";
        main.Palette.Refresh();
        Loaded += (_, _) =>
        {
            // oben mittig über dem Hauptfenster
            Left = owner.Left + (owner.ActualWidth - ActualWidth) / 2;
            Top = owner.Top + 70;
            QueryBox.Focus();
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var palette = _main.Palette;
        switch (e.Key)
        {
            case Key.Escape: CloseSafe(); break;
            case Key.Down: palette.MoveSelection(+1); Scroll(); break;
            case Key.Up: palette.MoveSelection(-1); Scroll(); break;
            case Key.PageDown: palette.MoveSelection(+8); Scroll(); break;
            case Key.PageUp: palette.MoveSelection(-8); Scroll(); break;
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt):
                Reveal(palette.Selected);
                break;
            case Key.Enter:
                Execute(palette.Selected, split: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                break;
            case Key.System when e.SystemKey == Key.Enter: // Alt+Enter kommt als SystemKey
                Reveal(palette.Selected);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void Scroll()
    {
        if (ResultList.SelectedItem is { } item) ResultList.ScrollIntoView(item);
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ResultList.SelectedItem is PaletteItem item)
            Execute(item, split: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private void Execute(PaletteItem? item, bool split)
    {
        if (item is null) return;
        CloseSafe();
        _main.ExecutePaletteItem(item, split);
    }

    private void Reveal(PaletteItem? item)
    {
        if (item?.Connection is null) return;
        CloseSafe();
        _main.SessionTree.FilterText = "";
        if (_main.SessionTree.FindByNode(item.Connection) is { } treeItem) _main.RevealInTree(treeItem);
    }

    private void OnDeactivated(object? sender, EventArgs e) => CloseSafe();

    private void CloseSafe()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
