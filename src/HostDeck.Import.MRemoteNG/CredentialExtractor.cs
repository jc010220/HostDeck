using HostDeck.Core.Model;

namespace HostDeck.Import.MRemoteNG;

/// <summary>
/// Überführt explizit gesetzte Zugangsdaten (Username + Domain + Passwort an demselben Knoten)
/// in zentrale Vault-Einträge und ersetzt sie durch <see cref="ConnectionSettings.CredentialId"/>.
/// Teilweise geerbte Kombinationen bleiben unverändert inline.
/// </summary>
public static class CredentialExtractor
{
    public static int Extract(ConnectionTree tree, CredentialExtractionMode mode)
    {
        if (mode == CredentialExtractionMode.Disabled) return 0;

        var groups = tree.Root.Descendants()
            .Where(n => n.Settings is { Username: not null, Domain: not null, Password: not null, CredentialId: null }
                        && (n.Settings.Username.Length > 0 || n.Settings.Password.Length > 0))
            .GroupBy(n => (n.Settings.Username!, n.Settings.Domain!, n.Settings.Password!))
            .Where(g => mode == CredentialExtractionMode.All || g.Count() >= 2)
            .OrderBy(g => g.Key.Item2, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var usedNames = new HashSet<string>(tree.Vault.Items.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var (user, domain, password) = group.Key;
            var credential = tree.Vault.Add(new Credential
            {
                Name = UniqueName(string.IsNullOrEmpty(domain) ? (user.Length > 0 ? user : "(no user)") : $"{domain}\\{user}", usedNames),
                Username = user,
                Domain = domain,
                Password = password,
                Notes = $"Imported from mRemoteNG, used by {group.Count()} nodes.",
            });

            foreach (var node in group)
            {
                node.Settings.CredentialId = credential.Id;
                node.Settings.Username = null;
                node.Settings.Domain = null;
                node.Settings.Password = null;
            }
        }
        return groups.Count;
    }

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        var name = baseName;
        for (var i = 2; !used.Add(name); i++)
            name = $"{baseName} ({i})";
        return name;
    }
}
