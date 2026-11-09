# Native product dependency attribution

These notices accompany dependencies of the Native AOT product, distinct from
the preserved Rust baseline and the editor extension's separate dependency set.

- Tomlyn 2.10.1: [license at the package's recorded source commit](https://github.com/xoofx/Tomlyn/blob/6cc82dd23a2c0aefb858f92a51c1b184e2812103/license.txt).
- PrettyPrompt 6.0.5: MPL-2.0 [license and corresponding source at the package's recorded commit](https://github.com/waf/PrettyPrompt/tree/b7bbd3da5fbc1af53d1e0e6977ce43760a138ca1).
  The product vendors this exact upstream revision with a narrow optional inline
  hint callback rendered by the same editor. The modified MPL-2.0 source, project
  and license ship alongside this notice under `prettyprompt-6.0.5/source/`,
  copied from the version-controlled `vendor/PrettyPrompt/` tree. Application
  adapters remain separate GPL-3.0-only source files.
- TextCopy 6.2.1: MIT [license at the package's recorded source commit](https://github.com/CopyText/TextCopy/blob/409eda72dd615c1ff2cd7327d5a294553eaa9b81/license.txt).
- Wcwidth.Sources 4.0.1: MIT [license at the source package's recorded commit](https://github.com/spectreconsole/wcwidth/blob/64a3f523d5dc27124e04ff9079137ac2c5c72143/LICENSE.md).
  PrettyPrompt embeds this private source dependency in its assembly, so its
  attribution is included even though it is not a separate runtime NuGet edge.
- Microsoft.Extensions.DependencyInjection.Abstractions 7.0.0: MIT
  [license at the package's recorded source commit](https://github.com/dotnet/runtime/blob/d099f075e45d2aa6007a22b71b45a08758559f80/LICENSE.TXT),
  brought into the dependency graph by TextCopy.
- Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core 10.0.14:
  [matching EF Core release license](https://github.com/dotnet/efcore/blob/v10.0.14/LICENSE.txt).
- SQLitePCLRaw core, provider.e_sqlite3, bundle_e_sqlite3 and lib.e_sqlite3 2.1.13:
  package metadata declares Apache-2.0 and Copyright 2014-2024 SourceGear, LLC.
  The included license is the [canonical Apache 2.0 text](https://www.apache.org/licenses/LICENSE-2.0.txt).
  The [published NuGet package](https://www.nuget.org/packages/SQLitePCLRaw.core/2.1.13)
  is the versioned metadata source; upstream has no corresponding `v2.1.13` Git
  tag, so this file does not invent a source revision for that release.
  [SQLite's own public-domain statement](https://www.sqlite.org/copyright.html)
  applies to SQLite embedded in the native library.

The build copies these files alongside the installed SDK's actual .NET license
and third-party notices. Updating package versions requires reviewing this
attribution set as well as the package references.
