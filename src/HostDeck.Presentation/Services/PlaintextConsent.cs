using System.Text.Json;

namespace HostDeck.Presentation.Services;

/// <summary>
/// Zustimmung, ein gespeichertes Passwort über eine unverschlüsselte Verbindung (Telnet, HTTP) zu senden.
/// Wird je Eintrag und Host gemerkt (verschlüsselt in der Datenbank); ändert sich der Host, wird erneut gefragt.
/// </summary>
public static class PlaintextConsent
{
    public const string Document = "plaintext-consent";

    public static bool Ask(IConnectionStore store, IDialogService dialogs, Guid connectionId, string host, string protocol,
        Func<Func<bool>, bool> onUi)
    {
        var key = $"{connectionId}|{host.Trim().ToLowerInvariant()}|{protocol}";
        var granted = Load(store);
        if (granted.Contains(key)) return true;
        var ok = onUi(() => dialogs.Confirm($"Unencrypted {protocol} connection",
            $"{protocol} sends everything unencrypted – including your stored password for {host}. " +
            "Anyone on the network path can read it.\n\n" +
            "Send the stored password anyway? (Your answer is remembered for this entry.)\n\n" +
            "“No”: connect without sending it – you can type the password yourself."));
        if (!ok) return false;
        granted.Add(key);
        store.WriteDocument(Document, JsonSerializer.Serialize(granted));
        return true;
    }

    private static HashSet<string> Load(IConnectionStore store)
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(store.ReadDocument(Document) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }
}
