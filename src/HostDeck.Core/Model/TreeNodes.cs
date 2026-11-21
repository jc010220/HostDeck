namespace HostDeck.Core.Model;

/// <summary>Basisklasse für Ordner und Verbindungen im Session-Tree.</summary>
public abstract class TreeNode
{
    protected TreeNode(string name, Guid? id = null)
    {
        Name = name;
        Id = id ?? Guid.NewGuid();
    }

    public Guid Id { get; }
    public string Name { get; set; }
    public FolderNode? Parent { get; internal set; }

    /// <summary>Eigene (nicht aufgelöste) Einstellungen. <c>null</c>-Felder werden geerbt.</summary>
    public ConnectionSettings Settings { get; set; } = new();

    /// <summary>
    /// Fremdattribute, die HostDeck (noch) nicht typisiert abbildet, z.B. "mrng:VaultOpenbaoRole".
    /// Sie werden gespeichert und beim mRemoteNG-Export unverändert zurückgeschrieben.
    /// </summary>
    public Dictionary<string, string> Extensions { get; } = new(StringComparer.Ordinal);

    /// <summary>Elternordner, vom direkten Elternteil aufwärts bis zur Wurzel.</summary>
    public IEnumerable<FolderNode> Ancestors()
    {
        for (var p = Parent; p is not null; p = p.Parent)
            yield return p;
    }

    /// <summary>Pfad ohne den unsichtbaren Wurzelordner, z.B. "Kunden/ACME/web01".</summary>
    public string Path => string.Join('/', Ancestors().Where(a => a.Parent is not null).Reverse().Select(a => a.Name).Append(Name));

    public override string ToString() => Path;
}

public sealed class FolderNode : TreeNode
{
    private readonly List<TreeNode> _children = [];

    public FolderNode(string name, Guid? id = null) : base(name, id) { }

    public bool IsExpanded { get; set; }
    public IReadOnlyList<TreeNode> Children => _children;

    public T Add<T>(T child) where T : TreeNode => Insert(_children.Count, child);

    public T Insert<T>(int index, T child) where T : TreeNode
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child == this || (child is FolderNode f && Ancestors().Contains(f)))
            throw new InvalidOperationException("A folder cannot be moved into itself.");
        child.Parent?._children.Remove(child);
        if (child.Parent == this && index > _children.Count) index = _children.Count;
        _children.Insert(index, child);
        child.Parent = this;
        return child;
    }

    public bool Remove(TreeNode child)
    {
        if (!_children.Remove(child)) return false;
        child.Parent = null;
        return true;
    }

    /// <summary>Alle Nachfahren (Tiefensuche, Dokumentreihenfolge).</summary>
    public IEnumerable<TreeNode> Descendants()
    {
        foreach (var child in _children)
        {
            yield return child;
            if (child is FolderNode folder)
                foreach (var d in folder.Descendants())
                    yield return d;
        }
    }
}

public sealed class ConnectionNode : TreeNode
{
    public ConnectionNode(string name, Guid? id = null) : base(name, id) { }

    /// <summary>Hostname/IP – wie in mRemoteNG bewusst <b>nicht</b> vererbbar.</summary>
    public string Hostname { get; set; } = "";
}

/// <summary>Ein kompletter Verbindungsbestand inkl. Credential-Vault.</summary>
public sealed class ConnectionTree
{
    public ConnectionTree(string rootName = "Connections")
    {
        Root = new FolderNode(rootName) { IsExpanded = true };
    }

    /// <summary>Unsichtbarer Wurzelordner. Seine Settings sind die globalen Benutzer-Defaults.</summary>
    public FolderNode Root { get; }

    public CredentialVault Vault { get; } = new();

    public IEnumerable<ConnectionNode> Connections => Root.Descendants().OfType<ConnectionNode>();
    public IEnumerable<FolderNode> Folders => Root.Descendants().OfType<FolderNode>();

    public TreeNode? Find(Guid id) => id == Root.Id ? Root : Root.Descendants().FirstOrDefault(n => n.Id == id);

    /// <summary>Erster Knoten mit dem Pfad "Ordner/Unterordner/Name".</summary>
    public TreeNode? FindByPath(string path) => Root.Descendants().FirstOrDefault(n => n.Path == path);
}
