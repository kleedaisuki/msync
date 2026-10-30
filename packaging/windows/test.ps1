#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Alias('Installer')][string]$InstallerPath,
    [string]$PortableZip
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Installer lifecycle tests require native Windows.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
if (-not $PortableZip) {
    $PortableZip = Join-Path ([IO.Path]::GetDirectoryName($installer)) ([IO.Path]::GetFileName($installer) -replace '-setup\.exe$', '.zip')
}
$portable = (Resolve-Path -LiteralPath $PortableZip).Path
$versionMatch = [regex]::Match([IO.File]::ReadAllText((Join-Path $root 'Cargo.toml')), '(?m)^version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"\s*$')
if (-not $versionMatch.Success) { throw 'Cargo package version not found.' }
$version = $versionMatch.Groups[1].Value
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{157D15D7-7899-4F94-9D69-3CCF769886E8}_is1'
$ownerKey = 'Software\msync\Installer'
$hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
$environment = $hive.OpenSubKey('Environment', $true)
if ($null -eq $environment) { $environment = $hive.CreateSubKey('Environment') }
$existing = $hive.OpenSubKey($uninstallKey)
if ($null -ne $existing) { $existing.Dispose(); $environment.Dispose(); $hive.Dispose(); throw 'Refusing to test over an existing per-user msync installation. Use an isolated Windows account/CI runner.' }
$owner = $hive.OpenSubKey($ownerKey)
if ($null -ne $owner) { $owner.Dispose(); $environment.Dispose(); $hive.Dispose(); throw 'Refusing to overwrite existing msync installer ownership state. Use an isolated Windows account/CI runner.' }
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'msync/msync REPL.lnk'
if (Test-Path -LiteralPath $shortcutPath) { $environment.Dispose(); $hive.Dispose(); throw 'Refusing to overwrite an existing msync REPL shortcut. Use an isolated Windows account/CI runner.' }

