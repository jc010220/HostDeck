using System.Xml.Linq;
using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.Tests.Presentation;

/// <summary>
/// Regressionstest für den Absturz in 1.0.0–1.0.3: Event-Handler in Styles/Vorlagen (z. B. Click an einem
/// MenuItem in einem Style-Setter) verdrahtet der WPF-XAML-Compiler falsch – „Set connectionId threw an
/// exception“ in einer Endlosschleife. In solchen Bereichen sind nur Commands erlaubt.
/// </summary>
public class XamlSafetyTests
{
    private static readonly string[] EventAttributes =
        ["Click", "MouseDown", "MouseUp", "MouseDoubleClick", "PreviewMouseDown", "PreviewMouseUp", "KeyDown", "KeyUp",
         "PreviewKeyDown", "Loaded", "Checked", "Unchecked", "SelectionChanged", "TextChanged", "MouseLeftButtonDown", "PreviewMouseLeftButtonDown"];

    private static readonly string[] TemplateContainers = ["Setter.Value", "DataTemplate", "ControlTemplate", "HierarchicalDataTemplate", "ItemsPanelTemplate"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HostDeck.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository nicht gefunden");
    }

    [Fact]
    public void No_event_handlers_inside_styles_or_templates()
    {
        var violations = new List<string>();
        var files = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "HostDeck.App"), "*.xaml", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in doc.Descendants())
            {
                var insideTemplate = element.Ancestors().Any(a => TemplateContainers.Contains(a.Name.LocalName));
                if (!insideTemplate) continue;
                foreach (var attr in element.Attributes().Where(a => EventAttributes.Contains(a.Name.LocalName)))
                {
                    var line = ((System.Xml.IXmlLineInfo)attr).LineNumber;
                    violations.Add($"{Path.GetFileName(file)}:{line} <{element.Name.LocalName} {attr.Name.LocalName}=\"{attr.Value}\">");
                }
            }
        }
        Assert.True(violations.Count == 0, "Event-Handler in Style/Vorlage (stattdessen Command verwenden):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Tab_commands_reach_the_right_workspace()
    {
        var (vm, _, _, _) = MainViewModelTests.Create();
        vm.QuickConnectText = "rdp 10.1.2.3";
        vm.QuickConnectCommand.Execute(null);
        var a = vm.Workspace.ActiveTab!;
        vm.QuickConnectText = "rdp 10.1.2.4";
        vm.QuickConnectCommand.Execute(null);
        var b = vm.Workspace.ActiveTab!;

        a.RequestDetachCommand.Execute(null);                // nur Tab a wandert in ein neues Fenster
        Assert.Equal([b], vm.Workspace.AllTabs);
        var detached = Assert.Single(vm.DetachedWorkspaces);
        Assert.Equal([a], detached.AllTabs);

        a.RequestCloseCommand.Execute(null);                 // Schließen im Zusatzfenster
        Assert.Empty(detached.AllTabs);
        Assert.Equal([b], vm.Workspace.AllTabs);             // b bleibt unberührt

        b.RequestSaveCommand.Execute(null);                  // Quick-Connect-Tab speichern
        Assert.NotNull(vm.SessionTree.FindByNode(b.Connection));
        Assert.Equal([b], vm.Workspace.AllTabs);             // Speichern schließt nichts
    }
}
