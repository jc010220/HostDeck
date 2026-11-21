# HostDeck – Architecture

Goal: mRemoteNG-compatible connection management with MobaXterm-style usability, native on Windows 10/11, High-DPI aware.

## 1. Tech stack

| Area | Choice | Rationale |
|---|---|---|
| Runtime | **.NET 10 (LTS, supported until Nov 2028)** | .NET 8 went out of support on 2026-11-10, .NET 9 even earlier. |
| UI | **WPF + WPF-UI 4.x (Fluent, Mica, dark mode)** | Vector-based, per-monitor V2 DPI. Mature hosting for ActiveX (`WindowsFormsHost`) and WebView2. |
| Tabs/split | **Own pane grid** (1 / 2 side by side / 2 stacked / 2×2 grid, `GridSplitter`) | AvalonDock was planned originally and dropped: re-parenting HWND content (RDP ActiveX, WebView2) while docking is fragile. The own model is simpler and fully unit-testable. |
| MVVM | CommunityToolkit.Mvvm | Source generators, testable view models. |
| RDP | Native ActiveX `MsRdpClient` (mstscax.dll) via hand-written COM interop | Full performance, clipboard, audio, drives, smart cards, RD Gateway. |
| SSH/SFTP | **SSH.NET** + **xterm.js in WebView2** | xterm.js is the most mature terminal emulator (used by VS Code), GPU rendering, add-ons for search and links. |
| Storage | SQLite in **SQLCipher v4 format** (AES-256-CBC + HMAC-SHA512) via *SQLite3 Multiple Ciphers*, Argon2id for the master password | Fully encrypted file, transactional. `e_sqlite3mc` is the recommended successor of the outdated `e_sqlcipher` builds. |
| mRemoteNG crypto | BouncyCastle.Cryptography | mRemoteNG uses AES-GCM with a **128-bit nonce**; `System.Security.Cryptography.AesGcm` only allows 96 bits. |
| Distribution | Velopack | Per-user installer without admin rights, delta updates. |

**Why not Avalonia?** The product is explicitly Windows-only, and its core (RDP ActiveX) is Windows-only. In Avalonia, ActiveX
hosting would only be possible via `NativeControlHost` with manual HWND management. The business logic (`HostDeck.Core`,
`HostDeck.Import.*`, storage, protocols, view models) is UI-free and targets `net10.0`, so a later Avalonia port would only
have to replace the UI layer.

**Known WPF pitfall ("airspace"):** HWND content (RDP ActiveX, WebView2) is always drawn on top of WPF. Therefore: no WPF
popups over the session area; overlays (quick search, dialogs) are separate top-level windows.

## 2. Project structure

```
HostDeck.slnx
├─ Directory.Build.props / Directory.Packages.props   (central versions, warnings = errors)
├─ src/
│  ├─ HostDeck.Core               net10.0          data model, inheritance, vault, three-way merge
│  ├─ HostDeck.Import.MRemoteNG   net10.0          confCons.xml import/export + crypto
│  ├─ HostDeck.Storage            net10.0          encrypted database, master password, key file
│  ├─ HostDeck.Protocols.Ssh      net10.0          SSH.NET, SFTP, Telnet/raw TCP, cwd tracking (OSC 7)
│  ├─ HostDeck.Protocols.Rdp      net10.0          RdpPlan (settings → RDP client properties)
│  ├─ HostDeck.Protocols.Vnc      net10.0          own RFB client
│  ├─ HostDeck.Presentation       net10.0          view models, quick search, quick connect, team library
│  └─ HostDeck.App                net10.0-windows  WPF shell, WPF-UI, RDP/VNC/Web/terminal views, updates
└─ tests/
   └─ HostDeck.Tests              xUnit v3 (Microsoft.Testing.Platform)
```

## 3. Data model

```
ConnectionTree
 ├─ Root : FolderNode            (invisible, Settings = global user defaults)
 │   └─ Children : TreeNode[]    FolderNode | ConnectionNode
 └─ Vault : CredentialVault      Credential { Id, Name, Username, Domain, Password, PrivateKey, … }

TreeNode       { Id (Guid), Name, Parent, Settings : ConnectionSettings, Extensions : Dictionary }
FolderNode     { Children, IsExpanded }
ConnectionNode { Hostname }      (as in mRemoteNG, the hostname is not inheritable)

ConnectionSettings  (all fields nullable)
 ├─ General: Protocol, Port, Description, Icon, Panel, Color, TabColor, Favorite, …
 ├─ Login:   CredentialId, Username, Domain, Password
 ├─ Ssh  : SshSettings     PuttySession, OpeningCommand, Options, TunnelConnectionName
 ├─ Rdp  : RdpSettings     resolution, colors, redirects, CredSSP, …, Gateway : RdGatewaySettings
 └─ Vnc  : VncSettings
```

