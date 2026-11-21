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
        Behaviors.GentleWheelScroll.Register();
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", (Exception)args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Background task", args.Exception); args.SetObserved(); };
        Log.Info($"Startup, version {typeof(App).Assembly.GetName().Version}");
        Log.Cleanup();
        HostDeck.Presentation.Services.AppLog.Sink = Log.Error;
        HostDeck.Presentation.Services.AppLog.InfoSink = Log.Info;
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // Dialoge vor dem Hauptfenster dürfen die App nicht beenden

        // Zwei Instanzen würden sich gegenseitig die Datenbank überschreiben
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\HostDeck.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("HostDeck is already running.", "HostDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settingsStore = new JsonSettingsStore(JsonSettingsStore.DefaultPath);
        var theme = new ThemeService();
        theme.Apply(settingsStore.Load().Theme); // Entsperr-Dialog bereits im richtigen Theme

        var device = new DpapiDeviceKeyProtector();
        var store = UnlockOrCreate(EncryptedConnectionStore.DefaultPath, device);
        Log.Info(store is null ? "Unlock cancelled" : $"Database opened: {store.DatabasePath} ({new FileInfo(store.DatabasePath).Length / 1024} KB)");
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

        _ = StartAsync(viewModel, store, e.Args);
    }

    /// <summary>Laden, Arbeitsbereich wiederherstellen, ggf. importieren – ohne den UI-Thread zu blockieren.</summary>
    private async Task StartAsync(MainViewModel viewModel, EncryptedConnectionStore store, string[] args)
    {
        try
        {
            DailyBackup(store);
            var mRemoteNgFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG", "confCons.xml");
            await viewModel.InitializeAsync(mRemoteNgFile);
            Log.Info("Startup complete");
            _ = _updates!.CheckOnStartupAsync();

            // Datei per Kommandozeile / "Öffnen mit" (z. B. HostDeck.exe confCons.xml)
            if (args is [var path, ..] && File.Exists(path))
                await viewModel.TryImportAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error("Start", ex);
        }
    }

    /// <summary>Einmal täglich beim Start eine verschlüsselte Sicherung (die letzten 10 bleiben erhalten).</summary>
    private static void DailyBackup(EncryptedConnectionStore store)
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "backups");
            Directory.CreateDirectory(dir);
            var today = DateTime.Now.ToString("yyyyMMdd");
            if (Directory.EnumerateFiles(dir, $"HostDeck-Sicherung-{today}-*.hdk").Any()) return;
            if (store.CountConnections() == 0) return; // nichts Sicherungswürdiges (und keine leeren Sicherungen anhäufen)
            Log.Info($"Daily backup: {store.Backup(dir)}");

            foreach (var old in Directory.EnumerateFiles(dir, "HostDeck-Sicherung-*.hdk").OrderByDescending(f => f).Skip(10))
            {
                File.Delete(old);
                File.Delete(old + ".key");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Daily backup", ex);
        }
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
                error = "Wrong master password.";
            }
            catch (InvalidDataException ex)
            {
                MessageBox.Show(ex.Message, "Database cannot be opened", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private static bool _showingError;

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI", e.Exception);
        e.Handled = true; // Sessions in anderen Tabs nicht wegen eines UI-Fehlers verlieren
        if (_showingError) return;
        _showingError = true;
        // Dialog nicht innerhalb des fehlerhaften Aufrufs öffnen (Layout/COM-Rückruf) – sonst Folgeabsturz
        Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                new MessageDialog("Unexpected error",
                    $"{e.Exception.Message}\n\nHostDeck keeps running. Details in the error log:\n{Log.Directory}", isError: true).ShowDialog();
            }
            catch (Exception ex) { Log.Error("Error dialog", ex); }
            finally { _showingError = false; }
        });
    }
}
