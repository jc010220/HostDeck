using HostDeck.Core.Model;
using HostDeck.Presentation.Snippets;
using HostDeck.Presentation.ViewModels;
using HostDeck.Storage;

namespace HostDeck.Tests.Presentation;

public class SnippetTests
{
    private sealed class MemorySnippets : ISnippetStore
    {
        public string? Json;
        public string? Load() => Json;
        public void Save(string json) => Json = json;
    }

    [Fact]
    public void Defaults_placeholders_and_persistence()
    {
        var store = new MemorySnippets();
        var sent = new List<string>();
        var answers = new Queue<string?>(["nginx", null]);
        var vm = new SnippetsViewModel(store, (_, _) => answers.Dequeue(), sent.Add);

        Assert.Contains(vm.Items, s => s.Name == "System load");                 // Startvorlagen
        var status = vm.Items.Single(s => s.Name == "Service status");
        vm.Run(status);
        Assert.Equal(["systemctl status nginx --no-pager\r"], sent);           // Platzhalter ersetzt + Enter
        vm.Run(status);
        Assert.Single(sent);                                                    // Abbruch ⇒ nichts senden

        var custom = vm.Add("Mehrzeilig", "cd /tmp\nls", execute: false);
        vm.Run(custom);
        Assert.Equal("cd /tmp\rls", sent[^1]);                                  // \n ⇒ \r, ohne Enter am Ende

        var reloaded = new SnippetsViewModel(store, (_, _) => null, _ => { });
        Assert.Contains(reloaded.Items, s => s.Name == "Mehrzeilig");
        reloaded.Filter = "mehrz";
        Assert.Equal("Mehrzeilig", Assert.Single(reloaded.Visible).Name);
    }

    [Fact]
    public void Snippets_are_stored_in_encrypted_database()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "c.hdk");
            using (var db = EncryptedConnectionStore.Create(path, "Testpasswort1", KdfParameters.FastForTests))
            {
                db.WriteDocument("snippets", "[{\"Name\":\"GEHEIMBEFEHL\"}]");
                db.Save(new ConnectionTree()); // Gesamtspeichern darf Dokumente nicht löschen
            }
            using (var reopened = EncryptedConnectionStore.Open(path, "Testpasswort1"))
                Assert.Contains("GEHEIMBEFEHL", reopened.ReadDocument("snippets"));
            // erst nach dem Schließen lesen – Windows sperrt geöffnete SQLite-Dateien
            foreach (var f in dir.GetFiles())
                Assert.DoesNotContain("GEHEIMBEFEHL", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(f.FullName)), StringComparison.Ordinal);
        }
        finally { dir.Delete(true); }
    }
}

public class DetachedWindowTests
{
    private static SessionTabViewModel Tab(string name) => new(new ConnectionNode(name) { Hostname = name }, new CredentialVault(), false);

    [Fact]
    public void Detach_and_close_window_moves_sessions_back_without_closing()
    {
        var (vm, _, _, _) = MainViewModelTests.Create();
        var a = vm.Workspace.Open(Tab("a"));
        var b = vm.Workspace.Open(Tab("b"));
        var closed = false;
        a.Closed += (_, _) => closed = true;
        WorkspaceViewModel? requested = null;
        vm.DetachedWindowRequested += w => requested = w;

        var detached = vm.DetachTab(a);
        Assert.Same(detached, requested);
        Assert.Equal([b], vm.Workspace.AllTabs);
        Assert.Equal([a], detached.AllTabs);
        Assert.True(a.IsSelected);
        Assert.Equal(2, vm.AllTabs.Count());
        Assert.False(closed);                                   // Session läuft weiter

        vm.MoveTabToWorkspace(b, detached);                     // Drag & Drop zwischen Fenstern
        Assert.Empty(vm.Workspace.AllTabs);
        Assert.Equal(2, detached.TabCount);

        vm.CloseDetachedWorkspace(detached);
        Assert.Empty(vm.DetachedWorkspaces);
        Assert.Equal(2, vm.Workspace.TabCount);
        Assert.False(closed);
    }
}
