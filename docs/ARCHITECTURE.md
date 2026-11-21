# HostDeck – Architektur (Phase 1)

Ziel: mRemoteNG-kompatible Verbindungsverwaltung mit MobaXterm-artiger Bedienung, nativ für Windows 10/11, High-DPI-fähig.

## 1. Tech-Stack-Entscheidung

| Bereich | Wahl | Begründung |
|---|---|---|
| Runtime | **.NET 10 (LTS, Support bis Nov. 2028)** | .NET 8 ist seit 10.11.2026 aus dem Support, .NET 9 bereits davor. |
| UI | **WPF + WPF-UI 4.x (Fluent, Mica, Dark Mode)** | Vektorbasiert, Per-Monitor-V2-DPI. Ausgereiftes Hosting für ActiveX (`WindowsFormsHost`) und WebView2. |
| Tabs/Split | **Eigenes Pane-Grid** (1 / 2 nebeneinander / 2 untereinander / 4er-Grid, `GridSplitter`) | Ursprünglich AvalonDock geplant – verworfen: Umhängen von HWND-Inhalten (RDP-ActiveX, WebView2) beim Docken ist fehleranfällig. Das eigene Modell ist einfacher und vollständig unit-testbar. |
| MVVM/DI | CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting | Source Generators, testbare ViewModels. |
| RDP | Nativer ActiveX `MsRdpClient` (mstscax.dll) per selbst erzeugtem Interop (`aximp`) | Volle Performance, Zwischenablage, Audio, Laufwerke, Smartcards, RD Gateway. |
| SSH/SFTP | **SSH.NET** + **xterm.js in WebView2** | xterm.js ist die ausgereifteste Terminal-Emulation (VS Code nutzt sie), GPU-Rendering, Addons für Suche und Links. |
| Speicher | SQLite im **SQLCipher-v4-Format** (AES-256-CBC + HMAC-SHA512) über *SQLite3 Multiple Ciphers*, Argon2id für das Master-Passwort | Komplett verschlüsselte Datei, transaktionssicher. `e_sqlite3mc` ist der empfohlene Nachfolger der veralteten `e_sqlcipher`-Builds. |
| mRemoteNG-Krypto | BouncyCastle.Cryptography | mRemoteNG nutzt AES-GCM mit **128-bit-Nonce**. `System.Security.Cryptography.AesGcm` erlaubt nur 96 bit. |

**Warum nicht Avalonia?** Das Produkt ist ausdrücklich Windows-only, und der Kern (RDP-ActiveX) ist Windows-only. In Avalonia wäre ActiveX-Hosting nur per `NativeControlHost` mit eigener HWND-Verwaltung möglich, ohne fertigen Docking-Ersatz für AvalonDock. Die Geschäftslogik (`HostDeck.Core`, `HostDeck.Import.*`, Storage, SSH) ist UI-frei auf `net10.0` gebaut. Ein späterer Avalonia-Port müsste also nur die UI-Schicht ersetzen.

**Bekannte WPF-Falle („Airspace“):** HWND-Inhalte (RDP-ActiveX, WebView2) werden immer über WPF gezeichnet. Deshalb gilt: keine WPF-Popups über dem Session-Bereich, Overlays als eigene Top-Level-Fenster. WebView2 kann im Composition-Modus laufen.

## 2. Projektstruktur

```
HostDeck.slnx
├─ Directory.Build.props / Directory.Packages.props   (zentrale Versionen, Warnings = Errors)
├─ src/
│  ├─ HostDeck.Core                 net10.0   Datenmodell, Vererbung, Vault          ✅ Phase 1
│  ├─ HostDeck.Import.MRemoteNG     net10.0   confCons.xml Import/Export + Krypto    ✅ Phase 2
│  ├─ HostDeck.Presentation         net10.0   ViewModels, Quick-Connect-Parser, Settings ✅ Phase 3
│  ├─ HostDeck.Storage              net10.0   Verschlüsselte DB, Master-Passwort       ✅ Phase 2b
│  ├─ HostDeck.Protocols.Abstractions net10.0 ISessionHost, IProtocolEngine           Phase 4
│  ├─ HostDeck.Protocols.Ssh        net10.0   SSH.NET, SFTP, CWD-Tracking (OSC 7)      ✅ Phase 4a
│  ├─ HostDeck.Protocols.Rdp        net10.0   RdpPlan (Einstellungen → RDP-Client)    ✅ Phase 4b
│  └─ HostDeck.App                  net10.0-windows  WPF-Shell, WPF-UI (Fluent/Mica)   ✅ Phase 3
└─ tests/
   └─ HostDeck.Tests                xUnit v3 (Microsoft.Testing.Platform)
```

