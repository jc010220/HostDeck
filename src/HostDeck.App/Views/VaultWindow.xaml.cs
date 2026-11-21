using System.IO;
using System.Windows;
using HostDeck.Presentation.ViewModels;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace HostDeck.App.Views;

public partial class VaultWindow : FluentWindow
{
    private readonly CredentialVaultViewModel _vm;

    public VaultWindow(CredentialVaultViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CredentialVaultViewModel.Selected)) { PasswordInput.Clear(); PassphraseInput.Clear(); }
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.NewPassword = PasswordInput.Password.Length > 0 ? PasswordInput.Password : null;
        _vm.NewPassphrase = PassphraseInput.Password.Length > 0 ? PassphraseInput.Password : null;
        _vm.Save();
        PasswordInput.Clear();
        PassphraseInput.Clear();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } c) return;
        var uses = _vm.UsageCount(c);
        var msg = uses > 0 ? $"“{c.Name}” is used by {uses} entry/entries. They will inherit from their folder again.\n\nDelete?" : $"Delete “{c.Name}”?";
        if (System.Windows.MessageBox.Show(this, msg, "Delete credentials", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _vm.Remove();
    }

    private void OnLoadKey(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load private key",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dialog.ShowDialog(this) == true) _vm.PrivateKey = File.ReadAllText(dialog.FileName);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
