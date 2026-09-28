# Licences

DipTick itself is under the MIT License: see [About DipTick](about). diptick.exe also carries other people's work, each under its own licence. The full terms and copyright notices of all of it ship beside diptick.exe, in THIRD-PARTY-NOTICES.txt; this page lists what is inside and the licence each comes under.

## Packages inside diptick.exe

Each licence named here is the one the package itself declares, in its NuGet entry or the licence file it carries.

- .NET 10.0.12, the runtime DipTick runs on (Microsoft.NETCore.App.Runtime.win-x64): MIT, with the notices it carries
- AngleSharp 1.8.2: MIT
- Anthropic 12.50.0: MIT
- Anthropic.Vertex 0.5.3: MIT
- Avalonia 12.1.3: MIT
- Avalonia.Angle.Windows.Natives 2.1.27548.20260419: BSD-3-Clause, the ANGLE Project Authors
- Avalonia.Fonts.Inter 12.1.3: MIT
- Avalonia.HarfBuzz 12.1.3: MIT
- Avalonia.Remote.Protocol 12.1.3: MIT
- Avalonia.Skia 12.1.3: MIT
- Avalonia.Themes.Fluent 12.1.3: MIT
- Avalonia.Win32 12.1.3: MIT
- ColorHelper 1.8.1: MIT
- Google.Apis 1.76.0: Apache-2.0
- Google.Apis.Auth 1.76.0: Apache-2.0
- Google.Apis.Core 1.76.0: Apache-2.0
- Google.Apis.Oauth2.v2 1.68.0.1869: Apache-2.0
- HarfBuzzSharp 8.3.1.3: MIT
- HarfBuzzSharp.NativeAssets.Win32 8.3.1.3: MIT
- JetBrains.Annotations 2026.2.0: MIT
- libsodium 1.0.22: ISC
- Markdig 1.3.2: BSD-2-Clause
- MicroCom.Runtime 0.11.6: MIT
- Microsoft.Data.Sqlite.Core 10.0.12: MIT
- Microsoft.Extensions.AI.Abstractions 10.8.3: MIT
- Microsoft.Extensions.Configuration 10.0.11: MIT
- Microsoft.Extensions.Configuration.Abstractions 10.0.11: MIT
- Microsoft.Extensions.Configuration.Binder 10.0.11: MIT
- Microsoft.Extensions.Configuration.FileExtensions 10.0.11: MIT
- Microsoft.Extensions.Configuration.Json 10.0.11: MIT
- Microsoft.Extensions.DependencyInjection.Abstractions 10.0.11: MIT
- Microsoft.Extensions.FileProviders.Abstractions 10.0.11: MIT
- Microsoft.Extensions.FileProviders.Physical 10.0.11: MIT
- Microsoft.Extensions.FileSystemGlobbing 10.0.11: MIT
- Microsoft.Extensions.Logging.Abstractions 10.0.10: MIT
- Microsoft.Extensions.Options 10.0.11: MIT
- Microsoft.Extensions.Primitives 10.0.11: MIT
- ModelContextProtocol.Core 2.2.0: Apache-2.0, with MIT for its earlier contributions (its LICENSE file)
- Newtonsoft.Json 13.0.4: MIT
- NSec.Cryptography 26.4.0: MIT
- Onigwrap 1.0.11: MIT
- SkiaSharp 3.119.4: MIT
- SkiaSharp.NativeAssets.Win32 3.119.4: MIT
- SmartReader 0.11.1: Apache-2.0
- SQLitePCLRaw.bundle_e_sqlite3 2.1.12: Apache-2.0
- SQLitePCLRaw.core 2.1.12: Apache-2.0
- SQLitePCLRaw.lib.e_sqlite3 2.1.12: Apache-2.0
- SQLitePCLRaw.provider.e_sqlite3 2.1.12: Apache-2.0
- System.CodeDom 7.0.0: MIT
- System.IO.Abstractions 22.2.0: MIT
- System.Management 7.0.2: MIT
- System.Security.Cryptography.ProtectedData 10.0.12: MIT
- Terminal.Gui 2.5.0: MIT
- TestableIO.System.IO.Abstractions 22.2.0: MIT
- TestableIO.System.IO.Abstractions.Wrappers 22.2.0: MIT
- Testably.Abstractions.FileSystem.Interface 10.3.0: MIT
- TextMateSharp 2.0.4: MIT
- TextMateSharp.Grammars 2.0.4: MIT
- Wcwidth 4.0.1: MIT

## Inside them

- SQLite 3.53.3, in e_sqlite3.dll: public domain ("SQLite is in the Public Domain", sqlite.org).
- libsodium, in libsodium.dll: ISC, Frank Denis.
- Skia, in libSkiaSharp.dll: BSD-3-Clause; with SPIRV-Cross and Wuffs: Apache-2.0.
- HarfBuzz, in libHarfBuzzSharp.dll: HarfBuzz's own licence, an MIT-style one.
- ANGLE, in av_libglesv2.dll: BSD-3-Clause; with zlib 1.3.2 (the zlib licence), Abseil (Apache-2.0), xxHash 0.8.3 (BSD-2-Clause) and Chromium's compression utilities (BSD-3-Clause).
- Oniguruma, in libonigwrap.dll: BSD-2-Clause.
- The Inter typeface 3.019, in Avalonia.Fonts.Inter: the SIL Open Font License 1.1.
- The Roboto typeface, in Avalonia's dialogs: Apache-2.0.
- React, react-dom, scheduler and object-assign, in Avalonia's designer support: MIT.
- SimpleJSON, in TextMateSharp: MIT.
- 57 grammars and 21 colour themes, in TextMateSharp.Grammars, for the code the terminal screen shows: each under its own licence (MIT, the TextMate Bundle License, BSD-3-Clause, Apache-2.0, and the licences of the projects the themes come from).

## Used to build DipTick, and not inside it

- Avalonia.BuildServices 11.3.2: MIT, used while DipTick is built.
- Microsoft.NET.ILLink.Tasks 10.0.12: MIT, the checks that diptick.exe works as one file.
- Microsoft.Data.Sqlite 10.0.12: MIT, which only names Microsoft.Data.Sqlite.Core and SQLite's bundle.
- HarfBuzzSharp.NativeAssets.Linux 8.3.1.3: MIT, for Linux.
- HarfBuzzSharp.NativeAssets.macOS 8.3.1.3: MIT, for macOS.
- HarfBuzzSharp.NativeAssets.WebAssembly 8.3.1.3: MIT, for WebAssembly.
- SkiaSharp.NativeAssets.Linux 3.119.4: MIT, for Linux.
- SkiaSharp.NativeAssets.macOS 3.119.4: MIT, for macOS.
- SkiaSharp.NativeAssets.WebAssembly 3.119.4: MIT, for WebAssembly.
- The WiX Toolset 7.0.0, which builds DipTick's installer: its source under the Microsoft Reciprocal License, its binaries under the Open Source Maintenance Fee EULA.
