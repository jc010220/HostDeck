using System.Windows;
using HostDeck.Presentation.ViewModels;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public partial class ConnectionEditorWindow : FluentWindow
{
    private readonly ConnectionEditorViewModel _vm;

    public ConnectionEditorWindow(ConnectionEditorViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.NewPassword = PasswordInput.Password.Length > 0 ? PasswordInput.Password : null;
        if (_vm.Apply() is { } error)
        {
            ErrorText.Text = error;
            return;
        }
        DialogResult = true;
    }
}
