param(
    [string[]] $Paths = @(),
    [string] $Base,
    [switch] $ForceFull,
    [switch] $GitHubOutput
)

# Only the local package version is irrelevant to dependency/feature selection.
# Refuse ambiguous/missing package metadata rather than masking another change.
function Normalize-MsyncVersion([string] $Path, [string] $Content) {
    $header = if ($Path -eq 'Cargo.toml') { '\[package\]' } else { '\[\[package\]\]' }
    $sections = [regex]::Matches($Content, "(?ms)^$header[^\S\r\n]*\r?`n.*?(?=^\[|\z)")
    $owned = @($sections | Where-Object { $_.Value -cmatch '(?m)^name\s*=\s*"msync"\s*\r?$' })
    if ($owned.Count -ne 1) { return $null }
    $section = $owned[0]
    $versions = [regex]::Matches($section.Value, '(?m)^(version\s*=\s*")[^"]+("[^\S\r\n]*(?:#[^\r\n]*)?\r?$)')
    if ($versions.Count -ne 1) { return $null }
    $version = $versions[0]
    $start = $section.Index + $version.Index + $version.Groups[1].Length
    $length = $version.Length - $version.Groups[1].Length - $version.Groups[2].Length
    $Content.Remove($start, $length).Insert($start, '<msync-version>')
}

function Test-VersionOnlyCargoChange([string] $Path, [string] $Before, [string] $After) {
    $left = Normalize-MsyncVersion $Path $Before
    $right = Normalize-MsyncVersion $Path $After
    $null -ne $left -and $null -ne $right -and $left -ceq $right
}

function Test-GitVersionOnlyCargoChange([string] $Path, [string] $Base) {
    $before = @(git show "${Base}:$Path" 2>$null)
    if ($LASTEXITCODE -ne 0) { return $false }
    $after = @(git show "HEAD:$Path" 2>$null)
    if ($LASTEXITCODE -ne 0) { return $false }
    Test-VersionOnlyCargoChange $Path ($before -join "`n") ($after -join "`n")
}

$ErrorActionPreference = 'Stop'
if ($Base) {
    $Paths = @(git diff --name-only $Base HEAD)
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect changes against $Base" }
}

$targets = [System.Collections.Generic.SortedSet[string]]::new()
$full = [bool] $ForceFull
$docs = $full
$rust = $full

function Add-Targets([string[]] $Names) {
    foreach ($name in $Names) {
        if (Test-Path -LiteralPath (Join-Path $PSScriptRoot "../tests/$name.rs")) {
            [void] $targets.Add($name)
        }
    }
}

$frontend = @('cli_v052', 'language_v052', 'symbol_v052', 'scripts_v052', 'scripts_env_v052', 'directional_barriers_v5', 'language_v4', 'language_barriers_v5', 'frontend_barriers_v5', 'join_v4', 'version_v4', 'source_v4', 'lowering_v4', 'hir_plan_v4', 'barrier_graph_v5', 'planner_optimization_v4', 'planner_proof_sharing_v4', 'pipeline_v4', 'submission_v4', 'cli_structured_v4')
$execution = @('execution_v4', 'ordinary_execution_v4', 'runtime_guards_v4', 'block_specialization_v5', 'execution_workspace_v4', 'program_execution_v4', 'macro_native_v4', 'physical_execution_v4', 'endpoint_execution_v4', 'remote_publication_v5', 'barrier_cli_v5', 'barrier_runtime_v5', 'process_exit_observation_v5', 'recovery_concurrency_v5', 'barrier_crash_v5', 'storage_crash_v5', 'cli_structured_v4', 'runtime_task_telemetry_v4', 'structured_telemetry_v4', 'repl_jobs')
$objects = @('cli_v052', 'symbol_v052', 'scripts_v052', 'scripts_env_v052', 'object_codec_v4', 'object_linker_v4', 'relocation_lowering_v4', 'artifact_cli_v4', 'independent_object_integrity_v4', 'independent_object_journeys_v4', 'independent_phase_journeys_v4', 'barrier_cli_v5')

