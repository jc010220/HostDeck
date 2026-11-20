using System.Windows;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public partial class MessageDialog : FluentWindow
{
    public MessageDialog(string title, string message, bool isError)
    {
        InitializeComponent();
        Title = title;
        TitleBar.Title = (isError ? "⚠  " : "") + title;
        MessageText.Text = message;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
