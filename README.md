# HostDeck

Moderne, schnelle Alternative zu mRemoteNG mit MobaXterm-artiger Bedienung (WPF, .NET 10).

**Stand:** Phase 1 (Architektur, Datenmodell), Phase 2 (mRemoteNG-Import/Export inkl. Passwort-Entschlüsselung) Phase 2b (verschlüsselte Datenbank mit Master-Passwort) und Phase 3 (UI-Grundgerüst: Seitenleiste, Quick-Connect, Tabs, Split-Screen) sind fertig. Phase 4a (SSH-Terminal mit xterm.js, SFTP-Browser, Multi-Exec, Highlighting) ist fertig; Phase 4b (RDP über den nativen Windows-Client) ist implementiert. Architektur siehe [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Auf Windows 11 starten

1. `HostDeck.exe` an einen beliebigen Ort kopieren (z. B. `C:\Tools\HostDeck\`) und starten.
   Es wird kein .NET benötigt (self-contained). Die Datei ist nicht signiert – Windows SmartScreen zeigt
   beim ersten Start „Unbekannter Herausgeber“ → *Weitere Informationen* → *Trotzdem ausführen*.
2. Master-Passwort festlegen (mind. 8 Zeichen). Optional „Auf diesem PC merken“.
3. Ist mRemoteNG installiert, bietet HostDeck die Übernahme von `%APPDATA%\mRemoteNG\confCons.xml` an
   (sonst: *Import* in der Werkzeugleiste).

Daten liegen in `%APPDATA%\HostDeck\` (`connections.hdk` + `connections.hdk.key` – beide sichern!),
das Terminal-Profil von WebView2 in `%LOCALAPPDATA%\HostDeck\WebView2`.

### Bedienung

| Taste | Aktion |
|---|---|
| **Strg+K** | **Schnellsuche**: Gerät suchen (Name, IP, Ordner, Tag, Beschreibung – auch unscharf: „acw1“ → „acme-web-01“), `ssh user@host` direkt verbinden, `>` für Befehle |
| Strg+Enter (in der Suche) | im Split-Screen öffnen |
| Strg+L | Quick-Connect-Feld |
| Strg+F | Filter im Baum |
| F2 / F4 / Entf | Umbenennen / Eigenschaften / Löschen |
| Strg+Tab, Strg+W | Tabs wechseln / schließen (im Terminal: Strg+Umschalt+W) |
| Strg+Alt+1…4 | Layout 1 / 2 nebeneinander / 2 untereinander / 4er-Grid |
| Strg+Umschalt+M | Multi-Execution |

Offene Sessions werden beim Beenden gemerkt und beim nächsten Start wiederhergestellt. Benannte
Arbeitsbereiche: Strg+K → `>arbeitsbereich speichern`.

## Bauen und Testen

```bash
dotnet test
```

Voraussetzung: .NET SDK 10.0.100+. Alles außer `HostDeck.App` ist plattformneutral (`net10.0`) und auch unter Linux testbar.

App starten (Windows 10/11):

```bash
dotnet run --project src/HostDeck.App
```

Eigenständige EXE ohne installiertes .NET:

```bash
dotnet publish src/HostDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## mRemoteNG-Import verwenden

```csharp
var importer = new ConfConsImporter(new ConfConsImportOptions
{
    // UI-Dialog; wird nur aufgerufen, wenn die Datei ein eigenes Master-Passwort hat
    MasterPasswordProvider = req => PasswordDialog.Show(req.SourceName, req.PreviousAttemptFailed),
    CredentialExtraction = CredentialExtractionMode.SharedOnly, // gleiche Logins → Credential-Vault
});

ConfConsImportResult result = importer.ImportFile(@"%APPDATA%\mRemoteNG\confCons.xml");
// result.Tree        → ConnectionTree (Ordner, Verbindungen, Vault)
// result.Warnings    → nicht kritische Probleme (z. B. einzelnes Passwort nicht entschlüsselbar)
// result.File        → ConfVersion, Verfahren, Full-File-Encryption, …

var effective = SettingsResolver.Resolve(node);                    // vererbte Einstellungen
var login     = CredentialResolver.Resolve(node, result.Tree.Vault); // User/Domain/Passwort

// Zurück nach mRemoteNG:
new ConfConsExporter(new ConfConsExportOptions { MasterPassword = "…", ConfVersion = 2.8 })
    .ExportFile(result.Tree, "confCons.xml");
```

## Unterstützte confCons.xml-Varianten (getestet)

| Variante | Test-Fixture |
|---|---|
| v2.5, Legacy AES-CBC | `confCons_v2_5.xml` |
| v2.5, komplette Datei verschlüsselt (Standard- und eigenes Passwort) | `confCons_v2_5_*fullencryption.xml` |
| v2.6, AES-256-GCM | `confCons_v2_6.xml` |
| v2.6, 5000 KDF-Iterationen | `confCons_v2_6_5k-iterations.xml` |
| v2.6, Full-File-Encryption | `confCons_v2_6_fullencryption.xml` |
| v2.6, eigenes Master-Passwort (+ Full-File) | `confCons_v2_6_passwordis_Password*.xml` |
| 2.7 – 2.9 (Attribute, Enum-Änderungen), Serpent/Twofish, GCM/CCM/EAX | synthetische Tests |

Die Fixtures stammen aus dem mRemoteNG-Repository (`mRemoteNGTests/Resources`, GPL-2.0) und wurden von mRemoteNG selbst erzeugt. Damit sind sie unabhängige Testvektoren.
