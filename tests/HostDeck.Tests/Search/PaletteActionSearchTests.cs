using HostDeck.Core.Model;
using HostDeck.Presentation.Search;

namespace HostDeck.Tests.Search;

public class PaletteActionSearchTests
{
    private static CommandPaletteViewModel Create(params string[] connectionNames)
    {
        var tree = new ConnectionTree();
        foreach (var n in connectionNames) tree.Root.Add(new ConnectionNode(n) { Hostname = "10.0.0.1" });
        return new CommandPaletteViewModel(() => tree, () => [], () => ProtocolKind.Ssh2,
            () => [("Show version (1.0.18)", "version info about", () => { }), ("Create backup …", "backup sicherung", () => { })]);
    }

    [Fact]
    public void Commands_are_found_without_the_prefix()
    {
        var vm = Create("web01", "verwaltung-srv");
        var results = vm.Compute("version").ToList();
        Assert.Equal("Show version (1.0.18)", results[0].Title);
        Assert.Equal(PaletteItemKind.Action, results[0].Kind);
    }

    [Fact]
    public void Scattered_letter_matches_do_not_add_commands()
    {
        var vm = Create("web01");
        Assert.DoesNotContain(vm.Compute("svn").ToList(), r => r.Kind == PaletteItemKind.Action);
        Assert.Contains(vm.Compute("web").ToList(), r => r.Title == "web01");
    }
}