foreach ($path in $Paths) {
    switch -Regex ($path.Replace('\', '/')) {
        '^Cargo\.(toml|lock)$' {
            $rust = $true
            if ($Base -and (Test-GitVersionOnlyCargoChange $path $Base)) {
                Add-Targets @('cli_v052', 'cli', 'cli_structured_v4', 'artifact_cli_v4', 'version_v4', 'object_codec_v4', 'object_linker_v4', 'independent_object_integrity_v4', 'language_v052', 'symbol_v052', 'scripts_v052', 'scripts_env_v052')
            } else {
                $full = $true
                $docs = $true
            }
            break
        }
        '^(build\.rs|\.github/)' {
            $full = $true
            $rust = $true
            $docs = $true
            break
        }
        '^scripts/(ci-|test-ci-)' { break }
        '^tests/support/pty' {
            $rust = $true
            Add-Targets @('repl_terminal', 'barrier_terminal_v5')
            break
        }
        '^tests/([^/]+)\.rs$' {
            $rust = $true
            Add-Targets @($Matches[1])
            break
        }
        '^src/(language/|compiler/structured(?:\.rs|/)|hir[/.]|pipeline\.rs|source\.rs)' {
            $rust = $true
            Add-Targets $frontend
            Add-Targets @('barrier_runtime_v5', 'barrier_cli_v5', 'ordinary_execution_v4', 'runtime_guards_v4', 'block_specialization_v5', 'preparation_v4')
            break
        }
        '^src/preparation[/.]' {
            $rust = $true
            Add-Targets @('ordinary_execution_v4', 'runtime_guards_v4', 'block_specialization_v5', 'preparation_v4', 'source_v4', 'hir_plan_v4', 'planner_optimization_v4', 'planner_proof_sharing_v4', 'pipeline_v4', 'cli_structured_v4', 'barrier_runtime_v5')
            break
        }
        '^src/filesystem[/.]' {
            $rust = $true
            Add-Targets @('filesystem_v5', 'selected_capture_v5', 'recovery_registry_v5', 'recovery_concurrency_v5', 'storage_crash_v5', 'storage_stage_crash_v5', 'barrier_crash_v5', 'review_foundation_v4', 'execution_v4', 'ordinary_execution_v4', 'runtime_guards_v4', 'block_specialization_v5', 'execution_workspace_v4', 'physical_execution_v4')
            break
        }
        '^src/(execution/|process[/.])' {
            $rust = $true
            Add-Targets $execution
            Add-Targets @('preparation_v4', 'planner_optimization_v4', 'planner_proof_sharing_v4', 'hir_plan_v4')
            break
        }
        '^src/(object[/.]|relocatable\.rs|context(?:\.rs|/))' {
            $rust = $true
            Add-Targets $objects
            break
        }
        '^src/(main|structured|doctor)\.rs$|^src/structured/' {
            $rust = $true
            Add-Targets @('cli_v052', 'cli', 'cli_structured_v4', 'cli_barriers_v5', 'artifact_cli_v4', 'doctor_v4', 'runtime_diagnostics_v4', 'independent_phase_journeys_v4', 'version_v4', 'ux_031', 'repl_jobs', 'repl_terminal', 'barrier_terminal_v5', 'review_ui_031', 'barrier_cli_v5', 'barrier_runtime_v5', 'process_exit_observation_v5', 'recovery_concurrency_v5', 'barrier_crash_v5', 'structured_telemetry_v4', 'runtime_task_telemetry_v4')
            break
        }
        '^src/(editor|repl|presentation|shell|highlight|suggest|progress)\.rs$' {
            $rust = $true
            Add-Targets @('language_v052', 'cli', 'cli_structured_v4', 'ux_031', 'repl_jobs', 'repl_terminal', 'barrier_terminal_v5', 'review_ui_031', 'barrier_cli_v5', 'runtime_diagnostics_v4')
            break
        }
        '^src/(compiler|syntax)\.rs$' {
            $rust = $true
            Add-Targets @('compiler', 'syntax', 'cli', 'review_v2', 'language_v4', 'lowering_v4', 'execution_v4')
            break
        }
        '^src/' {
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
