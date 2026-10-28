$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

function Select-Changes([string[]] $Paths) {
    (& "$PSScriptRoot/ci-select.ps1" -Paths $Paths) | ConvertFrom-Json
}

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$docs = Select-Changes @('docs/design/transactions-v4.md')
Assert (-not $docs.rust -and -not $docs.full) 'Design-only edits must not start Cargo jobs'
$sdk = Select-Changes @('README.md')
Assert ($sdk.docs -and -not $sdk.full) 'Embedded SDK documentation needs its doctest'
$one = Select-Changes @('tests/ux_031.rs')
Assert ($one.rust -and -not $one.full -and $one.targets.Count -eq 1 -and $one.targets[0] -eq 'ux_031') 'Single test edit must select exactly its target'
$terminal = Select-Changes @('tests/support/pty_windows.rs')
Assert (-not $terminal.full -and $terminal.targets[0] -eq 'repl_terminal') 'PTY helper must select terminal journeys'
$dependency = Select-Changes @('Cargo.lock')
Assert ($dependency.full -and $dependency.rust -and $dependency.docs) 'Dependency changes require the compatibility matrix'
$runtime = Select-Changes @('src/runtime.rs')
Assert $runtime.full 'Shared transport edits must retain broad safety coverage'
$combined = Select-Changes @('tests/ux_031.rs', 'tests/ux_031.rs', 'tests/repl_jobs.rs')
Assert ($combined.targets.Count -eq 2 -and -not $combined.full) 'Selections must be deduplicated'
$selector = Select-Changes @('scripts/ci-select.ps1', 'scripts/test-ci-select.ps1')
Assert (-not $selector.full -and -not $selector.rust) 'Selector edits are checked by this regression script, not an unrelated Rust suite'
$transactions = Select-Changes @('src/transaction/filesystem.rs')
Assert (-not $transactions.full -and $transactions.targets -contains 'transaction_filesystem_v4' -and $transactions.targets -contains 'transaction_store_v4') 'Filesystem changes must check the version store and real publisher'
$backend = Select-Changes @('.github/workflows/windows-backend.yml', 'scripts/ci-install-sandboxie.ps1')
Assert (-not $backend.rust -and -not $backend.full) 'Dependency installation is proven independently without restarting unrelated Rust suites'
$execution = Select-Changes @('src/execution/mod.rs')
Assert (-not $execution.full -and $execution.targets -contains 'execution_v4' -and $execution.targets -contains 'review_metadata_v4') 'Reference execution changes must check actual state and metadata without unrelated transport sweeps'
$production = Select-Changes @('src/execution/live.rs')
Assert ($production.targets -contains 'program_execution_v4' -and $production.targets -contains 'macro_native_v4' -and $production.targets -contains 'physical_execution_v4' -and $production.targets -contains 'endpoint_execution_v4') 'Production engine changes must select real program, typed macro and endpoint journeys'
$source = Select-Changes @('src/source.rs', 'src/language/version.rs')
Assert (-not $source.full -and $source.targets -contains 'source_v4' -and $source.targets -contains 'join_v4' -and $source.targets -contains 'version_v4' -and $source.targets -contains 'macro_native_v4' -and $source.targets -contains 'planner_proof_sharing_v4') 'Source and grammar changes need token provenance, explicit joins, native macros and value-checked proof coverage'
$prepare = Select-Changes @('src/preparation.rs')
Assert (-not $prepare.full -and $prepare.targets -contains 'preparation_v4' -and $prepare.targets -contains 'planner_proof_sharing_v4' -and $prepare.targets -contains 'cli_structured_v4') 'Preparation must check its certificate, immutable sharing and actual CLI handoff'
$finite = Select-Changes @('src/execution/materialize.rs')
Assert (-not $finite.full -and $finite.targets -contains 'execution_workspace_v4' -and $finite.targets -contains 'structured_telemetry_v4') 'Private finite IO must retain actual workspace and trace journeys'
if (Test-Path -LiteralPath 'tests/named_execution_v4.rs') {
    foreach ($path in @('src/execution/live.rs', 'src/transaction/store.rs', 'src/hir/graph.rs', 'src/preparation/state.rs')) {
        $named = Select-Changes @($path)
        Assert (-not $named.full -and $named.targets -contains 'named_execution_v4') "Named-frontier contracts must be selected for $path"
    }
}
Write-Output 'CI selection: 15 discriminating cases passed; named-frontier coverage checked when its target is present'
if (Test-Path -LiteralPath 'tests/runtime_guards_v4.rs') {
    foreach ($path in @('src/execution/ordinary.rs', 'src/transaction/store.rs', 'src/hir/graph.rs', 'src/preparation.rs')) {
        $ordinary = Select-Changes @($path)
        Assert (-not $ordinary.full -and $ordinary.targets -contains 'ordinary_execution_v4' -and $ordinary.targets -contains 'runtime_guards_v4') "Statement ownership and runtime guards must be selected for $path"
    }
    Write-Output 'Ordinary ownership and runtime guard coverage passed'
}
