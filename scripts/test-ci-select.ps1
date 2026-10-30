$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

function Select-Changes([string[]] $Paths, [switch] $ForceFull) {
    (& "$PSScriptRoot/ci-select.ps1" -Paths $Paths -ForceFull:$ForceFull) | ConvertFrom-Json
}
function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Selected([string] $Path, [string[]] $Targets) {
    $selection = Select-Changes @($Path)
    Assert ($selection.rust -and -not $selection.full) "Focused source selection failed for $Path"
    foreach ($target in $Targets) {
        if (Test-Path -LiteralPath "tests/$target.rs") {
            Assert ($selection.targets -contains $target) "Missing $target for $Path"
        }
    }
    foreach ($target in $selection.targets) {
        Assert (Test-Path -LiteralPath "tests/$target.rs") "Selected nonexistent target $target"
        Assert ($target -notmatch 'transaction_|process_view_|named_execution') "Selected removed transaction-era target $target"
    }
}

$docs = Select-Changes @('docs/design/barrier-execution.md')
Assert (-not $docs.rust -and -not $docs.full) 'Design-only edits must not start Cargo jobs'
$forcedDocs = Select-Changes -Paths @('docs/design/barrier-execution.md') -ForceFull
Assert ($forcedDocs.rust -and $forcedDocs.full -and $forcedDocs.docs) 'Explicit full acceptance must override design-only selection'
$forcedEmpty = Select-Changes -Paths @() -ForceFull
Assert ($forcedEmpty.rust -and $forcedEmpty.full -and $forcedEmpty.docs) 'Explicit full acceptance must work without changed paths'
$sdk = Select-Changes @('README.md')
Assert ($sdk.docs -and -not $sdk.full) 'Embedded SDK documentation needs its doctest'
$one = Select-Changes @('tests/ux_031.rs')
Assert ($one.rust -and -not $one.full -and $one.targets.Count -eq 1 -and $one.targets[0] -eq 'ux_031') 'Single test edit must select exactly its target'
$terminal = Select-Changes @('tests/support/pty_windows.rs')
Assert (-not $terminal.full -and $terminal.targets -contains 'repl_terminal') 'PTY helper must select terminal journeys'
$dependency = Select-Changes @('Cargo.lock')
Assert ($dependency.full -and $dependency.rust -and $dependency.docs) 'Dependency changes require the complete compatibility matrix'
$manifestWithoutEvidence = Select-Changes @('Cargo.toml')
Assert ($manifestWithoutEvidence.full -and $manifestWithoutEvidence.docs) 'Explicit manifest paths without Git evidence must remain conservative'
$runtime = Select-Changes @('src/runtime.rs')
Assert $runtime.full 'Shared transport edits must retain broad safety coverage'
$combined = Select-Changes @('tests/ux_031.rs', 'tests/ux_031.rs', 'tests/repl_jobs.rs')
Assert ($combined.targets.Count -eq 2 -and -not $combined.full) 'Selections must be deduplicated'
$selector = Select-Changes @('scripts/ci-select.ps1', 'scripts/test-ci-select.ps1')
Assert (-not $selector.full -and -not $selector.rust) 'Selector regression checks must not start an unrelated Rust suite'
Assert-Selected 'src/filesystem.rs' @('filesystem_v5', 'selected_capture_v5', 'recovery_registry_v5', 'storage_crash_v5', 'storage_stage_crash_v5', 'barrier_crash_v5', 'ordinary_execution_v4', 'review_foundation_v4')
Assert-Selected 'src/execution/live.rs' @('barrier_cli_v5', 'barrier_runtime_v5', 'barrier_crash_v5', 'program_execution_v4', 'physical_execution_v4', 'endpoint_execution_v4')
Assert-Selected 'src/process/direct.rs' @('barrier_cli_v5', 'program_execution_v4')
Assert-Selected 'src/hir/graph.rs' @('directional_barriers_v5', 'barrier_graph_v5', 'language_barriers_v5', 'lowering_v4', 'planner_proof_sharing_v4')
Assert-Selected 'src/preparation.rs' @('preparation_v4', 'runtime_guards_v4', 'planner_optimization_v4')
Assert-Selected 'src/context.rs' @('object_codec_v4', 'object_linker_v4', 'artifact_cli_v4')
Assert-Selected 'src/compiler/structured/scripts.rs' @('scripts_v052', 'scripts_env_v052', 'cli_v052')
Assert-Selected 'src/object/validate.rs' @('scripts_env_v052')
Assert-Selected 'src/object/validate.rs' @('object_codec_v4', 'independent_object_integrity_v4', 'artifact_cli_v4')
Assert-Selected 'src/structured/artifact.rs' @('artifact_cli_v4', 'barrier_cli_v5', 'barrier_crash_v5')
$deleted = Select-Changes @('tests/transaction_manager_v4.rs')
Assert ($deleted.targets.Count -eq 0) 'Deleted tests must not become nonexistent Cargo targets'
Write-Output 'CI selection: barrier, filesystem recovery, native-process, artifact, and focused compatibility cases passed'

