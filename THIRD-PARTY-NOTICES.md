# Third-party notices

HostDeck is licensed under the [Apache License 2.0](LICENSE). It ships with or depends on the following components,
each under its own license. All of them are compatible with distributing HostDeck under Apache-2.0.

| Component | Used for | License |
|---|---|---|
| [.NET](https://github.com/dotnet/runtime) / WPF / Windows Forms | runtime and UI framework (bundled, self-contained) | MIT |
| [WPF-UI](https://github.com/lepoco/wpfui) | Fluent design controls | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM source generators | MIT |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | SSH, SFTP | MIT |
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) | mRemoteNG-compatible AEAD ciphers (AES/Serpent/Twofish GCM/CCM/EAX) | MIT |
| [Konscious.Security.Cryptography.Argon2](https://github.com/kmaragon/Konscious.Security.Cryptography) | Argon2id key derivation | MIT |
| [SQLite3 Multiple Ciphers](https://github.com/utelle/SQLite3MultipleCiphers) (SQLite3MC.PCLRaw.bundle) | encrypted database (SQLCipher v4 format) | MIT |
| [SQLite](https://sqlite.org) | database engine (inside SQLite3MC) | Public domain |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | ADO.NET provider | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) (provider core) | native SQLite binding | Apache-2.0 |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | Windows DPAPI | MIT |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | terminal rendering, web tabs | BSD-3-Clause (Microsoft WebView2 SDK license) |
| [xterm.js](https://github.com/xtermjs/xterm.js) and add-ons (fit, search, unicode11, web-links) | terminal emulator (embedded, see `src/HostDeck.App/Assets/Terminal/LICENSE-xterm.txt`) | MIT |
| [Velopack](https://github.com/velopack/velopack) | installer and delta updates | MIT |
| [Microsoft.Extensions.Hosting](https://github.com/dotnet/runtime), [Serilog](https://github.com/serilog/serilog-extensions-hosting) | hosting/logging infrastructure | MIT / Apache-2.0 |

## Test data (not shipped)

`tests/HostDeck.Tests/Fixtures/confCons*.xml` are unmodified test files from
[mRemoteNG](https://github.com/mRemoteNG/mRemoteNG) (GPL-2.0), used only as test vectors to verify compatible
decryption. They are not part of the application. HostDeck's mRemoteNG import/export is an independent
implementation of the file format.

## Not affiliated

HostDeck is an independent project and not affiliated with mRemoteNG, MobaXterm or Microsoft. Product names are used
only to describe compatibility.