function Read-UserPath {
    $present = @($environment.GetValueNames()) -contains 'Path'
    if (-not $present) { return [pscustomobject]@{ Present = $false; Raw = $null; Kind = $null } }
    return [pscustomobject]@{
        Present = $true
        Raw = [string]$environment.GetValue('Path', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        Kind = $environment.GetValueKind('Path')
    }
}
function Write-UserPath([object]$State) {
    if ($State.Present) { $environment.SetValue('Path', [string]$State.Raw, $State.Kind) }
    else { $environment.DeleteValue('Path', $false) }
}
function Assert-Path([object]$Expected, [string]$Journey) {
    $actual = Read-UserPath
    if ($actual.Present -ne $Expected.Present -or $actual.Raw -cne $Expected.Raw -or $actual.Kind -ne $Expected.Kind) {
        throw "$Journey changed unrelated PATH text/type. Expected [$($Expected.Raw)] ($($Expected.Kind)); got [$($actual.Raw)] ($($actual.Kind))."
    }
}
function Invoke-Setup([string]$Executable, [string[]]$Arguments, [string]$Log) {
    $quoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $quoted += '"/LOG=' + $Log + '"'
    $process = Start-Process -FilePath $Executable -ArgumentList ($quoted -join ' ') -PassThru
    if (-not $process.WaitForExit(120000)) {
        & (Join-Path $env:SystemRoot 'System32/taskkill.exe') /PID $process.Id /T /F | Out-Null
        $null = $process.WaitForExit(5000)
        throw "Installer timed out; its process tree was terminated. Inspect $Log"
    }
    if ($process.ExitCode -ne 0) { throw "Installer exited $($process.ExitCode); inspect $Log" }
}
function Install-App([string]$Directory, [bool]$AddPath) {
    $tasks = '/TASKS='
    if ($AddPath) { $tasks = '/TASKS=addpath' }
    Invoke-Setup $installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$Directory", $tasks) (Join-Path $work 'install.log')
}
function Uninstall-App([string]$Directory) {
    $uninstaller = Join-Path $Directory 'installer/unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw "Uninstaller missing: $uninstaller" }
    Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') (Join-Path $work 'uninstall.log')
    $remaining = $hive.OpenSubKey($uninstallKey)
    if ($null -ne $remaining) { $remaining.Dispose(); throw 'Uninstall entry remains after successful uninstall.' }
    if (Test-Path -LiteralPath $shortcutPath) { throw 'Installer-owned REPL shortcut remains after uninstall.' }
}
function Invoke-Msync([string]$Executable, [string[]]$Arguments) {
    $output = & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Installed msync failed ($LASTEXITCODE): $Arguments" }
    return $output
}
function Assert-InstalledUx([string]$Directory) {
    $exe = Join-Path $Directory 'bin/msync.exe'
    $previousProcessPath = $env:Path
    try {
        # Simulate a restarted terminal; do not change Machine/User PATH here.
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        $resolved = Get-Command msync.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
        if ($resolved.Source -ine $exe) { throw "Refreshed terminal resolved the wrong msync: $($resolved.Source)" }
        $reported = & msync.exe --version
        if ($LASTEXITCODE -ne 0 -or "$reported".Trim() -ne "msync $version") { throw 'Named msync command is not usable after PATH installation.' }
    } finally { $env:Path = $previousProcessPath }
    if (-not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) { throw 'Installed REPL shortcut is missing.' }
    $shell = New-Object -ComObject WScript.Shell
    $link = $null
    try {
        # CreateShortcut reads an existing link; no Save call or unrelated edit.
        $link = $shell.CreateShortcut($shortcutPath)
        if ($link.TargetPath -ine $exe -or $link.Arguments -cne 'repl' -or $link.WorkingDirectory -ine $env:USERPROFILE) {
            throw 'Installed REPL shortcut has an unexpected executable, arguments or working directory.'
        }
    } finally {
        if ($null -ne $link) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
        $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}

function Assert-InstalledJourney([string]$Directory) {
    $exe = Join-Path $Directory 'bin/msync.exe'
    $reported = Invoke-Msync $exe @('--version')
    if ("$reported".Trim() -ne "msync $version") { throw "Installed version mismatch: $reported" }
    $journey = Join-Path $work ('journey-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $journey
    $utf8 = New-Object Text.UTF8Encoding $false
    [IO.File]::WriteAllText((Join-Path $journey 'source.txt'), 'installed artifact payload 可莉', $utf8)
    [IO.File]::WriteAllText((Join-Path $journey 'sync.msync'), "macro copy_one(source, destination):`n    source -> destination`n`ncopy_one(source.txt, output.txt)`n", $utf8)
    Push-Location $journey
    try {
        $null = Invoke-Msync $exe @('check', 'sync.msync')
        $null = Invoke-Msync $exe @('run', 'sync.msync', '--dry-run')
        if (Test-Path -LiteralPath 'output.txt') { throw 'Installed dry-run created an output.' }
        $null = Invoke-Msync $exe @('run', 'sync.msync')
        if ([IO.File]::ReadAllText((Join-Path $journey 'output.txt')) -cne 'installed artifact payload 可莉') { throw 'Installed copy bytes differ.' }
        $null = Invoke-Msync $exe @('emit-ir', 'sync.msync', '-o', 'portable.json')
        $null = Invoke-Msync $exe @('bind-ir', 'portable.json', '-o', 'bound.json')
        $null = Invoke-Msync $exe @('plan-ir', 'bound.json')
        $null = Invoke-Msync $exe @('compile', 'sync.msync', '-o', 'macro.mobj')
        Remove-Item -LiteralPath 'sync.msync'
        Remove-Item -LiteralPath 'output.txt'
        $null = Invoke-Msync $exe @('run', 'macro.mobj')
        if ([IO.File]::ReadAllText((Join-Path $journey 'output.txt')) -cne 'installed artifact payload 可莉') { throw 'Installed macro object replay bytes differ.' }
        $replInput = "macro copy_one(source, destination):`n    source -> destination`n`ncopy_one(source.txt, repl-output.txt)`n/export-relocatable repl.json`n/quit`n"
        $replOutput = $replInput | & $exe repl 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Installed multiline macro REPL failed: $replOutput" }
        if ([IO.File]::ReadAllText((Join-Path $journey 'repl-output.txt')) -cne 'installed artifact payload 可莉') { throw 'Installed multiline macro REPL bytes differ.' }
        Remove-Item -LiteralPath 'repl-output.txt'
        $null = Invoke-Msync $exe @('run-ir', 'repl.json')
        if ([IO.File]::ReadAllText((Join-Path $journey 'repl-output.txt')) -cne 'installed artifact payload 可莉') { throw 'Installed macro REPL export bytes differ.' }

        Remove-Item -LiteralPath 'output.txt'
        $null = Invoke-Msync $exe @('run-ir', 'bound.json')
        if ([IO.File]::ReadAllText((Join-Path $journey 'output.txt')) -cne 'installed artifact payload 可莉') { throw 'Installed IR replay bytes differ.' }
    } finally { Pop-Location }
}

function Remove-OwnedWorkspace([string]$Directory) {
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try {
            Remove-Item -LiteralPath $Directory -Recurse -Force -ErrorAction Stop
            return
        } catch {
            $code = 0
            for ($cause = $_.Exception; $null -ne $cause; $cause = $cause.InnerException) {
                $native = $cause.HResult -band 0xffff
                if ($native -in @(32, 33)) { $code = $native; break }
            }
            # Only Windows sharing/lock violations are transient here. Preserve
            # all other errors rather than turning cleanup into a false pass.
            if ($code -eq 0) { throw }
            if ($elapsed.Elapsed.TotalSeconds -ge 10) {
                throw "Timed out cleaning owned workspace $Directory (Win32 error $code); diagnostics retained. Cause: $($cause.Message)"
            }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Assert-PortablePackage {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($portable)
    try {
        foreach ($entry in $archive.Entries) {
            if ([IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Contains('\') -or @($entry.FullName.Split('/')) -contains '..') {
                throw "Unsafe portable ZIP entry: $($entry.FullName)"
            }
        }
    } finally { $archive.Dispose() }
    $destination = Join-Path $work 'portable'
    Expand-Archive -LiteralPath $portable -DestinationPath $destination
    $roots = @(Get-ChildItem -LiteralPath $destination -Directory)
    if ($roots.Count -ne 1) { throw 'Portable ZIP must contain one package directory.' }
    $payload = $roots[0].FullName
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $payload 'manifest.sha256'))) {
        $match = [regex]::Match($line, '^([a-f0-9]{64})  (.+)$')
        if (-not $match.Success) { throw "Malformed portable manifest line: $line" }
        $relative = $match.Groups[2].Value
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or @($relative.Split('/')) -contains '..' -or $relative -eq 'manifest.sha256') {
            throw "Unsafe portable manifest path: $relative"
        }
        if (-not $seen.Add($relative)) { throw "Duplicate portable manifest path: $relative" }
        $file = Join-Path $payload $relative
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $match.Groups[1].Value) {
            throw "Portable checksum mismatch: $relative"
        }
    }
    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
    if ($files.Count -ne $seen.Count + 1) { throw 'Portable payload contains unmanifested files.' }
    Assert-InstalledJourney $payload
}

$original = Read-UserPath
if ($original.Present -and $original.Kind -notin @([Microsoft.Win32.RegistryValueKind]::String, [Microsoft.Win32.RegistryValueKind]::ExpandString)) {
    $environment.Dispose(); $hive.Dispose()
    throw 'Refusing to mutate a user PATH with an unsupported registry type.'
}
$scratch = Join-Path $root '.temp'
$null = New-Item -ItemType Directory -Path $scratch -Force
$work = Join-Path $scratch ('windows-install-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$active = $null
$completed = $false
try {
    Assert-PortablePackage
    # Missing PATH, upgrade ownership, unrelated later addition, retained config.
    Write-UserPath ([pscustomobject]@{ Present = $false; Raw = $null; Kind = $null })
    $active = Join-Path $work 'absent path install'
    Install-App $active $true
    $installedPath = Read-UserPath
    $bin = Join-Path $active 'bin'
    if (-not $installedPath.Present -or $installedPath.Raw -cne $bin) { throw 'Missing user PATH was not created with the installed bin directory.' }
    Assert-InstalledJourney $active
    Assert-InstalledUx $active
    $config = Join-Path $active 'config.toml'
    [IO.File]::WriteAllText($config, 'user-owned configuration must survive')
    $data = Join-Path $active 'data'
    $null = New-Item -ItemType Directory -Path $data
    [IO.File]::WriteAllText((Join-Path $data 'note.txt'), 'user-owned data')
    $helper = Join-Path $active 'bin/custom-helper.exe'
    [IO.File]::WriteAllText($helper, 'user-owned helper sentinel, not an executable')
    Install-App $active $true
    Assert-Path $installedPath 'Upgrade'
    if ([IO.File]::ReadAllText($config) -cne 'user-owned configuration must survive' -or
        [IO.File]::ReadAllText((Join-Path $data 'note.txt')) -cne 'user-owned data' -or
        [IO.File]::ReadAllText($helper) -cne 'user-owned helper sentinel, not an executable') {
        throw 'Repair/upgrade changed user configuration, data or a custom helper.'
    }
    Assert-InstalledJourney $active
    Assert-InstalledUx $active
    $addon = Join-Path $work 'unrelated addon'
    $withAddon = [pscustomobject]@{ Present = $true; Raw = "$($installedPath.Raw);$addon"; Kind = $installedPath.Kind }
    Write-UserPath $withAddon
    Uninstall-App $active
    Assert-Path ([pscustomobject]@{ Present = $true; Raw = $addon; Kind = $installedPath.Kind }) 'Uninstall with later PATH addition'
    if ([IO.File]::ReadAllText($config) -cne 'user-owned configuration must survive') { throw 'Uninstall removed user configuration.' }
    if ([IO.File]::ReadAllText((Join-Path $data 'note.txt')) -cne 'user-owned data') { throw 'Uninstall removed user data.' }
    if ([IO.File]::ReadAllText($helper) -cne 'user-owned helper sentinel, not an executable') { throw 'Uninstall removed a user-added bin helper.' }
    $active = $null

    foreach ($kind in @([Microsoft.Win32.RegistryValueKind]::String, [Microsoft.Win32.RegistryValueKind]::ExpandString)) {
        $baseline = [pscustomobject]@{ Present = $true; Raw = ';C:\First;;%USERPROFILE%\kept;%MSYNC_UNEXPANDED_TEST%;C:\Last;'; Kind = $kind }
        Write-UserPath $baseline
        $active = Join-Path $work ("existing-$kind")
        Install-App $active $true
        $added = Read-UserPath
        if ($added.Raw -cne ($baseline.Raw + ';' + (Join-Path $active 'bin')) -or $added.Kind -ne $kind) { throw 'Installer rewrote unrelated raw PATH text or registry kind.' }
        Install-App $active $true
        Assert-Path $added 'Upgrade with existing PATH'
        Uninstall-App $active
        Assert-Path $baseline 'Uninstall exact PATH restoration'
        $active = $null
    }

    # A preexisting matching entry is user-owned, never an uninstall target.
    $active = Join-Path $work 'already in path'
    $baseline = [pscustomobject]@{ Present = $true; Raw = 'C:\First;"' + (Join-Path $active 'bin').ToUpperInvariant() + '\";C:\Last'; Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString }
    Write-UserPath $baseline
    Install-App $active $true
    Assert-Path $baseline 'Install over preexisting matching entry'
    Uninstall-App $active
    Assert-Path $baseline 'Uninstall of preexisting matching entry'
    $active = $null

    # Declining the task must not touch PATH at all.
    $baseline = [pscustomobject]@{ Present = $true; Raw = 'C:\Keep;;%MSYNC_UNEXPANDED_TEST%;'; Kind = [Microsoft.Win32.RegistryValueKind]::String }
    Write-UserPath $baseline
    $active = Join-Path $work 'path declined'
    Install-App $active $false
    Assert-Path $baseline 'Declined PATH task'
    Uninstall-App $active
    Assert-Path $baseline 'Uninstall after declined PATH task'
    $active = $null
    $completed = $true
} finally {
    try {
        if ($active -and (Test-Path -LiteralPath (Join-Path $active 'installer/unins000.exe'))) { Uninstall-App $active }
    } finally {
        Write-UserPath $original
        # These keys were absent before this isolated test. Remove only test-owned remnants.
        $hive.DeleteSubKeyTree($uninstallKey, $false)
        $hive.DeleteSubKeyTree($ownerKey, $false)
        $environment.Dispose()
        $hive.Dispose()
        if ($completed) { Remove-OwnedWorkspace $work }
        else { Write-Warning "Retained installer diagnostics and fixture files at $work" }
    }
}
Write-Host 'PASS: portable manifest/CLI/IR, installed CLI/IR, upgrade, PATH ownership/types, declined PATH, user config/data/helper retention.'
