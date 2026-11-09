#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'dist',
    [ValidateSet('win-x64', 'x86_64-pc-windows-msvc')][string]$Target = 'win-x64',
    [switch]$SkipBuild,
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$arguments = @((Join-Path $root 'packaging/build.py'), '--rid', 'win-x64', '--output', $OutputDirectory)
if ($SkipBuild) { $arguments += '--skip-build' }
if ($IsccPath) { $arguments += @('--iscc', $IsccPath) }
& python @arguments
exit $LASTEXITCODE
