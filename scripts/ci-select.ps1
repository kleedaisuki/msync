param(
    [string[]] $Paths = @(),
    [string] $Base,
    [switch] $GitHubOutput
)

$ErrorActionPreference = 'Stop'
if ($Base) {
    $Paths = @(git diff --name-only $Base HEAD)
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect changes against $Base" }
}

$targets = [System.Collections.Generic.SortedSet[string]]::new()
$full = $false
$docs = $false
$rust = $false

function Add-Targets([string[]] $Names) {
    foreach ($name in $Names) {
        if (Test-Path -LiteralPath (Join-Path $PSScriptRoot "../tests/$name.rs")) {
            [void] $targets.Add($name)
        }
    }
}

foreach ($path in $Paths) {
    switch -Regex ($path.Replace('\', '/')) {
        '^\.github/workflows/windows-backend\.yml$' {
            # This workflow proves dependency installation and ABI independently.
            break
        }
        '^(Cargo\.(toml|lock)|build\.rs|\.github/)' {
            $full = $true
            $rust = $true
            $docs = $true
            break
        }
        '^scripts/(ci-|test-ci-)' {
            # The changes job always runs the selector's own regression cases.
            break
        }
        '^tests/support/pty' {
            $rust = $true
            Add-Targets @('repl_terminal')
            break
        }
        '^tests/([^/]+)\.rs$' {
            $rust = $true
            Add-Targets @($Matches[1])
            break
        }
        '^src/(language/|compiler/structured\.rs|hir[/.]|pipeline\.rs|source\.rs)' {
            $rust = $true
            Add-Targets @('language_v4', 'join_v4', 'version_v4', 'source_v4', 'lowering_v4', 'hir_plan_v4', 'pipeline_v4', 'submission_v4', 'review_foundation_v4', 'execution_v4', 'preparation_v4', 'cli_structured_v4', 'macro_native_v4')
            break
        }
        '^src/preparation[/.]' {
            $rust = $true
            Add-Targets @('preparation_v4', 'source_v4', 'hir_plan_v4', 'pipeline_v4', 'cli_structured_v4', 'program_execution_v4', 'macro_native_v4')
            break
        }
        '^src/transaction/' {
            $rust = $true
            Add-Targets @('transaction_store_v4', 'transaction_filesystem_v4', 'transaction_manager_v4', 'process_view_linux_v4', 'process_view_windows_v4', 'review_foundation_v4', 'review_metadata_v4', 'execution_v4', 'execution_workspace_v4', 'program_execution_v4', 'macro_native_v4', 'physical_execution_v4', 'cli_structured_v4')
            break
        }
        '^src/execution/' {
            $rust = $true
            Add-Targets @('execution_v4', 'execution_workspace_v4', 'transaction_manager_v4', 'review_metadata_v4', 'program_execution_v4', 'macro_native_v4', 'physical_execution_v4', 'endpoint_execution_v4', 'cli_structured_v4', 'runtime_task_telemetry_v4', 'structured_telemetry_v4', 'repl_jobs')
            break
        }
        '^src/process/' {
            $rust = $true
            Add-Targets @('process_v4', 'process_view_linux_v4', 'process_view_windows_v4', 'review_foundation_v4', 'execution_v4', 'program_execution_v4', 'macro_native_v4', 'physical_execution_v4')
            break
        }
        '^src/(main|structured)\.rs$' {
            $rust = $true
            Add-Targets @('cli', 'cli_structured_v4', 'version_v4', 'ux_031', 'repl_jobs', 'repl_terminal', 'review_ui_031', 'execution_v4', 'process_view_linux_v4', 'process_view_windows_v4', 'program_execution_v4', 'macro_native_v4', 'structured_telemetry_v4', 'runtime_task_telemetry_v4')
            break
        }
        '^src/(editor|repl|presentation|shell|highlight|suggest)\.rs$' {
            $rust = $true
            Add-Targets @('cli', 'cli_structured_v4', 'ux_031', 'repl_jobs', 'repl_terminal', 'review_ui_031', 'execution_v4')
            break
        }
        '^src/(compiler|syntax)\.rs$' {
            $rust = $true
            Add-Targets @('compiler', 'syntax', 'cli', 'review_v2', 'language_v4', 'lowering_v4', 'execution_v4')
            break
        }
        '^src/' {
            # Shared I/O and scheduling changes affect transport safety contracts.
            $rust = $true
            $full = $true
            break
        }
        '^README\.md$' { $docs = $true; break }
        '^tests/support/' { $rust = $true; $full = $true; break }
    }
}

$selection = [ordered]@{
    rust = $rust
    full = $full
    docs = $docs
    targets = @($targets)
}
if ($GitHubOutput) {
    if (-not $env:GITHUB_OUTPUT) { throw 'GITHUB_OUTPUT is not defined' }
    "rust=$($rust.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
    "full=$($full.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
    "docs=$($docs.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
    "targets=$(ConvertTo-Json -InputObject @($targets) -Compress)" >> $env:GITHUB_OUTPUT
}
ConvertTo-Json -InputObject $selection -Compress
