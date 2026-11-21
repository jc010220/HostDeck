using System.Windows.Controls;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.App.Views;

public partial class SessionPlaceholderView : UserControl
{
    public SessionPlaceholderView()
    {
        InitializeComponent();
        // Passwort nie anzeigen – nur ob eines vorhanden ist.
        DataContextChanged += (_, _) =>
            PasswordState.Text = DataContext is SessionTabViewModel { Credentials.Password.Length: > 0 } ? "stored ●●●●●●" : "–";
    }
}