### Inheritance

* **`null` = inherit, value = explicitly set.** Each field has one state instead of a value plus an `InheritXyz` flag as in mRemoteNG.
* Resolved per field, including nested groups: `node → parent → … → root → ConnectionSettings.Defaults`.
* No port anywhere in the chain ⇒ the protocol's default port.
* `SettingsResolver.FindOrigin(node, s => s.X)` tells where a value comes from (for the UI: "inherited from folder X").
* **Credential vault:** `CredentialId` is itself inheritable. Per field (user, domain, password) the *nearest* node wins that has
  either a valid vault reference or an inline value. On the same node, the vault takes precedence.

### mRemoteNG specifics handled by the importer

| mRemoteNG | HostDeck |
|---|---|
| `InheritXyz="True"` | field = `null` |
| Children directly under the root **never** inherit (`InheritanceActive`) | inherit flags ignored there, own value is used |
| `Inherit="True"` (very old files) | all fields `null` |
| `RedirectDiskDrives` bool (≤ 2.7) / enum (≥ 2.8) | `RdpDiskDrives` (True ⇒ Local) |
| `Resolution="Res1920x1080"` (old) | `Fixed` + width/height |
| unknown attributes | `Extensions["mrng:…"]`, written back on export |
| missing/duplicate ids | new GUID (like mRemoteNG) |

## 4. mRemoteNG cryptography (verified against the original source code)

| ConfVersion | Method | Format (Base64) |
|---|---|---|
| ≤ 2.5 | AES-128-CBC, key = MD5(UTF-8(pw)) | `iv[16] ‖ ct` |
| ≥ 2.6 | AES/Serpent/Twofish-256 in GCM/CCM/EAX, key = PBKDF2-HMAC-SHA1(pw, salt, KdfIterations) | `salt[16] ‖ nonce[16] (CCM: 11) ‖ ct ‖ tag[16]`, AAD = salt |
| `KeyProtection="Dpapi"` | Windows DPAPI (CurrentUser) | – |

* Default key: `mR3m`. Root attribute `Protected` = Encrypt("ThisIsNotProtected" or "ThisIsProtected"); this is how the master password is checked.
* `FullFileEncryption="true"`: the entire content of the root element is a single ciphertext.
* Very old files: the whole file is legacy-encrypted (no recognizable XML).
* Password bytes as in BouncyCastle `Pkcs5PasswordToBytes`: **one byte per character**; characters above U+00FF make mRemoteNG throw.

## 5. Security

* XML is parsed without DTD processing and without external entities (XXE protection, tested).
* Plain-text passwords exist only in memory. They are persisted exclusively in the encrypted database (section 7).
* Derived keys are zeroed after use (`CryptographicOperations.ZeroMemory`).
* No secrets in `settings.json` or the log. The update token and the team file password are stored as encrypted documents in the database.
* Changing the master password never uses weaker Argon2id parameters than the current default, even if the key file is old or was tampered with.
* SFTP downloads: server file names are sanitized (path separators, reserved device names, control characters), existing files are
  never overwritten, and downloaded files get the "Mark of the Web" (`Zone.Identifier`, zone 3) so SmartScreen/Office warn.
* Terminal WebView: navigation is locked to the embedded page, messages are only accepted from that origin, links open only
  http(s) in the default browser, multi-line paste asks for confirmation.
* Web tabs: only http(s), external protocol handlers are blocked, stored credentials are only sent to the configured host/port/scheme,
  over plain HTTP only after consent. Certificate exceptions are keyed by host, port and SHA-256 of the certificate.
* **Target binding for personal logins:** for team entries that resolve to a vault credential, the confirmed target
  (`protocol|host|port`) is stored per entry (`team-trusted-targets`). Pulls remember the previous target; a changed target
  requires explicit confirmation before the login is sent, otherwise the session opens without the personal login.
* **Plain-text protocols:** Telnet sends a stored password only after per-entry/host consent (`plaintext-consent`); raw TCP never auto-logs in.
* **RDP:** security-critical properties (`AuthenticationLevel`, `RestrictedLogon`, `DisableCredentialsDelegation`,
  `RedirectedAuthentication`) must apply, otherwise the connection is aborted.