## 3. Datenmodell

```
ConnectionTree
 ├─ Root : FolderNode            (unsichtbar, Settings = globale Benutzer-Defaults)
 │   └─ Children : TreeNode[]    FolderNode | ConnectionNode
 └─ Vault : CredentialVault      Credential { Id, Name, Username, Domain, Password, PrivateKey, … }

TreeNode    { Id (Guid), Name, Parent, Settings : ConnectionSettings, Extensions : Dictionary }
FolderNode  { Children, IsExpanded }
ConnectionNode { Hostname }      (Hostname ist – wie in mRemoteNG – nicht vererbbar)

ConnectionSettings  (alle Felder nullable)
 ├─ Allgemein: Protocol, Port, Description, Icon, Panel, Color, TabColor, Favorite, …
 ├─ Zugang:    CredentialId, Username, Domain, Password
 ├─ Ssh  : SshSettings     PuttySession, OpeningCommand, Options, TunnelConnectionName
 ├─ Rdp  : RdpSettings     Auflösung, Farben, Redirects, CredSSP, … , Gateway : RdGatewaySettings
 └─ Vnc  : VncSettings
```

### Vererbung

* **`null` = erben, Wert = explizit gesetzt.** Pro Feld gibt es also nur einen Zustand statt Wert und `InheritXyz`-Flag wie in mRemoteNG.
* Auflösung pro Feld, auch in verschachtelten Gruppen: `Knoten → Eltern → … → Root → ConnectionSettings.Defaults`.
* Port ohne Wert in der ganzen Kette ⇒ Standardport des Protokolls.
* `SettingsResolver.FindOrigin(node, s => s.X)` liefert, wo ein Wert herkommt (für die UI: „geerbt von Ordner X“).
* **Credential-Vault:** `CredentialId` ist selbst vererbbar. Pro Feld (User, Domain, Passwort) gewinnt der *nächste* Knoten, der entweder einen gültigen Vault-Verweis oder einen Inline-Wert hat. Am selben Knoten hat der Vault Vorrang.

### mRemoteNG-Besonderheiten, die der Importer abbildet

| mRemoteNG | HostDeck |
|---|---|
| `InheritXyz="True"` | Feld = `null` |
| Kinder direkt unter der Wurzel erben **nie** (`InheritanceActive`) | Inherit-Flags dort ignoriert, eigener Wert wird übernommen |
| `Inherit="True"` (sehr alte Dateien) | alle Felder `null` |
| `RedirectDiskDrives` bool (≤ 2.7) / Enum (≥ 2.8) | `RdpDiskDrives` (True ⇒ Local) |
| `Resolution="Res1920x1080"` (alt) | `Fixed` + Breite/Höhe |
| unbekannte Attribute | `Extensions["mrng:…"]`, werden beim Export zurückgeschrieben |
| fehlende/doppelte Ids | neue GUID (wie mRemoteNG) |

## 4. mRemoteNG-Kryptografie (aus dem Original-Quellcode verifiziert)

| ConfVersion | Verfahren | Format (Base64) |
|---|---|---|
| ≤ 2.5 | AES-128-CBC, Key = MD5(UTF-8(pw)) | `iv[16] ‖ ct` |
| ≥ 2.6 | AES/Serpent/Twofish-256 in GCM/CCM/EAX, Key = PBKDF2-HMAC-SHA1(pw, salt, KdfIterations) | `salt[16] ‖ nonce[16] (CCM: 11) ‖ ct ‖ tag[16]`, AAD = salt |
| `KeyProtection="Dpapi"` | Windows DPAPI (CurrentUser) | – |

* Standardschlüssel: `mR3m`. Root-Attribut `Protected` = Encrypt("ThisIsNotProtected" bzw. "ThisIsProtected"). Damit wird das Master-Passwort geprüft.
* `FullFileEncryption="true"`: Der gesamte Inhalt des Root-Elements ist ein einziger Chiffretext.
* Uralt-Dateien: Die komplette Datei ist Legacy-verschlüsselt (kein XML erkennbar).
* Passwort-Bytes wie BouncyCastle `Pkcs5PasswordToBytes`: **1 Byte pro Zeichen**, Zeichen > U+00FF ⇒ mRemoteNG wirft eine Exception.

