# Native product dependency attribution

These notices accompany dependencies of the Native AOT product, distinct from
the preserved Rust baseline and the editor extension's separate dependency set.

- Tomlyn 2.10.1: [license at the package's recorded source commit](https://github.com/xoofx/Tomlyn/blob/6cc82dd23a2c0aefb858f92a51c1b184e2812103/license.txt).
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