* **Export:** secrets are only written with an export password, which also enables full-file encryption and 10,000 KDF iterations.
* **Limits:** VNC desktop ≤ 16384 px per side and ≤ 50 M pixels, ZRLE blocks ≤ 64 MB, clipboard ≤ 1 MB (larger is skipped);
  imported `KdfIterations` ≤ 100,000. Quick-connect input with `user:password@` is rejected and never stored.
* **Updates:** plain HTTP sources are rejected. SQLite3MC 2.7.1 (SQLite 3.54.1) is bundled (CVE-2025-6965 fixed).

## 6. UI layer

```
MainWindow (FluentWindow, Mica, PerMonitorV2 DPI)
├─ Toolbar: sidebar toggle · import/export · new · layout 1/2/2/4 · team sync · multi-exec · quick search · theme
├─ Sidebar (collapsible): quick connect (with history) · search (Ctrl+F) · TreeView (drag & drop) · SFTP · snippets
├─ SplitWorkspace (Grid + GridSplitter)
│   └─ PaneView × 1–4  (own tab strip; content of all tabs stays loaded)
└─ Status bar: status · team status · multi-exec indicator · cancel connecting
```

* **MVVM:** all logic (filter, move, layout, tabs, quick connect, import, team sync) lives in `HostDeck.Presentation`
  (platform-neutral, tested). Code-behind only wires input and drag & drop.
* **No event handlers in templates/styles:** context menus in templates use commands. The XAML compiler mis-wires handlers in
  `Setter.Value`/templates (this caused an endless exception loop in 1.0.2); `XamlSafetyTests` guard against it.
* **Sessions survive tab and layout changes:** no `TabControl` (it destroys the content of unselected tabs); PaneViews are reused per area.
* **Persistence:** structural changes (move, new, delete, import) are saved immediately; expand/collapse state on exit.
  If the database could not be loaded, nothing is saved; if more than half of the connections would disappear, a backup is made first.
* **Scrolling:** a global class handler (`GentleWheelScroll`) scrolls proportionally to the wheel delta (2 items or 40 px per notch),
  so high-resolution wheels and touchpads stay controllable.
* **Theme:** System (follows Windows) → Dark → Light.

### Keyboard shortcuts

| Key | Action |
|---|---|
| Ctrl+F / Esc / ↓ / Enter | Search / clear filter / jump into results / open first result |
| Ctrl+K | Quick search |
| Ctrl+L | Quick connect |
| Ctrl+O | Import mRemoteNG |
| Ctrl+S | Sync team library |
| Ctrl+N / Ctrl+Shift+N | New connection / new folder |
| Ctrl+D | Duplicate (in the tree) |
| Enter / double-click / Del | Connect / connect / delete (in the tree) |
| Ctrl+W, middle-click | Close tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+Alt+1…4 | Layout: 1 / 2 side by side / 2 stacked / 2×2 grid |
| Ctrl+Alt+→ | Next pane |
| Ctrl+Shift+M | Toggle multi-execution |
| Ctrl+Shift+B | Show/hide sidebar |

## 7. Encrypted database

Files under `%APPDATA%\HostDeck\` (both belong in every backup):

| File | Content |
|---|---|
| `connections.hdk` (+ `-wal`, `-shm`) | SQLite, fully encrypted in SQLCipher v4 format; indistinguishable from random data without the key |
| `connections.hdk.key` | Key file (JSON): Argon2id parameters + salt, wrapped database key, optional device slot |

```
master password ──Argon2id (64 MiB, t=3, p=4, 128-bit salt)──► KEK
KEK ──AES-256-GCM──► DEK (random, 256 bit) ──► SQLCipher raw key (no additional PBKDF2)
Windows DPAPI (CurrentUser) ──► DEK            optional slot "remember on this PC"
```

* **Changing the password** only re-wraps the DEK; the database stays unchanged and the operation is atomic.
* **Forgotten password:** no back door. "Start over" renames the old files to `*.bak` instead of deleting them.
* **Schema:** tables `nodes` (tree, settings as JSON; inherited fields omitted), `credentials` (vault), `known_hosts`,
  `meta` (schema version, root defaults, encrypted documents such as snippets, team base/backups, update token).
  The full state is saved in one transaction (5,000 connections < 1 s).
* **Backups:** daily automatic backup on startup (last 10 kept) plus manual "Create backup".
* **Single instance:** a named mutex prevents two HostDeck instances from overwriting the same database.
* Verified by tests: RFC 9106 test vector for Argon2id, no plain text in the files, wrong password and wrong device key are rejected.

## 8. SSH, terminal and SFTP

```
TerminalView (WPF) ── WebView2 ── terminal.html + xterm.js 6 (embedded resources, no CDN)
      │  JSON messages: output (batched), input, resize, copy/paste, shortcuts
