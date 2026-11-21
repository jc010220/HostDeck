using System.IO;
using System.Windows;
using System.Windows.Threading;
using HostDeck.App.Services;
using HostDeck.App.Views;
using HostDeck.Presentation.Services;
using HostDeck.Presentation.ViewModels;
using HostDeck.Storage;

namespace HostDeck.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private StoreAdapter? _store;
    private UpdateService? _updates;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unbehandelte Ausnahme", (Exception)args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Hintergrundaufgabe", args.Exception); args.SetObserved(); };
        Log.Info($"Start, Version {typeof(App).Assembly.GetName().Version}");
        Log.Cleanup();
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // Dialoge vor dem Hauptfenster dürfen die App nicht beenden

        // Zwei Instanzen würden sich gegenseitig die Datenbank überschreiben
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\HostDeck.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("HostDeck läuft bereits.", "HostDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settingsStore = new JsonSettingsStore(JsonSettingsStore.DefaultPath);
        var theme = new ThemeService();
        theme.Apply(settingsStore.Load().Theme); // Entsperr-Dialog bereits im richtigen Theme

        var device = new DpapiDeviceKeyProtector();
        var store = UnlockOrCreate(EncryptedConnectionStore.DefaultPath, device);
        if (store is null)
        {
            Shutdown();
            return;
        }
        _store = new StoreAdapter(store, device);

        var dialogs = new DialogService();
        var viewModel = new MainViewModel(dialogs, settingsStore, theme, _store);
        _updates = new UpdateService(viewModel, dialogs, _store);
        _updates.Register();
        var window = new MainWindow(viewModel);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Closing += (_, _) => viewModel.Shutdown();
        window.Show();

        var mRemoteNgFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG", "confCons.xml");
        viewModel.Initialize(mRemoteNgFile);
        _ = _updates.CheckOnStartupAsync();

        // Datei per Kommandozeile / "Öffnen mit" (z. B. HostDeck.exe confCons.xml)
        if (e.Args is [var path, ..] && File.Exists(path))
            viewModel.TryImport(path);
    }

    /// <summary>Gerätschlüssel → Passwortdialog (mit Wiederholung) → bzw. Ersteinrichtung.</summary>
    private static EncryptedConnectionStore? UnlockOrCreate(string path, IDeviceKeyProtector device)
    {
        if (!EncryptedConnectionStore.Exists(path))
            return CreateNew(path, device);

        try
        {
            if (OperatingSystem.IsWindows() && EncryptedConnectionStore.TryOpenWithDevice(path, device) is { } viaDevice)
                return viaDevice;
        }
        catch (InvalidDataException) { /* Geräteschlüssel passt nicht mehr ⇒ Passwort abfragen */ }

        string? error = null;
        while (true)
        {
            var dialog = new MasterKeyDialog(MasterKeyDialogMode.Unlock, error, OperatingSystem.IsWindows());
            if (dialog.ShowDialog() != true)
            {
                if (!dialog.ResetRequested) return null;
                MoveAside(path);
                return CreateNew(path, device);
            }

            try
            {
                var store = EncryptedConnectionStore.Open(path, dialog.Password);
                if (dialog.Remember) store.EnableDeviceUnlock(device);
                return store;
            }
            catch (WrongMasterPasswordException)
            {
                error = "Falsches Master-Passwort.";
            }
            catch (InvalidDataException ex)
            {
                MessageBox.Show(ex.Message, "Datenbank kann nicht geöffnet werden", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }
    }

    private static EncryptedConnectionStore? CreateNew(string path, IDeviceKeyProtector device)
    {
        var dialog = new MasterKeyDialog(MasterKeyDialogMode.Create, deviceUnlockSupported: OperatingSystem.IsWindows());
        if (dialog.ShowDialog() != true) return null;
        var store = EncryptedConnectionStore.Create(path, dialog.Password);
        if (dialog.Remember) store.EnableDeviceUnlock(device);
        return store;
    }

    /// <summary>„Passwort vergessen“: alte Dateien nicht löschen, sondern mit Zeitstempel sichern.</summary>
    private static void MoveAside(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var file in new[] { path, path + ".key", path + "-wal", path + "-shm" })
            if (File.Exists(file)) File.Move(file, $"{file}.{stamp}.bak");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updates?.ApplyOnExit();
        _store?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI", e.Exception);
        new MessageDialog("Unerwarteter Fehler",
            $"{e.Exception.Message}\n\nDetails stehen im Fehlerprotokoll:\n{Log.Directory}\n\n{e.Exception}", isError: true).ShowDialog();
        e.Handled = true; // Sessions in anderen Tabs nicht wegen eines UI-Fehlers verlieren
    }
}
