# HostDeck

Verbindungsmanager für Windows 11 – mRemoteNG-kompatibel, mit MobaXterm-artiger Bedienung.
RDP · SSH (+ SFTP) · Telnet · Raw-TCP · VNC · HTTP/HTTPS · verschlüsselte Datenbank · Schnellsuche.

## Installation

**Empfohlen:** `HostDeck-win-Setup.exe` aus dem [neuesten Release](../../releases/latest) starten.
Installiert pro Benutzer (keine Adminrechte), legt einen Startmenü-Eintrag an und kann automatisch aktualisieren.
Alternativ `HostDeck-win-Portable.zip` entpacken (ohne automatische Updates).

Solange der Installer nicht signiert ist, zeigt Windows SmartScreen „Unbekannter Herausgeber“ →
*Weitere Informationen* → *Trotzdem ausführen* (siehe [Code-Signierung](#code-signierung)).

Beim ersten Start: Master-Passwort festlegen (mind. 8 Zeichen, optional „auf diesem PC merken“).
Ist mRemoteNG installiert, wird die Übernahme von `%APPDATA%\mRemoteNG\confCons.xml` angeboten.

## Funktionen

| Bereich | |
|---|---|
| **Protokolle** | RDP (nativer Windows-Client, Live-Auflösung, DPI-Skalierung, RD Gateway, Restricted Admin), SSH (Passwort/Schlüssel/OTP, Host-Key-Prüfung), Telnet (Auto-Login für Netzwerkgeräte), Raw-TCP, VNC (eigener Client: ZRLE/Hextile), HTTP/HTTPS (WebView2, Anmeldung aus dem Vault) |
| **Bedienung** | Schnellsuche **Strg+K** (unscharf über Name, IP, Ordner, Tags; Befehle mit `>`), Quick-Connect `ssh user@host`, Filter, Drag & Drop, Favoriten, zuletzt verwendet |
| **Fenster** | Tabs, Split-Screen 1/2/2/4 mit verschiebbaren Trennern, Tabs in eigene Fenster ziehen (zweiter Monitor), Arbeitsbereiche speichern und beim Start wiederherstellen |
| **Terminal** | xterm.js: Farben, Unicode, Suche, Zoom, Markieren = Kopieren, Rechtsklick = Einfügen, Syntax-Highlighting (Fehler/Warnungen/IPs), Multi-Execution, Snippets mit Platzhaltern |
| **SFTP** | Seitenleiste folgt automatisch `cd` im Terminal, Upload per Drag & Drop, Download, Umbenennen, Löschen |
| **Sicherheit** | AES-256-verschlüsselte Datenbank (SQLCipher v4) mit Argon2id, Credential-Vault, sichere RDP-Standards, Import-Sicherheitsprüfung, kein Passwort im Klartext auf der Platte |
| **mRemoteNG** | Import inkl. Passwort-Entschlüsselung (alle Formate 2.5–2.9, Master-Passwort, Full-File-Encryption), Export zurück |
| **Betrieb** | Online-Status im Baum, Sicherung, automatische Updates (Netzwerkfreigabe/HTTP/GitHub), Fehlerprotokoll |

### Tastenkürzel

| Taste | Aktion |
|---|---|
| **Strg+K** (im Terminal Strg+Umschalt+K) | Schnellsuche – Enter verbinden, Strg+Enter im Split-Screen, Alt+Enter im Baum zeigen |
| Strg+L / Strg+F | Quick-Connect / Filter im Baum |
| F2 / F4 / Entf | Umbenennen / Eigenschaften / Löschen |
| Strg+S | Team-Bibliothek synchronisieren |
| Strg+D (im Baum) | Verbindung duplizieren – Eigenschaften öffnen sich direkt zum Anpassen von Name und Host |
| Strg+N / Strg+Umschalt+N | Neue Verbindung / Neuer Ordner |
| Strg+Tab, Strg+W (Terminal: Strg+Umschalt+W) | Tab wechseln / schließen |
| Strg+Alt+1…4 | Layout |
| Strg+Umschalt+M | Multi-Execution |
| Strg+Umschalt+B | Seitenleiste ein-/ausklappen (auch über das Symbol ganz links in der Symbolleiste) |
| Esc / Strg+C (Terminal), „Abbrechen“ in der Statusleiste | Laufenden Verbindungsaufbau abbrechen |
| Terminal: Strg+Umschalt+C/V/F, Strg+Mausrad | Kopieren / Einfügen / Suchen / Zoom |

## Team-Bibliothek (gemeinsame confCons.xml)

Wie eine gemeinsame KeePass-Datei: Alle arbeiten auf derselben `confCons.xml` auf dem Netzlaufwerk.

- **Einrichten:** Strg+K → „Team-Bibliothek verbinden …“ (oder *Einstellungen → Team-Bibliothek*). Im Baum erscheint der Ordner **Team**.
- **Änderungen anderer** werden automatisch übernommen (Prüfung alle 15 s), eigene, noch nicht gespeicherte bleiben dabei erhalten.
- **Eigene Änderungen** speichern mit **Strg+S** (oder Knopf „Team“ in der Symbolleiste, die Zahl zeigt offene Änderungen).
  HostDeck führt pro Eintrag zusammen: Wer was geändert hat, bleibt erhalten. Haben zwei Personen *denselben* Eintrag
  unterschiedlich geändert, fragt HostDeck nach. Gelöscht und gleichzeitig woanders geändert ⇒ bleibt erhalten.
- **Sicher speichern:** Sperrdatei gegen gleichzeitiges Schreiben, atomares Ersetzen, vorheriger Stand wird verschlüsselt
  in der HostDeck-Datenbank gesichert (die letzten 10). Den Team-Ordner zu löschen trennt nur die Verbindung – die Datei bleibt.
- **Persönliche Logins (z. B. RADIUS):** einmal im Vault anlegen und einem Team-Ordner/-Eintrag zuweisen. Diese Zuordnung
  bleibt lokal und wird nie in die gemeinsame Datei geschrieben. Gemeinsame Passwörter in der Datei bleiben wie bisher.
- **Jumphost ohne Laufwerkszugriff:** Datei per RDP in einen festen Ordner kopieren (z. B. `C:\HostDeck\Team`) und diesen
  Ordner als Team-Bibliothek mit „Nur lesen“ eintragen – die neueste `.xml` darin wird automatisch geladen.
- **mRemoteNG-kompatibel:** Format, Verschlüsselung und Master-Passwort der Datei bleiben erhalten. Hinweis: mRemoteNG
  selbst führt nicht zusammen – speichert ein Kollege mit mRemoteNG, überschreibt er die Datei mit seinem Stand.
- **Wichtig:** Ohne eigenes Master-Passwort verwendet mRemoteNG einen öffentlich bekannten Schlüssel – Passwörter in der
  Datei sind dann für jeden mit Lesezugriff lesbar. Master-Passwort setzen (HostDeck fragt es einmal ab) oder persönliche
  Logins im Vault verwenden.

## Sicherheit

- Datenbank: SQLCipher (AES-256), Schlüssel per Argon2id (64 MiB, 3 Durchläufe) aus dem Master-Passwort; optional
  zusätzlich an den Windows-Benutzer gebunden (DPAPI, „auf diesem PC merken“).
- Keine Geheimnisse in `settings.json` oder im Protokoll; Update-Token und Team-Passwort liegen verschlüsselt in der Datenbank.
- SSH: Host-Keys werden geprüft (Warnung bei Änderung), SFTP nutzt denselben bestätigten Key. Ein gespeichertes Passwort
  wird nur einmal gesendet.
- SFTP-Downloads: Dateinamen vom Server werden bereinigt (kein Schreiben außerhalb des Zielordners), vorhandene Dateien
  nie überschrieben, Downloads als „aus dem Internet“ markiert (SmartScreen-Warnung bei Programmen).
- RDP: Server-Zertifikat wird geprüft (Warnung), Laufwerke/Drucker/Ports werden standardmäßig nicht umgeleitet.
- Terminal: mehrzeiliges Einfügen nur nach Rückfrage; Links öffnen nur http(s) im Standardbrowser.
- Web-Tabs: nur http(s), keine externen Protokoll-Handler, gespeicherte Zugangsdaten nur an das eingetragene Gerät.

## Daten und Sicherung

`%APPDATA%\HostDeck\connections.hdk` + `connections.hdk.key` (beide sichern – oder *Sicherung erstellen*),
Einstellungen `settings.json`, Fehlerprotokoll `%LOCALAPPDATA%\HostDeck\logs`.

## Updates im Unternehmen

1. Release erstellen: GitHub → *Actions* → *Release* → *Run workflow* (Version eingeben) – oder einen Tag `v1.0.1` pushen.
   GitHub Actions baut Installer und Update-Pakete und veröffentlicht sie als Release.
2. Release-Dateien (`RELEASES`, `releases.win.json`, `*.nupkg`, `HostDeck-win-Setup.exe`) auf eine Freigabe kopieren,
   z. B. `\\server\software\HostDeck`.
3. In HostDeck: *Einstellungen → Updates* → Freigabe eintragen. Updates werden im Hintergrund geladen und beim
   Beenden installiert.

## Code-Signierung

Der Release-Workflow signiert automatisch, sobald eines davon als Repository-Secret hinterlegt ist:
`SIGN_PFX_BASE64` + `SIGN_PFX_PASSWORD` (Code-Signing-Zertifikat) **oder** `AZURE_TRUSTED_SIGNING_JSON`
(+ `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `AZURE_TENANT_ID`) für Azure Trusted Signing.

## Entwicklung

```bash
dotnet test
```

```bash
dotnet run --project src/HostDeck.App
```

Architektur: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). Integrationstests gegen echte Server über
`HOSTDECK_TEST_SSH*` und `HOSTDECK_TEST_VNC*` (siehe Testklassen).
