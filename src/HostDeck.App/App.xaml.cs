using System.Windows;
using System.Windows.Threading;
using HostDeck.App.Services;
using HostDeck.App.Views;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var dialogs = new DialogService();
        var viewModel = new MainViewModel(dialogs, new JsonSettingsStore(JsonSettingsStore.DefaultPath), new ThemeService());

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();

        // Erst nach Show(): Passwortdialog braucht ein sichtbares Owner-Fenster, Theme-Watcher ein HWND.
        viewModel.Initialize();

        // Datei per Kommandozeile / "Öffnen mit" (z. B. HostDeck.exe confCons.xml)
        if (e.Args is [var path, ..] && System.IO.File.Exists(path))
            viewModel.TryImport(path, silentOnCancel: false);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        new MessageDialog("Unerwarteter Fehler", e.Exception.ToString(), isError: true).ShowDialog();
        e.Handled = true; // Sessions in anderen Tabs nicht wegen eines UI-Fehlers verlieren
    }
}
