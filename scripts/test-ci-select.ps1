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
Assert-Selected 'src/object/validate.rs' @('object_codec_v4', 'independent_object_integrity_v4', 'artifact_cli_v4')
Assert-Selected 'src/structured/artifact.rs' @('artifact_cli_v4', 'barrier_cli_v5', 'barrier_crash_v5')
$deleted = Select-Changes @('tests/transaction_manager_v4.rs')
Assert ($deleted.targets.Count -eq 0) 'Deleted tests must not become nonexistent Cargo targets'
Write-Output 'CI selection: barrier, filesystem recovery, native-process, artifact, and focused compatibility cases passed'
