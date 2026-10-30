#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'dist',
    [ValidateSet('x86_64-pc-windows-msvc')]
    [string]$Target = 'x86_64-pc-windows-msvc',
    [switch]$SkipBuild,
    [string]$IsccPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Build Windows packages on native Windows with the MSVC Rust toolchain.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$manifest = [IO.File]::ReadAllText((Join-Path $root 'Cargo.toml'))
$versionMatch = [regex]::Match($manifest, '(?m)^version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"\s*$')
if (-not $versionMatch.Success) { throw 'Cargo.toml must declare a numeric package version.' }
$version = $versionMatch.Groups[1].Value
if ([IO.Path]::IsPathRooted($OutputDirectory)) { $output = [IO.Path]::GetFullPath($OutputDirectory) }
else { $output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory)) }
if (-not $IsccPath) {
    $IsccPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
}
if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw "Inno Setup compiler not found: $IsccPath. Install Inno Setup 6 or supply -IsccPath."
}
$targetRoot = Join-Path $root 'target'
if ($env:CARGO_TARGET_DIR) {
    if ([IO.Path]::IsPathRooted($env:CARGO_TARGET_DIR)) { $targetRoot = [IO.Path]::GetFullPath($env:CARGO_TARGET_DIR) }
    else { $targetRoot = [IO.Path]::GetFullPath((Join-Path $root $env:CARGO_TARGET_DIR)) }
}
$binary = Join-Path $targetRoot "$Target/release/msync.exe"
if (-not $SkipBuild) {
    $oldFlags = [Environment]::GetEnvironmentVariable('RUSTFLAGS', 'Process')
    $oldEncoded = [Environment]::GetEnvironmentVariable('CARGO_ENCODED_RUSTFLAGS', 'Process')
    Push-Location $root
    try {
        # A portable executable must not inherit the builder's installed VC runtime.
        if ($oldEncoded) {
            $env:CARGO_ENCODED_RUSTFLAGS = $oldEncoded + [char]31 + '-C' + [char]31 + 'target-feature=+crt-static'
        } else {
            $env:RUSTFLAGS = "$oldFlags -C target-feature=+crt-static".Trim()
        }
        & cargo build --release --locked --target $Target
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit $LASTEXITCODE" }
    } finally {
        Pop-Location
        [Environment]::SetEnvironmentVariable('RUSTFLAGS', $oldFlags, 'Process')
        [Environment]::SetEnvironmentVariable('CARGO_ENCODED_RUSTFLAGS', $oldEncoded, 'Process')
    }
}
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Release executable missing: $binary" }
$reportedVersion = & $binary --version
if ($LASTEXITCODE -ne 0 -or "$reportedVersion".Trim() -ne "msync $version") {
    throw "Release executable version does not match Cargo.toml ($version): $reportedVersion"
}

$dumpbin = Get-Command dumpbin.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$dumpbinPath = $null
if ($dumpbin) { $dumpbinPath = $dumpbin.Source }
if (-not $dumpbinPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $dumpbinCandidates = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe')
        if ($LASTEXITCODE -eq 0 -and $dumpbinCandidates.Count -gt 0) { $dumpbinPath = $dumpbinCandidates[0] }
    }
}
if ($dumpbinPath) {
    $imports = & $dumpbinPath /DEPENDENTS $binary
    if ($LASTEXITCODE -ne 0) { throw 'dumpbin could not inspect the release executable.' }
    if (($imports -join "`n") -match '(?im)^\s*(VCRUNTIME|MSVCP)[A-Z0-9_]*\.dll\s*$') {
        throw 'Release executable imports a dynamic Visual C++ runtime. Rebuild with -C target-feature=+crt-static.'
    }
} else {
    Write-Warning 'dumpbin not found: PE runtime imports were not audited; -SkipBuild requires a caller-provided static-CRT executable.'
}
function Get-PayloadFiles([string]$Directory) {
    $paths = [string[]]@(Get-ChildItem -LiteralPath $Directory -File -Recurse | ForEach-Object { $_.FullName })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    foreach ($path in $paths) { Get-Item -LiteralPath $path }
}

$null = New-Item -ItemType Directory -Path $output -Force
$scratch = Join-Path $root '.temp'
$null = New-Item -ItemType Directory -Path $scratch -Force
$stage = Join-Path $scratch ('windows-package-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage
try {
    $null = New-Item -ItemType Directory -Path (Join-Path $stage 'bin')
    $documentation = Join-Path $stage 'share/doc/msync'
    $null = New-Item -ItemType Directory -Path $documentation -Force
    Copy-Item -LiteralPath $binary -Destination (Join-Path $stage 'bin/msync.exe')
    foreach ($name in @('README.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $documentation
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $documentation -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'portable-readme.txt') -Destination (Join-Path $stage 'INSTALL.md')
    $python = Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $python) { $python = Get-Command python3 -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if (-not $python) { throw 'Packaging requires Python 3 to collect third-party license notices (not needed by installed local-copy users).' }
    Push-Location $root
    try {
        & $python.Source (Join-Path $root 'packaging/licenses.py') --target $Target --output (Join-Path $documentation 'THIRD-PARTY-NOTICES.txt')
        if ($LASTEXITCODE -ne 0) { throw "Third-party license collection failed with exit $LASTEXITCODE" }
    } finally { Pop-Location }
    $files = @(Get-PayloadFiles $stage)
    $lines = foreach ($file in $files) {
        $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
    }
    $utf8 = New-Object Text.UTF8Encoding $false
    [IO.File]::WriteAllText((Join-Path $stage 'manifest.sha256'), ($lines -join "`n") + "`n", $utf8)

    # Sorted entries and fixed timestamps make ZIP bytes repeatable for identical
    # payloads and the same .NET compressor, independent of checkout timestamps.
    Add-Type -AssemblyName System.IO.Compression
    $stamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    if ($env:SOURCE_DATE_EPOCH) {
        $requested = [DateTimeOffset]::FromUnixTimeSeconds([long]$env:SOURCE_DATE_EPOCH)
        if ($requested.Year -gt 2107) { throw 'SOURCE_DATE_EPOCH exceeds the ZIP year limit 2107.' }
        if ($requested.Year -ge 1980) { $stamp = $requested }
    }
    $base = "msync-$version-windows-x86_64"
    $zipPath = Join-Path $output "$base.zip"
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true, [Text.Encoding]::UTF8)
        try {
            foreach ($file in (Get-PayloadFiles $stage)) {
                $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
                $entry = $zip.CreateEntry("$base/$relative", [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $stamp
                $destination = $entry.Open()
                $source = $file.OpenRead()
                try { $source.CopyTo($destination) } finally { $source.Dispose(); $destination.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }

    & $IsccPath '/Qp' "/DAppVersion=$version" "/DSourceDir=$stage" "/DOutputDir=$output" "/DOutputBaseName=$base-setup" (Join-Path $PSScriptRoot 'msync.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler failed with exit $LASTEXITCODE" }
    $setupPath = Join-Path $output "$base-setup.exe"
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "Installer output missing: $setupPath" }
    $checksums = foreach ($artifact in @($zipPath, $setupPath)) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($artifact)
    }
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n", $utf8)
    Write-Host "Built $zipPath and $setupPath"
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
