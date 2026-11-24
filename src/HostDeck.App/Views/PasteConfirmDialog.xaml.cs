using System.Windows;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

/// <summary>Rückfrage beim Einfügen mehrerer Zeilen – mit „Nicht mehr fragen“ direkt im Dialog.</summary>
public partial class PasteConfirmDialog : FluentWindow
{
    public PasteConfirmDialog(int lineCount, string preview)
    {
        InitializeComponent();
        Intro.Text = $"You are pasting {lineCount} lines. Each line is executed immediately:";
        Preview.Text = preview;
    }

    public bool DontAskAgain => DontAsk.IsChecked == true;

    private void OnPaste(object sender, RoutedEventArgs e) => DialogResult = true;
}
