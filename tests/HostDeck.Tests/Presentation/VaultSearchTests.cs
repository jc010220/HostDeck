using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

public class VaultSearchTests
{
    private static CredentialVaultViewModel Create()
    {
        var tree = new ConnectionTree();
        tree.Vault.Add(new Credential { Name = "RADIUS Alice", Username = "alice", Notes = "network devices" });
        tree.Vault.Add(new Credential { Name = "Domain admin", Username = "adm-bob", Domain = "CORP" });
        tree.Vault.Add(new Credential { Name = "Local root", Username = "root" });
        return new CredentialVaultViewModel(tree);
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("radius", 1)]
    [InlineData("corp", 1)]          // Domäne
    [InlineData("CORP\\adm", 1)]     // Anzeige Domäne\Benutzer
    [InlineData("network", 1)]       // Notizen
    [InlineData("o", 3)]
    [InlineData("local root", 1)]    // alle Wörter
    [InlineData("xyz", 0)]
    public void Filters_by_name_user_domain_and_notes(string filter, int expected)
    {
        var vm = Create();
        vm.Filter = filter;
        Assert.Equal(expected, vm.VisibleItems.Count);
        Assert.Equal(3, vm.Items.Count);
        if (expected > 0) Assert.Contains(vm.Selected, vm.VisibleItems);
    }

    [Fact]
    public void Adding_clears_the_filter_and_selects_the_new_entry()
    {
        var vm = Create();
        vm.Filter = "radius";
        vm.Add();
        Assert.Equal("", vm.Filter);
        Assert.Equal("New credentials", vm.Selected!.Name);
        Assert.Contains(vm.Selected, vm.VisibleItems);
    }
}