## 5. Sicherheit

* XML wird ohne DTD und ohne externe Entities geparst (XXE-Schutz, getestet).
* Klartext-Passwörter existieren nur im Arbeitsspeicher. Persistiert wird ausschließlich in der verschlüsselten Datenbank (siehe Abschnitt 7).
* Abgeleitete Schlüssel werden nach Gebrauch genullt (`CryptographicOperations.ZeroMemory`).

## 6. UI-Schicht (Phase 3)

```
MainWindow (FluentWindow, Mica, PerMonitorV2-DPI)
├─ Toolbar: Import/Export · Neu · Layout 1/2/2/4 · Multi-Exec · Theme
├─ Seitenleiste: Quick-Connect (mit Verlauf) · Suche (Strg+F) · TreeView (Drag & Drop)
├─ SplitWorkspace (Grid + GridSplitter)
│   └─ PaneView × 1–4  (eigene Tab-Leiste; Inhalte aller Tabs bleiben geladen)
└─ Statusleiste
```

* **MVVM:** Die gesamte Logik (Filter, Verschieben, Layout, Tabs, Quick-Connect, Import-Ablauf) steckt in
  `HostDeck.Presentation` (plattformneutral, getestet). Code-behind enthält nur Eingabe- und Drag-and-Drop-Verdrahtung.
* **Sessions überleben Tab-Wechsel und Layoutwechsel:** kein `TabControl` (zerstört den Inhalt nicht
  ausgewählter Tabs), PaneViews werden pro Bereich wiederverwendet. Voraussetzung für RDP/SSH in Phase 4.
* **Persistenz:** Strukturänderungen (Verschieben, Neu, Löschen, Import) werden sofort gespeichert,
  der Auf-/Zuklapp-Zustand beim Beenden.

### Tastenkürzel

| Taste | Aktion |
|---|---|
| Strg+F / Esc / ↓ / Enter | Suchen / Filter leeren / in Treffer springen / ersten Treffer öffnen |
| Strg+L | Quick-Connect |
| Strg+O | mRemoteNG importieren |
| Strg+N / Strg+Umschalt+N | Neue Verbindung / Neuer Ordner |
| Enter / Doppelklick / Entf | Verbinden / Verbinden / Löschen (im Baum) |
| Strg+W, Mittelklick | Tab schließen |
| Strg+Tab / Strg+Umschalt+Tab | Nächster / vorheriger Tab |
| Strg+Alt+1…4 | Layout: 1 / 2 nebeneinander / 2 untereinander / 4er-Grid |
| Strg+Alt+→ | Nächster Bereich |
| Strg+Umschalt+M | Multi-Execution an/aus |

## 7. Verschlüsselte Datenbank (Phase 2b)

Dateien unter `%APPDATA%\HostDeck\` (beide gehören in jede Sicherung):

| Datei | Inhalt |
|---|---|
| `connections.hdk` (+ `-wal`, `-shm`) | SQLite, vollständig verschlüsselt im SQLCipher-v4-Format; ohne Schlüssel nicht von Zufallsdaten unterscheidbar |
| `connections.hdk.key` | Schlüsseldatei (JSON): Argon2id-Parameter + Salt, verpackter Datenbankschlüssel, optional Geräte-Slot |

```
Master-Passwort ──Argon2id (64 MiB, t=3, p=4, Salt 128 bit)──► KEK
KEK ──AES-256-GCM──► DEK (zufällig, 256 bit) ──► SQLCipher-Raw-Key (keine zusätzliche PBKDF2)
Windows-DPAPI (CurrentUser) ──► DEK            optionaler Slot „Auf diesem PC merken“
```

* **Passwort ändern** verpackt nur den DEK neu – die Datenbank bleibt unverändert, der Vorgang ist atomar.
* **Passwort vergessen:** keine Hintertür. „Neu beginnen“ benennt die alten Dateien in `*.bak` um, statt sie zu löschen.
* **Schema:** Tabellen `nodes` (Baum, Einstellungen als JSON – geerbte Felder entfallen), `credentials` (Vault), `meta`
  (Schema-Version, Wurzel-Defaults). Gespeichert wird als Gesamtstand in einer Transaktion (5.000 Verbindungen < 1 s).
* **Einzelinstanz:** Ein benannter Mutex verhindert, dass zwei HostDeck-Instanzen dieselbe Datenbank überschreiben.
* Geprüft durch Tests: RFC-9106-Testvektor für Argon2id, keine Klartexte in den Dateien, falsches Passwort und falscher Geräteschlüssel werden abgewiesen.

## 8. SSH, Terminal und SFTP (Phase 4a)

```
TerminalView (WPF) ── WebView2 ── terminal.html + xterm.js 6 (eingebettete Ressourcen, kein CDN)
      │  JSON-Nachrichten: Ausgabe (gebündelt), Eingabe, Größe, Kopieren/Einfügen, Tastenkürzel