TerminalSessionViewModel ── output buffer (512 KB, replayed when a view attaches)
      │                    └─ multi-exec routing via MainViewModel
SshTerminalSession (SSH.NET) ── PTY xterm-256color · OSC 7 parser · TerminalHighlighter
SftpSession ── separate connection, same (already confirmed) host key
```

* **Host keys:** trust on first use with a fingerprint dialog; changed keys are clearly warned about and rejected by default.
  Stored in the encrypted database (table `known_hosts`).
* **Authentication:** key, password, keyboard-interactive (OTP). A stored password is sent only once; if the server asks
  again, the user is prompted instead of repeating a wrong password until `MaxAuthTries`.
* **SFTP follows the terminal:** an exec channel checks whether the login shell is bash or zsh. Only then a prompt hook is
  installed that reports the directory via OSC 7. The echo of the hook command is hidden; the MOTD stays. Nothing is injected
  on network devices (switch CLIs).
* **Highlighting:** errors (red), warnings (yellow), success (green), IPv4 (cyan), only on text the program does not color
  itself; full-screen programs (vim, htop) are left alone.
* **Terminal usage:** select = copy, right-click = paste (bracketed paste), Ctrl+Shift+C/V, Ctrl+Shift+F search,
  Ctrl+mouse wheel font size, clickable links, Enter in a disconnected session reconnects. App shortcuts inside the terminal:
  Ctrl+Tab, Ctrl+Shift+W (close tab; Ctrl+W stays "delete word" in the shell), Ctrl+Shift+M, Ctrl+Shift+L, Ctrl+Shift+B, Ctrl+Alt+1…4.
* **Integration tests** run against a real OpenSSH server when `HOSTDECK_TEST_SSH`, `HOSTDECK_TEST_SSH_USER`,
  `HOSTDECK_TEST_SSH_PASSWORD` (optionally `HOSTDECK_TEST_SSH_KEYFILE`) are set.

## 9. RDP

Like mRemoteNG, the **native Windows RDP client** (ActiveX `MsRdpClient11…7NotSafeForScripting` from `mstscax.dll`) is
embedded, without a third-party package and without `COMReference` (which only builds with Visual Studio's MSBuild):

* `HostDeck.Protocols.Rdp` (platform-neutral, tested): `RdpPlanBuilder` translates the inherited settings into an `RdpPlan`
  (properties per object, non-scriptable flags, drives, DPI).
* `HostDeck.App/Rdp`: own COM definitions. CLSIDs, IIDs, DISPIDs and the vtable order of `IMsRdpClientNonScriptable5` were
  checked against interop metadata generated from the type library. Settings objects (dual) are set via IDispatch by name,
  which is robust across Windows versions.
* **Robustness:** all ActiveX event handlers are wrapped; the control is never disposed inside its own event, teardown is
  deferred to application idle. A connection attempt can be cancelled at any time (Esc / status bar).

**Security defaults** (stricter than mRemoteNG):

| Setting | HostDeck | mRemoteNG default |
|---|---|---|
| Server authentication | warn on invalid certificate | connect without verification |
| NLA / CredSSP | on | on |
| Password | only via `IMsTscNonScriptable.ClearTextPassword`, never in .rdp files | same |
| No stored password | secure Windows credential dialog (CredUI) | failure/prompt |
| Restricted Admin / Remote Credential Guard | supported (extended properties) | supported |
| Drives/printers/ports | off unless configured | off |

**Convenience:** the resolution follows the tab size live (`UpdateSessionDisplaySettings`, fallback smart sizing), DPI scaling
is passed to the server (`DesktopScaleFactor`/`DeviceScaleFactor`), auto-reconnect, readable disconnect reasons via
`GetErrorDescription`. Layout changes in split view keep the views in the visual tree, so RDP sessions are not disconnected.

**Note:** imported mRemoteNG connections keep their explicit value (often `NoAuth`); the import security check offers to switch them.

## 10. Usability: quick search, editor, workspaces

* **Quick search (Ctrl+K):** `FuzzyMatcher` (prefix > word start > contiguous > scattered) over name (×3), host (×2), folder
  path, description, tags and user; multiple terms must all match. Empty input shows recently used and favorites, `>` lists
  commands and workspaces, an unknown address is offered as quick connect. The palette is its own window, so it also appears
  above RDP/terminal content.
* **Properties editor:** shows inheritance directly: empty fields show the inherited value and its origin, drop-downs have
  "(inherited: …)", check boxes are three-state. Warns about insecure RDP settings.
* **Duplicate (Ctrl+D):** deep copy (settings cloned via JSON) directly below the original; the editor opens immediately so only
  name and host need changing; cancelling discards the copy.
* **Credential vault:** own dialog (user, domain, password, SSH key + passphrase) with search over name, user, domain and notes;
  assignment in the editor.
* **Workspaces:** open tabs (layout + pane) are saved on exit and restored on startup; named workspaces via quick search.
* **Import security check:** mRemoteNG stores "don't verify certificate" explicitly; HostDeck offers to switch to "warn".

## 11. Team library (shared confCons.xml)

A shared `confCons.xml` on a network share works like a shared KeePass database:

```
network share: confCons.xml ◄──── read (every 15 s: timestamp/size probe) ────┐
        ▲                                                                      │
        └── Ctrl+S: lock file → re-read → three-way merge → atomic replace ── HostDeck (folder "Team")
                                                                               │
