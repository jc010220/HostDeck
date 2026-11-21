using System.Windows;
using HostDeck.Presentation.ViewModels;
using Wpf.Ui.Controls;

namespace HostDeck.App.Views;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }

    private void OnBrowseTeamLibrary(object sender, RoutedEventArgs e)
    {
        var vm = (SettingsViewModel)DataContext;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose team library (shared confCons.xml)",
            Filter = "mRemoteNG (confCons.xml)|*.xml|All files|*.*",
        };
        try
        {
            var current = vm.TeamLibraryPath.Trim();
            if (current.Length > 0) dialog.InitialDirectory = System.IO.Directory.Exists(current) ? current : System.IO.Path.GetDirectoryName(current);
        }
        catch (ArgumentException) { }
        if (dialog.ShowDialog(this) == true) vm.TeamLibraryPath = dialog.FileName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ((SettingsViewModel)DataContext).UpdateToken = TokenBox.Password ?? "";
        DialogResult = true;
    }
}
