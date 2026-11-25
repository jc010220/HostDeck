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
        // Strg+F springt in die Suche; beim Öffnen direkt lostippen können
        InputBindings.Add(new System.Windows.Input.KeyBinding(
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => { SearchBox.Focus(); SearchBox.SelectAll(); }),
            System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control));
        Loaded += (_, _) => SearchBox.Focus();
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

    /// <summary>Aufräumen: gleiche Zugangsdaten zusammenführen, danach optional ungenutzte Einträge löschen.</summary>
    private void OnCleanUp(object sender, RoutedEventArgs e)
    {
        var groups = _vm.DuplicateGroups();
        var duplicates = groups.Sum(g => g.Count - 1);
        if (duplicates > 0 && System.Windows.MessageBox.Show(this,
                $"{duplicates} credential(s) are exact duplicates of others (same user, domain, password and key) in {groups.Count} group(s).\n\n" +
                "Merge them? One entry per group is kept and all folders/connections are switched to it – nothing has to be re-assigned.",
                "Clean up vault", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.MergeDuplicates();

        var unused = _vm.Unused();
        if (unused.Count > 0 && System.Windows.MessageBox.Show(this,
                $"{unused.Count} credential(s) are not used by any folder or connection:\n\n" +
                string.Join("\n", unused.Take(12).Select(c => $"• {c.Name}  ({c.DisplayUser})")) + (unused.Count > 12 ? $"\n… and {unused.Count - 12} more" : "") +
                "\n\nDelete them?", "Clean up vault", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            _vm.RemoveMany(unused);

        if (duplicates == 0 && unused.Count == 0)
            System.Windows.MessageBox.Show(this, "No duplicates and no unused credentials found.", "Clean up vault", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete) return;
        OnRemove(sender, e);
        e.Handled = true;
    }

    /// <summary>Löschen – auch mehrere markierte Einträge auf einmal (Strg/Umschalt+Klick, Strg+A).</summary>
    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var selected = VaultList.SelectedItems.OfType<HostDeck.Core.Model.Credential>().ToList();
        if (selected.Count == 0 && _vm.Selected is { } single) selected.Add(single);
        if (selected.Count == 0) return;
        var uses = selected.Sum(_vm.UsageCount);
        var what = selected.Count == 1 ? $"“{selected[0].Name}”" : $"{selected.Count} credentials";
        var msg = (uses > 0 ? $"{what} {(selected.Count == 1 ? "is" : "are")} used by {uses} folder(s)/connection(s). They will ask for a login again (or use their folder's login).\n\n" : "")
                  + $"Delete {what}?";
        if (System.Windows.MessageBox.Show(this, msg, "Delete credentials", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _vm.RemoveMany(selected);
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
