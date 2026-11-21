using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

/// <summary>Gemeldeter Fehler: „Die ganze Library ist rausgeflogen.“</summary>
public class DataSafetyTests
{
    private static ConnectionTree TreeWith(int connections)
    {
        var tree = new ConnectionTree();
        var folder = tree.Root.Add(new FolderNode("Kunden"));
        for (var i = 0; i < connections; i++) folder.Add(new ConnectionNode($"srv{i}") { Hostname = $"10.0.0.{i}" });
        return tree;
    }

    [Fact]
    public void Failed_load_never_overwrites_the_database()
    {
        var d = new MainViewModelTests.FakeDialogs();
        var settings = new MainViewModelTests.MemorySettings();
        var store = new MainViewModelTests.MemoryStore { Saved = TreeWith(25) };
        var original = store.Saved;
        store.LoadFailure = new InvalidOperationException("Datenbank gesperrt");
        var vm = new MainViewModel(d, settings, new MainViewModelTests.NoTheme(), store);

        vm.Initialize();
        Assert.Single(d.Errors);                                   // klare Fehlermeldung
        Assert.Empty(vm.SessionTree.Items);

        vm.NewFolderCommand.Execute(null);                         // Änderungen …
        vm.Shutdown();                                             // … und Beenden
        Assert.Same(original, store.Saved);                        // Datenbank unverändert!
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void Import_after_failed_load_backs_up_first_and_asks()
    {
        var d = new MainViewModelTests.FakeDialogs();
        var store = new MainViewModelTests.MemoryStore { Saved = TreeWith(25), LoadFailure = new IOException("defekt") };
        var vm = new MainViewModel(d, new MainViewModelTests.MemorySettings(), new MainViewModelTests.NoTheme(), store);
        vm.Initialize();

        d.ConfirmAnswer = false;
        Assert.False(vm.TryImport(Fixture.PathOf("confCons_v2_6.xml")));
        Assert.Equal(0, store.SaveCount);

        d.ConfirmAnswer = true;
        Assert.True(vm.TryImport(Fixture.PathOf("confCons_v2_6.xml")));
        Assert.Single(store.Backups);                              // alte Datenbank zuerst gesichert
        Assert.Equal(12, store.Saved.Connections.Count());
    }

    [Fact]
    public void Losing_more_than_half_of_connections_triggers_automatic_backup()
    {
        var (vm, _, _, store) = MainViewModelTests.Create();
        store.Saved = TreeWith(20);
        vm.SessionTree.Load(store.Saved);
        vm.Persist();
        Assert.Empty(store.Backups);                               // normale Speicherung: keine Sicherung

        vm.SessionTree.Delete(vm.SessionTree.Items.Single());      // ganzer Ordner weg
        Assert.Single(store.Backups);                              // vorher gesichert
        Assert.Empty(store.Saved.Connections);
    }

    [Fact]
    public async Task Import_runs_in_background_and_reports_busy_state()
    {
        var (vm, d, _, store) = MainViewModelTests.Create();
        d.FileToOpen = Fixture.PathOf("confCons_v2_6.xml");
        var busySeen = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsImporting) && vm.IsImporting) busySeen = true; };
        await vm.ImportMRemoteNGCommand.ExecuteAsync(null);
        Assert.True(busySeen);
        Assert.False(vm.IsImporting);
        Assert.Equal(12, store.Saved.Connections.Count());
    }
}
