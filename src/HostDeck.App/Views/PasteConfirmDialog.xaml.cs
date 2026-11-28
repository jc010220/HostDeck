using System.Windows;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

/// <summary>
/// Rückfrage beim Einfügen mehrerer Zeilen: zeigt den ganzen Text scroll- und bearbeitbar an (eingefügt wird, was im Feld
/// steht) – mit „Nicht mehr fragen“ direkt im Dialog.
/// </summary>
public partial class PasteConfirmDialog : FluentWindow
{
    public PasteConfirmDialog(int lineCount, string text, string? removedControlCharacters = null, bool multiExec = false, bool canSkip = true)
    {
        InitializeComponent();
        var intro = lineCount > 1 ? $"You are pasting {lineCount} lines. Each line is executed immediately" : "You are pasting text";
        if (multiExec) intro += " – on ALL sessions receiving multi-exec input";
        intro += ". You can check and edit the text below:";
        if (removedControlCharacters is not null)
            intro += $"\n\nHidden control characters were removed ({removedControlCharacters}). They act like key presses – e.g. ^O runs the line in bash.";
        Intro.Text = intro;
        Editor.Text = text;
        // Sicherheitsrelevante Fälle lassen sich nicht abschalten
        DontAsk.Visibility = canSkip ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => { Editor.Focus(); Editor.CaretIndex = 0; Editor.ScrollToHome(); };
    }

    public bool DontAskAgain => DontAsk.IsChecked == true;

    /// <summary>Text, wie er (ggf. bearbeitet) im Feld steht – Zeilenumbrüche als \n.</summary>
    public string EditedText => Editor.Text.Replace("\r\n", "\n").Replace('\r', '\n');

    private void OnPaste(object sender, RoutedEventArgs e) => DialogResult = true;
}
