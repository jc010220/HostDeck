using HostDeck.Core.Model;

namespace HostDeck.Core.Inheritance;

public sealed record ResolvedCredentials(
    string Username,
    string Domain,
    string Password,
    Credential? VaultEntry,
    TreeNode? UsernameOrigin);

/// <summary>
/// Löst Benutzername/Domäne/Passwort auf – unter Berücksichtigung des Credential-Vaults.
///
/// Regel: Für jedes Feld wird die Kette Knoten → Eltern → … durchlaufen. Der <b>erste</b> Knoten,
/// der entweder einen gültigen <see cref="ConnectionSettings.CredentialId"/> oder einen expliziten
/// Inline-Wert hat, gewinnt. Am selben Knoten hat der Vault-Verweis Vorrang.
/// Dadurch verhält sich "Credential am Ordner zuweisen" wie mRemoteNG-Vererbung, ein Override
/// direkt an der Verbindung bleibt aber möglich.
/// </summary>
public static class CredentialResolver
{
    public static ResolvedCredentials Resolve(TreeNode node, CredentialVault vault, ConnectionSettings? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(vault);
        defaults ??= ConnectionSettings.Defaults;

        var (user, userVault, userOrigin) = Pick(node, vault, s => s.Username, c => c.Username);
        var (domain, domainVault, _) = Pick(node, vault, s => s.Domain, c => c.Domain);
        var (password, passwordVault, _) = Pick(node, vault, s => s.Password, c => c.Password);

        return new ResolvedCredentials(
            user ?? defaults.Username ?? "",
            domain ?? defaults.Domain ?? "",
            password ?? defaults.Password ?? "",
            userVault ?? passwordVault ?? domainVault,
            userOrigin);
    }

    private static (string? Value, Credential? Vault, TreeNode? Origin) Pick(
        TreeNode node, CredentialVault vault, Func<ConnectionSettings, string?> inline, Func<Credential, string> fromVault)
    {
        for (TreeNode? n = node; n is not null; n = n.Parent)
        {
            if (vault.Get(n.Settings.CredentialId) is { } credential)
                return (fromVault(credential), credential, n);
            if (inline(n.Settings) is { } value)
                return (value, null, n);
        }
        return (null, null, null);
    }
}
