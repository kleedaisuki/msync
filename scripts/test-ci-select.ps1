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
Write-Output 'CI selection: 7 discriminating cases passed'
