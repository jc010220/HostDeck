using HostDeck.Core.Model;
using static HostDeck.Tests.Presentation.MainViewModelTests;

namespace HostDeck.Tests.Presentation;

public class PersonalFolderTests
{
    [Fact]
    public void Personal_folder_is_created_on_top_level_even_if_a_folder_is_selected()
    {
        var (vm, dialogs, _, _) = Create();
        var team = vm.SessionTree.Add(new FolderNode("Team"));
        vm.SessionTree.SelectedItem = team;
        dialogs.PromptAnswer = _ => "Meine Server";
        vm.NewPersonalFolderCommand.Execute(null);

        var created = vm.SessionTree.Tree.Root.Children.OfType<FolderNode>().Single(f => f.Name == "Meine Server");
        Assert.Null(created.Parent!.Parent);              // direkt unter der Wurzel
        Assert.Empty(((FolderNode)team.Node).Children);
    }
}