# Load the pure normalizer without providing Git evidence for the initial selection.
. "$PSScriptRoot/ci-select.ps1" -Paths @() | Out-Null
$manifest = "[package]`nname = `"msync`"`nversion = `"0.5.0`"`n`n[dependencies]`nserde = `"1.0`"`n"
$nextManifest = $manifest.Replace('0.5.0', '0.5.2')
$lock = "version = 4`n`n[[package]]`nname = `"dependency`"`nversion = `"0.5.0`"`n`n[[package]]`nname = `"msync`"`nversion = `"0.5.0`"`ndependencies = [`n `"dependency`",`n]`n"
$nextLock = $lock.Replace("name = `"msync`"`nversion = `"0.5.0`"", "name = `"msync`"`nversion = `"0.5.2`"")
Assert (Test-VersionOnlyCargoChange 'Cargo.toml' $manifest $nextManifest) 'Local manifest version must normalize'
Assert (Test-VersionOnlyCargoChange 'Cargo.lock' $lock $nextLock) 'Only the local lock package version must normalize'
Assert (Test-VersionOnlyCargoChange 'Cargo.toml' ($manifest.Replace("`n", "`r`n")) ($nextManifest.Replace("`n", "`r`n"))) 'CRLF package metadata must normalize'
Assert (-not (Test-VersionOnlyCargoChange 'Cargo.toml' $manifest ($nextManifest.Replace('serde = "1.0"', 'serde = "2.0"')))) 'Dependency version must not normalize'
Assert (-not (Test-VersionOnlyCargoChange 'Cargo.toml' $manifest ($nextManifest + "`n[features]`nnew = []`n"))) 'Feature changes must not normalize'
Assert (-not (Test-VersionOnlyCargoChange 'Cargo.lock' $lock ($nextLock.Replace('name = "dependency"', 'name = "replacement"')))) 'Dependency identity must not normalize'
Assert (-not (Test-VersionOnlyCargoChange 'Cargo.lock' $lock ($nextLock.Replace('0.5.0', '0.6.0')))) 'Dependency lock version must not normalize'
Assert (-not (Test-VersionOnlyCargoChange 'Cargo.lock' $lock ($nextLock.Replace('version = 4', 'version = 5')))) 'Lock format version must not normalize'
Assert (-not (Test-VersionOnlyCargoChange 'Cargo.toml' '[package]' '[package]')) 'Missing package fields must remain conservative'
Assert (-not (Test-VersionOnlyCargoChange 'Cargo.toml' ($manifest + $manifest) ($nextManifest + $nextManifest))) 'Ambiguous local package metadata must remain conservative'
Assert-Selected 'src/highlight.rs' @('language_v052', 'cli_structured_v4')
Assert-Selected 'src/compiler/structured/symbols.rs' @('language_v052', 'symbol_v052', 'scripts_v052', 'lowering_v4')
Assert-Selected 'src/compiler/structured/scripts.rs' @('language_v052', 'symbol_v052', 'scripts_v052', 'pipeline_v4')
Assert-Selected 'src/object.rs' @('object_codec_v4', 'object_linker_v4', 'independent_object_integrity_v4', 'symbol_v052', 'scripts_v052')
Assert-Selected 'src/context.rs' @('object_codec_v4', 'object_linker_v4', 'independent_object_integrity_v4', 'symbol_v052', 'scripts_v052')

# Exercise -Base against real local Git history, never the developer's index/history.
$repository = (Get-Location).Path
$fixture = Join-Path $repository ('.temp/ci-selection-' + [guid]::NewGuid().ToString('N'))
[void] [IO.Directory]::CreateDirectory($fixture)
try {
    Set-Location $fixture
    git init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize selector Git fixture' }
    [IO.File]::WriteAllText((Join-Path $fixture 'Cargo.toml'), $manifest)
    [IO.File]::WriteAllText((Join-Path $fixture 'Cargo.lock'), $lock)
    git add Cargo.toml Cargo.lock
    git -c user.name=CI -c user.email=ci@example.invalid commit --quiet -m baseline
    if ($LASTEXITCODE -ne 0) { throw 'Cannot commit selector Git fixture baseline' }
    $baseline = git rev-parse HEAD
    [IO.File]::WriteAllText((Join-Path $fixture 'Cargo.toml'), $nextManifest)
    [IO.File]::WriteAllText((Join-Path $fixture 'Cargo.lock'), $nextLock)
    git add Cargo.toml Cargo.lock
    git -c user.name=CI -c user.email=ci@example.invalid commit --quiet -m version
    if ($LASTEXITCODE -ne 0) { throw 'Cannot commit selector Git fixture version bump' }
    $versionOnly = (& "$PSScriptRoot/ci-select.ps1" -Base $baseline) | ConvertFrom-Json
    Assert ($versionOnly.rust -and -not $versionOnly.full) 'Git-proven version-only changes must stay focused'
    foreach ($target in @('cli', 'version_v4', 'object_codec_v4', 'symbol_v052', 'scripts_v052', 'language_v052')) {
        Assert ($versionOnly.targets -contains $target) "Missing version-only target $target"
    }
    $forcedVersion = (& "$PSScriptRoot/ci-select.ps1" -Base $baseline -ForceFull) | ConvertFrom-Json
    Assert ($forcedVersion.full -and $forcedVersion.docs) 'ForceFull must override a Git-proven version bump'
    [IO.File]::WriteAllText((Join-Path $fixture 'Cargo.toml'), $nextManifest.Replace('serde = "1.0"', 'serde = "2.0"'))
    git add Cargo.toml
    git -c user.name=CI -c user.email=ci@example.invalid commit --quiet -m dependency
    if ($LASTEXITCODE -ne 0) { throw 'Cannot commit selector Git fixture dependency change' }
    $changedDependency = (& "$PSScriptRoot/ci-select.ps1" -Base $baseline) | ConvertFrom-Json
    Assert ($changedDependency.full -and $changedDependency.docs) 'Git-proven dependency changes must remain full'
} finally {
    Set-Location $repository
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
Write-Output 'CI selection: v0.5.2 compiler, object metadata, pure version normalization, and real Git version/dependency cases passed'
