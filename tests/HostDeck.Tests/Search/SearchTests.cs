using HostDeck.Core.Model;
using HostDeck.Presentation.Search;

namespace HostDeck.Tests.Search;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("acw1", "acme-web-01")]
    [InlineData("web", "acme-web-01")]
    [InlineData("ACME", "acme-web-01")]
    [InlineData("dc", "DomainController")]
    [InlineData("10.0.5", "10.0.5.20")]
    public void Matches(string query, string text) => Assert.True(FuzzyMatcher.Score(query, text) > 0);

    [Theory]
    [InlineData("xyz", "acme-web-01")]
    [InlineData("web99", "acme-web-01")]
    [InlineData("toolongquery", "short")]
    public void Does_not_match(string query, string text) => Assert.Equal(0, FuzzyMatcher.Score(query, text));

    [Fact]
    public void Ranking_prefers_exact_prefix_wordstart_then_scattered()
    {
        int S(string t) => FuzzyMatcher.Score("web", t);
        Assert.True(S("web") > S("web01"));                 // exakt
        Assert.True(S("web01") > S("acme-web01"));          // Präfix
        Assert.True(S("acme-web01") > S("cobweb"));         // Wortanfang vor Wortmitte
        Assert.True(S("cobweb") > S("w-e-b"));              // zusammenhängend vor verstreut
    }

    [Fact]
    public void Returns_positions_for_highlighting()
    {
        FuzzyMatcher.Match("aw1", "acme-web-01", out var pos);
        Assert.Equal([0, 5, 10], pos);
    }
}

public class CommandPaletteTests
{
    private static (CommandPaletteViewModel Vm, ConnectionTree Tree, List<Guid> Recent, List<string> Ran) Create()
    {
        var tree = new ConnectionTree();
        var acme = tree.Root.Add(new FolderNode("Kunde ACME"));
        var web = acme.Add(new ConnectionNode("acme-web-01") { Hostname = "10.1.0.11" });
        web.Settings.Protocol = ProtocolKind.Ssh2;
        acme.Add(new ConnectionNode("acme-db-01") { Hostname = "10.1.0.21" }).Settings.Description = "PostgreSQL Produktion";
        var dc = tree.Root.Add(new FolderNode("Intern")).Add(new ConnectionNode("dc01") { Hostname = "dc01.corp.local" });
        dc.Settings.Favorite = true;
        tree.Root.Add(new ConnectionNode("webmail") { Hostname = "mail.corp.local" });
        var recent = new List<Guid>();
        var ran = new List<string>();
        var vm = new CommandPaletteViewModel(() => tree, () => recent, () => ProtocolKind.Ssh2,
            [("Layout: 4er-Grid", "grid quad", () => ran.Add("grid")), ("Neue Verbindung", "new", () => ran.Add("new"))]);
        return (vm, tree, recent, ran);
    }

    [Fact]
    public void Empty_query_shows_recent_then_favorites()
    {
        var (vm, tree, recent, _) = Create();
        recent.Add(tree.Connections.Single(c => c.Name == "acme-db-01").Id);
        vm.Refresh();
        Assert.Equal(["acme-db-01", "dc01"], vm.Results.Select(r => r.Title));
        Assert.True(vm.Results[0].IsRecent);
        Assert.True(vm.Results[1].IsFavorite);
        Assert.Same(vm.Results[0], vm.Selected);
    }

    [Fact]
    public void Fuzzy_query_ranks_name_matches_first()
    {
        var (vm, _, _, _) = Create();
        vm.Query = "acw1";
        Assert.Equal("acme-web-01", vm.Results[0].Title);
        Assert.Equal("Kunde ACME  ·  10.1.0.11", vm.Results[0].Subtitle);

        vm.Query = "web";
        Assert.Equal(["webmail", "acme-web-01"], vm.Results.Take(2).Select(r => r.Title)); // Präfix vor Wortanfang
    }

    [Fact]
    public void Searches_host_path_description_and_multiple_terms()
    {
        var (vm, _, _, _) = Create();
        vm.Query = "10.1.0.21";
        Assert.Equal("acme-db-01", vm.Results[0].Title);
        vm.Query = "postgres";
        Assert.Equal("acme-db-01", vm.Results.Single().Title);
        vm.Query = "acme db";
        Assert.Equal("acme-db-01", vm.Results[0].Title);
        vm.Query = "intern";
        Assert.Equal("dc01", vm.Results.Single().Title);
    }

    [Fact]
    public void Unknown_address_offers_quick_connect()
    {
        var (vm, _, _, _) = Create();
        vm.Query = "ssh root@192.168.7.7:2222";
        var qc = Assert.Single(vm.Results);
        Assert.Equal(PaletteItemKind.QuickConnect, qc.Kind);
        Assert.Equal(2222, qc.QuickConnect!.Port);

        vm.Query = "dc01";  // bekannter Name ⇒ kein Quick-Connect-Eintrag zusätzlich
        Assert.DoesNotContain(vm.Results, r => r.Kind == PaletteItemKind.QuickConnect);
    }

    [Fact]
    public void Commands_with_prefix_and_keyboard_navigation()
    {
        var (vm, _, _, ran) = Create();
        vm.Query = ">grid";
        var item = Assert.Single(vm.Results);
        item.Action!();
        Assert.Equal(["grid"], ran);

        vm.Query = ">";
        Assert.Equal(2, vm.Results.Count);
        vm.MoveSelection(+1);
        Assert.Same(vm.Results[1], vm.Selected);
        vm.MoveSelection(+5);
        Assert.Same(vm.Results[1], vm.Selected);
        vm.MoveSelection(-9);
        Assert.Same(vm.Results[0], vm.Selected);
    }
}
