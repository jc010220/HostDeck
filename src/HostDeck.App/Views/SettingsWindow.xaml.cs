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

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
