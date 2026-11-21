using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HostDeck.Core.Inheritance;
using HostDeck.Core.Model;

namespace HostDeck.Presentation.ViewModels;

public enum DropPosition { Before, Into, After }

/// <summary>Ein Eintrag im Session-Tree (Ordner oder Verbindung).</summary>
public sealed partial class TreeItemViewModel : ObservableObject
{
    private bool _suppressModelSync;

    public TreeItemViewModel(TreeNode node, TreeItemViewModel? parent)
    {
        Node = node;
        Parent = parent;
        if (node is FolderNode folder)
        {
            _isExpanded = folder.IsExpanded;
            foreach (var child in folder.Children)
                Children.Add(new TreeItemViewModel(child, this));
        }
        RefreshSelf(); // Kinder haben sich im eigenen Konstruktor bereits berechnet
    }

    public TreeNode Node { get; }
    public TreeItemViewModel? Parent { get; internal set; }
    public ObservableCollection<TreeItemViewModel> Children { get; } = [];
    public bool IsFolder => Node is FolderNode;

    public string Name
    {
        get => Node.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Node.Name) return;
            Node.Name = value.Trim();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;
    /// <summary>True, wenn der Eintrag selbst dem Filter entspricht (für Hervorhebung).</summary>
    [ObservableProperty] private bool _isMatch;

    [ObservableProperty] private ProtocolKind _protocol;
    /// <summary>Erreichbarkeit des Dienst-Ports (nur Verbindungen).</summary>
    [ObservableProperty] private Services.Reachability _reachability;
    [ObservableProperty] private string _subtitle = "";

    partial void OnIsExpandedChanged(bool value)
    {
        if (!_suppressModelSync && Node is FolderNode folder)
            folder.IsExpanded = value;
    }

    /// <summary>Expandiert nur visuell (Filter), ohne den gespeicherten Zustand zu ändern.</summary>
    internal void SetExpandedTransient(bool value)
    {
        _suppressModelSync = true;
        IsExpanded = value;
        _suppressModelSync = false;
    }

    /// <summary>Effektive Werte neu berechnen – nötig nach Verschieben (Vererbung ändert sich!).</summary>
    public void NotifyNameChanged() => OnPropertyChanged(nameof(Name));

    public void RefreshDisplay()
    {
        RefreshSelf();
        foreach (var child in Children) child.RefreshDisplay();
    }

    private void RefreshSelf()
    {
        var s = SettingsResolver.Resolve(Node);
        Protocol = s.Protocol ?? ProtocolKind.Rdp;
        Subtitle = Node is ConnectionNode c ? $"{Protocol} · {c.Hostname}:{s.Port}" : $"{Children.Count} Einträge";
    }

    public IEnumerable<TreeItemViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var d in Children.SelectMany(c => c.SelfAndDescendants()))
            yield return d;
    }

    public bool IsAncestorOf(TreeItemViewModel other)
    {
        for (var p = other.Parent; p is not null; p = p.Parent)
            if (p == this) return true;
        return false;
    }
}

/// <summary>Linke Seitenleiste: Baum, Echtzeit-Filter, Drag &amp; Drop, Bearbeiten.</summary>
public sealed partial class SessionTreeViewModel : ObservableObject
{
    private ConnectionTree _tree = new();

    public ObservableCollection<TreeItemViewModel> Items { get; } = [];
    public ConnectionTree Tree => _tree;

    /// <summary>Strukturänderung (Verschieben, Anlegen, Löschen, Umbenennen) – Auslöser für das Speichern.</summary>
    public event EventHandler? Changed;
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private TreeItemViewModel? _selectedItem;
    [ObservableProperty] private int _visibleConnectionCount;

    public void Load(ConnectionTree tree)
    {
        _tree = tree;
        Items.Clear();
        foreach (var child in tree.Root.Children)
            Items.Add(new TreeItemViewModel(child, null));
        ApplyFilter();
        OnPropertyChanged(nameof(Tree));
    }

    public IEnumerable<TreeItemViewModel> AllItems => Items.SelectMany(i => i.SelfAndDescendants());

    public TreeItemViewModel? FindByNode(TreeNode node) => AllItems.FirstOrDefault(i => i.Node == node);