local encrypted DB: team subtree (offline copy) + base (fingerprints of last sync) + encrypted backups
```

* **Representation:** a top-level folder marked with `Extensions["hostdeck:team"]` holds the team content; ids are those of the file.
* **Three-way merge (`HostDeck.Core.Sync.TreeMerge`):** base = fingerprints (SHA-256 over all synced fields) of the last sync,
  *mine* = local subtree, *theirs* = current file. Per id: only one side changed ⇒ that side wins; both identical ⇒ fine; both
  different ⇒ conflict (asked on save, "keep mine" on automatic pull). Deleted on one side but changed on the other ⇒ kept.
  Parent changes are merged; deleted parents are restored if they still contain changes; move cycles fall back to the file's version.
  Order follows the file, locally added entries are placed after their local predecessor.
* **Personal data never syncs:** `CredentialId` (vault assignment, e.g. personal RADIUS logins) and expand state are excluded from
  fingerprints and stripped before writing. The vault itself is never written to the team file.
* **Safe writing (`TeamLibraryFile`):** lock file `*.hostdeck-lock` (stale after 2 min), re-read and hash comparison right before
  writing (changed ⇒ merge again), write to a temp file and replace atomically. The previous content is stored as an encrypted
  document in the local database (ring of 10). After writing, the written content is re-imported and used as the new base, so
  normalisation by the exporter (e.g. default values for attributes an older file lacked) never shows up as a change.
* **Format preserved:** ConfVersion, cipher engine/mode, KDF iterations, full-file encryption and the file's master password are
  kept, so colleagues can keep using mRemoteNG (which does not merge and overwrites the file when it saves).
* **Read-only mode (jump hosts):** the configured path may be a folder; the newest `.xml` in it is used. Local deviations are
  discarded on every pull and nothing is ever written.
* **Safeguards:** deleting the team folder only disconnects (never "delete everything in the file"); an earlier manual import of
  the same file (same ids) is replaced on setup, carrying over personal assignments; a warning is shown if the file contains
  passwords but uses mRemoteNG's public default key.

## 12. Other protocols, windows, snippets, distribution

* **Telnet / raw TCP:** `TelnetSession` (ECHO, SGA, TTYPE "XTERM-256COLOR", NAWS, CR-NUL, IAC escaping) with auto-login for
  network devices; same terminal UI as SSH (`ITerminalBackend`).
* **VNC:** own RFB client (`HostDeck.Protocols.Vnc`): RFB 3.3/3.7/3.8, None/VNC auth, ZRLE/Hextile/CopyRect/Raw, DesktopSize,
  clipboard. Tested against TigerVNC (ZRLE and Hextile byte-identical to Raw). VNC is unencrypted; use it over VPN or an SSH tunnel.
* **HTTP/HTTPS:** WebView2 tab with Basic/NTLM login from the vault and an explicit certificate decision per tab.
* **Windows:** detach tabs into their own windows (context menu or drag out of the window), move them between windows; closing an
  extra window moves its sessions back. Terminal views replay their history, RDP reconnects.
* **Snippets:** commands/macros with `{{Name}}` placeholders, stored encrypted in the database.
* **Distribution:** Velopack: `HostDeck-win-Setup.exe` (per user, no admin), delta updates. Update source: network share, HTTP(S)
  or GitHub (default: this repository; private repositories need a read-only token). Updates are downloaded in the background and
  applied on exit. Releases via Git tag `v*` or workflow dispatch (`release.yml`); code signing via PFX or Azure Trusted Signing.
