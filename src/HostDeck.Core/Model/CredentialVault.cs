namespace HostDeck.Core.Model;

/// <summary>
/// Zentral gepflegte Zugangsdaten, die beliebig vielen Ordnern/Verbindungen über
/// <see cref="ConnectionSettings.CredentialId"/> zugewiesen werden können.
/// </summary>
public sealed class Credential
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Privater SSH-Schlüssel (OpenSSH/PEM) – optional.</summary>
    public string? PrivateKey { get; set; }
    public string? PrivateKeyPassphrase { get; set; }
    public string Notes { get; set; } = "";

    /// <summary>Anzeige "DOMAIN\user" bzw. "user".</summary>
    public string DisplayUser => string.IsNullOrEmpty(Domain) ? Username : $"{Domain}\\{Username}";
}

public sealed class CredentialVault
{
    private readonly Dictionary<Guid, Credential> _items = [];

    public IReadOnlyCollection<Credential> Items => _items.Values;
    public int Count => _items.Count;

    public Credential Add(Credential credential)
    {
        if (!_items.TryAdd(credential.Id, credential))
            throw new InvalidOperationException($"Credential {credential.Id} existiert bereits.");
        return credential;
    }

    public Credential? Get(Guid id) => _items.GetValueOrDefault(id);
    public Credential? Get(Guid? id) => id is { } g ? Get(g) : null;
    public bool Remove(Guid id) => _items.Remove(id);
}
