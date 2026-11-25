using System.Windows;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

/// <summary>Rückfrage beim Einfügen mehrerer Zeilen – mit „Nicht mehr fragen“ direkt im Dialog.</summary>
public partial class PasteConfirmDialog : FluentWindow
{
    public PasteConfirmDialog(int lineCount, string preview, bool controlCharacters = false, bool multiExec = false, bool canSkip = true)
    {
        InitializeComponent();
        var intro = lineCount > 1 ? $"You are pasting {lineCount} lines. Each line is executed immediately" : "You are pasting text";
        if (multiExec) intro += " – on ALL sessions receiving multi-exec input";
        intro += ":";
        if (controlCharacters) intro += "\n\nThe text contains hidden control characters (shown as ^X / ␛). They act like key presses – e.g. ^O runs the line in bash.";
        Intro.Text = intro;
        Preview.Text = preview;
        // Sicherheitsrelevante Fälle lassen sich nicht abschalten
        DontAsk.Visibility = canSkip ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool DontAskAgain => DontAsk.IsChecked == true;

    private void OnPaste(object sender, RoutedEventArgs e) => DialogResult = true;
}