    // ---- Filter (Strg+F) --------------------------------------------------------------------------

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// Alle Suchbegriffe (durch Leerzeichen getrennt) müssen in Name, Hostname, Beschreibung,
    /// Benutzer oder Protokoll vorkommen. Passt ein Ordner selbst, wird sein ganzer Inhalt gezeigt.
    /// Ordner mit Treffern werden vorübergehend aufgeklappt.
    /// </summary>
    public void ApplyFilter()
    {
        var terms = FilterText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in Items)
            Apply(item, terms, ancestorMatched: false);
        VisibleConnectionCount = AllItems.Count(i => !i.IsFolder && i.IsVisible);
    }

    private static bool Apply(TreeItemViewModel item, string[] terms, bool ancestorMatched)
    {
        var filtering = terms.Length > 0;
        var selfMatch = filtering && Matches(item, terms);
        var anyChildVisible = false;
        foreach (var child in item.Children)
            anyChildVisible |= Apply(child, terms, ancestorMatched || selfMatch);

        item.IsMatch = selfMatch;
        item.IsVisible = !filtering || selfMatch || ancestorMatched || anyChildVisible;

        if (item.Node is FolderNode folder)
            item.SetExpandedTransient(filtering ? anyChildVisible || folder.IsExpanded : folder.IsExpanded);

        return item.IsVisible;
    }

    private static bool Matches(TreeItemViewModel item, string[] terms)
    {
        var s = item.Node.Settings;
        var haystack = string.Join('\u0001',
            item.Node.Name, (item.Node as ConnectionNode)?.Hostname, s.Description, s.Username, s.EnvironmentTags, item.Protocol);
        return terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Drag & Drop ------------------------------------------------------------------------------

    public bool CanMove(TreeItemViewModel source, TreeItemViewModel? target, DropPosition position)
    {
        if (target is null) return true; // auf leere Fläche ⇒ ans Ende der obersten Ebene
        if (source == target) return false;
        if (source.IsAncestorOf(target)) return false; // Ordner nicht in eigenen Unterordner
        return position != DropPosition.Into || target.IsFolder;
    }

    /// <summary>Verschiebt im Modell und im ViewModel; liefert false, wenn unzulässig.</summary>
    public bool Move(TreeItemViewModel source, TreeItemViewModel? target, DropPosition position)
    {
        if (!CanMove(source, target, position)) return false;

        TreeItemViewModel? newParent;
        int index;
        if (target is null)
        {
            newParent = null;
            index = Items.Count;
        }
        else if (position == DropPosition.Into)
        {
            newParent = target;
            index = target.Children.Count;
        }
        else
        {
            newParent = target.Parent;
            index = SiblingsOf(target).IndexOf(target) + (position == DropPosition.After ? 1 : 0);
        }

        // Index korrigieren, wenn innerhalb derselben Liste nach hinten verschoben wird
        var oldSiblings = SiblingsOf(source);
        var newSiblings = newParent?.Children ?? Items;
        var oldIndex = oldSiblings.IndexOf(source);
        if (oldSiblings == newSiblings && oldIndex < index) index--;

        oldSiblings.RemoveAt(oldIndex);
        newSiblings.Insert(index, source);
        source.Parent = newParent;

        var parentFolder = (FolderNode?)newParent?.Node ?? _tree.Root;
        parentFolder.Insert(index, source.Node);

        if (newParent is not null) newParent.IsExpanded = true;
        source.RefreshDisplay();
        ApplyFilter();
        NotifyChanged();
        return true;
    }

    private ObservableCollection<TreeItemViewModel> SiblingsOf(TreeItemViewModel item) => item.Parent?.Children ?? Items;

    // ---- Bearbeiten -------------------------------------------------------------------------------

    /// <summary>Legt einen Knoten im ausgewählten Ordner (bzw. neben der ausgewählten Verbindung) an.</summary>
    public TreeItemViewModel Add(TreeNode node)
    {
        var parentItem = SelectedItem switch
        {
            { IsFolder: true } f => f,
            { } c => c.Parent,
            null => null,
        };
        var parentFolder = (FolderNode?)parentItem?.Node ?? _tree.Root;
        parentFolder.Add(node);

        var vm = new TreeItemViewModel(node, parentItem);
        (parentItem?.Children ?? Items).Add(vm);
        if (parentItem is not null) parentItem.IsExpanded = true;
        SelectedItem = vm;
        ApplyFilter();
        NotifyChanged();
        return vm;
    }

    public void Delete(TreeItemViewModel item)
    {
        (item.Node.Parent ?? _tree.Root).Remove(item.Node);
        SiblingsOf(item).Remove(item);
        if (SelectedItem == item || (SelectedItem is not null && item.IsAncestorOf(SelectedItem)))
            SelectedItem = null;
        ApplyFilter();
        NotifyChanged();
    }
}