TerminalSessionViewModel ── Ausgabepuffer (512 KB, Wiedergabe beim Anmelden der View)
      │                    └─ Multi-Exec-Routing über MainViewModel
SshTerminalSession (SSH.NET) ── PTY xterm-256color · OSC-7-Parser · TerminalHighlighter
SftpSession ── eigene Verbindung, gleicher (bereits bestätigter) Host-Key
```

* **Host-Keys:** Trust on First Use mit Fingerprint-Dialog; geänderte Keys werden deutlich gewarnt und standardmäßig
  abgelehnt. Gespeichert in der verschlüsselten Datenbank (Tabelle `known_hosts`, Schema v2).
* **SFTP folgt dem Terminal:** Über einen Exec-Kanal wird geprüft, ob die Login-Shell bash oder zsh ist. Nur dann wird
  ein Prompt-Hook gesetzt, der das Verzeichnis per OSC 7 meldet. Das Echo des Hook-Befehls wird ausgeblendet; die MOTD
  bleibt erhalten. Auf Netzwerkgeräten (Switch-CLI) wird nichts eingeschleust.
* **Highlighting:** Fehler (rot), Warnungen (gelb), Erfolg (grün), IPv4 (cyan) – nur auf Text, den das Programm nicht
  selbst färbt; Vollbild-Programme (vim, htop) bleiben unangetastet.
* **Bedienung im Terminal:** Markieren = kopieren, Rechtsklick = einfügen (Bracketed Paste), Strg+Umschalt+C/V,
  Strg+Umschalt+F Suche, Strg+Mausrad Schriftgröße, Links per Klick, Enter in getrennter Session verbindet neu.
  App-Kürzel im Terminal: Strg+Tab, Strg+Umschalt+W (Tab schließen; Strg+W bleibt „Wort löschen“ der Shell),
  Strg+Umschalt+M, Strg+Umschalt+L, Strg+Alt+1…4.
* **Integrationstests** laufen gegen einen echten OpenSSH-Server, wenn `HOSTDECK_TEST_SSH`, `HOSTDECK_TEST_SSH_USER`,
  `HOSTDECK_TEST_SSH_PASSWORD` (optional `HOSTDECK_TEST_SSH_KEYFILE`) gesetzt sind.

## 9. RDP (Phase 4b)

Wie mRemoteNG wird der **native Windows-RDP-Client** (ActiveX `MsRdpClient11…7NotSafeForScripting` aus `mstscax.dll`)
eingebettet – ohne Drittanbieter-Paket und ohne `COMReference` (die nur mit Visual-Studio-MSBuild baut):

* `HostDeck.Protocols.Rdp` (plattformneutral, getestet): `RdpPlanBuilder` übersetzt die vererbten Einstellungen in
  einen `RdpPlan` (Eigenschaften je Objekt, NonScriptable-Flags, Laufwerke, DPI).
* `HostDeck.App/Rdp`: eigene COM-Definitionen. CLSIDs, IIDs, DISPIDs und die vtable-Reihenfolge von
  `IMsRdpClientNonScriptable5` wurden gegen die aus der Typbibliothek erzeugten Interop-Metadaten geprüft.
  Settings-Objekte (dual) werden per IDispatch über den Namen gesetzt – robust über Windows-Versionen.

**Sicherheitsvoreinstellungen** (strenger als mRemoteNG):

| Einstellung | HostDeck | mRemoteNG-Standard |
|---|---|---|
| Server-Authentifizierung | warnen bei ungültigem Zertifikat | ohne Prüfung verbinden |
| NLA / CredSSP | an | an |
| Passwort | nur über `IMsTscNonScriptable.ClearTextPassword`, nie in .rdp-Dateien | gleich |
| Kein Passwort gespeichert | sicherer Windows-Anmeldedialog (CredUI) | Fehlschlag/Prompt |
| Restricted Admin / Remote Credential Guard | unterstützt (Extended Properties) | unterstützt |
| Laufwerke/Drucker/Ports | aus, solange nicht konfiguriert | aus |

**Komfort:** Auflösung folgt der Tab-Größe live (`UpdateSessionDisplaySettings`, Fallback Smart Sizing),
DPI-Skalierung wird an den Server übergeben (`DesktopScaleFactor`/`DeviceScaleFactor`), Auto-Reconnect,
verständliche Trennungsgründe über `GetErrorDescription`. Layoutwechsel im Split-Screen lassen die Ansichten im
visuellen Baum, damit RDP-Sitzungen dabei nicht getrennt werden.

**Hinweis:** Importierte mRemoteNG-Verbindungen behalten ihren expliziten Wert (oft `NoAuth`).

## 10. Bedienung: Schnellsuche, Editor, Arbeitsbereiche

* **Schnellsuche (Strg+K):** `FuzzyMatcher` (Präfix > Wortanfang > zusammenhängend > verstreut) über Name (×3), Host (×2),
  Ordnerpfad, Beschreibung, Tags und Benutzer; mehrere Begriffe müssen alle passen. Leere Eingabe zeigt
  „zuletzt verwendet“ und Favoriten, `>` listet Befehle und Arbeitsbereiche, eine unbekannte Adresse wird als
  Quick-Connect angeboten. Die Palette ist ein eigenes Fenster und liegt damit auch über RDP-/Terminal-Inhalten.
* **Eigenschaften-Editor:** bildet die Vererbung direkt ab – leere Felder zeigen den geerbten Wert samt Herkunft,
  Auswahllisten haben „(geerbt: …)“, Checkboxen sind dreistufig. Warnung bei unsicheren RDP-Einstellungen.
* **Credential-Vault:** eigener Dialog (Benutzer, Domäne, Passwort, SSH-Schlüssel + Passphrase); Zuweisung im Editor.
* **Arbeitsbereiche:** offene Tabs (Layout + Bereich) werden beim Beenden gespeichert und beim Start
  wiederhergestellt; benannte Arbeitsbereiche über die Schnellsuche.
* **Import-Sicherheitsprüfung:** mRemoteNG speichert „Zertifikat nicht prüfen“ explizit; HostDeck bietet die
  Umstellung auf „warnen“ an.

## 11. Weitere Protokolle, Fenster, Snippets, Verteilung

* **Telnet / Raw-TCP:** `TelnetSession` (ECHO, SGA, TTYPE „XTERM-256COLOR“, NAWS, CR-NUL, IAC-Escaping) mit
  Auto-Login für Netzwerkgeräte; gleiche Terminal-Oberfläche wie SSH (`ITerminalBackend`).
* **VNC:** eigener RFB-Client (`HostDeck.Protocols.Vnc`): RFB 3.3/3.7/3.8, None/VNC-Auth, ZRLE/Hextile/CopyRect/Raw,
  DesktopSize, Zwischenablage. Gegen TigerVNC getestet (ZRLE und Hextile bytegleich zu Raw). VNC ist unverschlüsselt –
  im Unternehmen über VPN oder SSH-Tunnel nutzen.
* **HTTP/HTTPS:** WebView2-Tab mit Basic/NTLM-Anmeldung aus dem Vault und bewusster Zertifikatsentscheidung pro Tab.
* **Fenster:** Tabs in eigene Fenster ablösen (Kontextmenü oder aus dem Fenster ziehen), zwischen Fenstern verschieben;
  Schließen eines Zusatzfensters holt die Sessions zurück. Terminal-Ansichten liefern den Verlauf nach, RDP verbindet neu.
* **Snippets:** Befehle/Makros mit Platzhaltern `{{Name}}`, gespeichert verschlüsselt in der Datenbank.
* **Verteilung:** Velopack – `HostDeck-win-Setup.exe` (pro Benutzer, ohne Admin), Delta-Updates. Update-Quelle:
  Netzwerkfreigabe, HTTP(S) oder GitHub (privat mit Token). Updates werden im Hintergrund geladen und beim Beenden
  angewendet. Release per Git-Tag `v*` (Workflow `release.yml`), Code-Signierung über PFX oder Azure Trusted Signing.
