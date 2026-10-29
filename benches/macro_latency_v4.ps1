param(
    [Parameter(Mandatory)] [string] $Executable,
    [string] $Candidate,
    [ValidateRange(1, 31)] [int] $Samples = 3,
    [ValidateSet('local', 'mixed', 'both')] [string] $Endpoint = 'both',
    [ValidateRange(0, 10000)] [int] $UnrelatedFiles = 0,
    [ValidateRange(0, 10000)] [int] $DestinationSiblingFiles = 0,
    [ValidateSet('existing', 'missing')] [string] $DestinationState = 'existing',
    [switch] $RequirePureStatic,
    [ValidateSet('check', 'compile', 'plan', 'run')]
    [string[]] $Commands = @('check', 'compile', 'plan', 'run')
)

$ErrorActionPreference = 'Stop'
if ($DestinationState -eq 'missing' -and $DestinationSiblingFiles -gt 0) {
    throw 'Destination sibling controls require existing output parents.'
}
$repository = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path $repository '.temp/macro-latency-v4'
[void] (New-Item -ItemType Directory -Force -Path $temporary)
$id = [guid]::NewGuid().ToString('N')
$fixture = Join-Path $temporary "fixture-$id"
$results = Join-Path $temporary "results-$id"
[void] (New-Item -ItemType Directory -Path $fixture, $results)
$remote = $null
$images = @([pscustomobject]@{name='before'; path=(Resolve-Path -LiteralPath $Executable).Path})
if ($Candidate) {
    $images += [pscustomobject]@{name='after'; path=(Resolve-Path -LiteralPath $Candidate).Path}
}
$rows = [System.Collections.Generic.List[object]]::new()
$record = Join-Path $results 'samples.jsonl'

function Record([object] $Value) {
    $text = ConvertTo-Json -InputObject $Value -Depth 8 -Compress
    Add-Content -LiteralPath $record -Value $text -Encoding utf8
    Write-Output $text
}

function Invoke-Child([string] $Image, [string[]] $Arguments, [string] $Working) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($Image)
    $start.WorkingDirectory = $Working
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['NO_COLOR'] = '1'
    $start.Environment['WSL_UTF8'] = '1'
    [void] $start.Environment.Remove('MSYNC_TRACE')
    foreach ($argument in $Arguments) { [void] $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    if (!$process.Start()) { throw "Cannot start $Image" }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(120000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "Timed out: $Image $Arguments"
    }
    $output = $stdout.GetAwaiter().GetResult()
    $errorText = $stderr.GetAwaiter().GetResult()
    $clock.Stop()
    $code = $process.ExitCode
    $process.Dispose()
    if ($code -ne 0) { throw "$Image $Arguments exited ${code}: $errorText $output" }
    [pscustomobject]@{milliseconds=$clock.Elapsed.TotalMilliseconds; stdout=$output; stderr=$errorText}
}

function Wsl([string] $Script, [string[]] $Arguments = @()) {
    Invoke-Child 'C:\Windows\System32\wsl.exe' (@('--exec','sh','-c',$Script,'sh') + $Arguments) $repository
}

function Remove-FixturePath([string] $Path) {
    $absolute = [System.IO.Path]::GetFullPath($Path)
    if ($absolute -ne $fixture -and !$absolute.StartsWith($fixture + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside owned fixture: $absolute"
    }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}

function Prepare-Outputs([string] $Working) {
    foreach ($name in @('local', 'wsl')) {
        $path = Join-Path $Working $name
        Remove-FixturePath $path
        if ($DestinationState -eq 'missing') { continue }
        [void] (New-Item -ItemType Directory -Force -Path (Join-Path $path 'agents'))
        [System.IO.File]::WriteAllText((Join-Path $path 'config.toml'), 'old')
        [System.IO.File]::WriteAllText((Join-Path $path 'agents/stale'), 'must disappear')
        for ($i = 0; $i -lt $DestinationSiblingFiles; $i++) {
            $sibling = Join-Path $path ".msync-perf-sibling-$i.bin"
            [System.IO.File]::WriteAllBytes($sibling, [byte[]]::new(16))
            [System.IO.File]::SetLastWriteTimeUtc($sibling, [datetime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc))
        }
    }
}

function Verify-Outputs([string] $Working, [bool] $Mixed) {
    $count = 0
    $bytes = 0L
    foreach ($name in @('local', 'wsl')) {
        foreach ($source in Get-ChildItem -LiteralPath (Join-Path $Working 'source') -Recurse -File) {
            $relative = [System.IO.Path]::GetRelativePath((Join-Path $Working 'source'), $source.FullName)
            $target = Join-Path (Join-Path $Working $name) $relative
            $expected = [System.IO.File]::ReadAllBytes($source.FullName)
            if ($Mixed -and $name -eq 'wsl' -and $relative.Replace('\','/') -eq 'agents/group-0/agent-12.toml') {
                $expected += [byte] 87
            }
            $actual = [System.IO.File]::ReadAllBytes($target)
            if (![System.Linq.Enumerable]::SequenceEqual([byte[]]$expected, [byte[]]$actual)) {
                throw "Incorrect bytes: $target"
            }
            $count++
            $bytes += $actual.Length
        }
        if (Test-Path -LiteralPath (Join-Path $Working "$name/agents/stale")) {
            throw "Directory replacement retained old child: $name"
        }
        if (@(Get-ChildItem -LiteralPath (Join-Path $Working $name) -Recurse -File).Count -ne 14 + $DestinationSiblingFiles) {
            throw "Unexpected output files: $name"
        }
        for ($i = 0; $i -lt $DestinationSiblingFiles; $i++) {
            $sibling = Join-Path $Working "$name/.msync-perf-sibling-$i.bin"
            $actual = [System.IO.File]::ReadAllBytes($sibling)
            if (![System.Linq.Enumerable]::SequenceEqual([byte[]]::new(16), [byte[]]$actual) -or
                [System.IO.File]::GetLastWriteTimeUtc($sibling) -ne [datetime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)) {
                throw "Unrelated destination sibling changed: $sibling"
            }
        }
    }
    [pscustomobject]@{files=$count; bytes=$bytes}
}

try {
    $source = Join-Path $fixture 'source'
    [void] (New-Item -ItemType Directory -Path $source)
    [System.IO.File]::WriteAllBytes((Join-Path $source 'config.toml'), [byte[]](65..90 * 58)[0..1499])
    foreach ($i in 0..12) {
        $group = Join-Path $source "agents/group-$($i % 3)"
        [void] (New-Item -ItemType Directory -Force -Path $group)
        $length = if ($i -eq 12) { 5141 } else { 4700 }
        [System.IO.File]::WriteAllBytes((Join-Path $group "agent-$i.toml"), [byte[]]::new($length))
    }
    if ($UnrelatedFiles -gt 0) {
        $unrelated = Join-Path $fixture 'unrelated'
        [void] (New-Item -ItemType Directory -Path $unrelated)
        foreach ($i in 0..($UnrelatedFiles - 1)) {
            [System.IO.File]::WriteAllBytes((Join-Path $unrelated "$i.bin"), [byte[]]::new(8192))
        }
    }
    if ($Endpoint -ne 'local') {
        $remote = (Wsl 'mktemp -d /tmp/msync-macro-latency.XXXXXXXXXX').stdout.Trim()
        if (!$remote.StartsWith('/tmp/msync-macro-latency.')) { throw "Invalid remote root: $remote" }
        $linuxSource = (Wsl 'wslpath -a -u "$1"' @($source)).stdout.Trim()
        [void] (Wsl 'cp -a -- "$1" "$2/source"; printf W >> "$2/source/agents/group-0/agent-12.toml"' @($linuxSource, $remote))
        $remoteFilesystem = (Wsl 'stat -f -c "%T %t" "$1"; findmnt -T "$1" -o SOURCE,FSTYPE,TARGET -n' @($remote)).stdout.Trim()
        if (($remoteFilesystem -split '\r?\n')[0] -notmatch '\s+ef53$') {
            throw "The WSL source must be native ext4, not a mounted Windows path: $remoteFilesystem"
        }
        Record @{kind='remote-filesystem'; output=$remoteFilesystem}
    }
    $localDrive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($fixture))
    Record @{kind='setup'; fixture=$fixture; results=$results; unrelated_files=$UnrelatedFiles; destination_sibling_files=$DestinationSiblingFiles; destination_state=$DestinationState; local_filesystem=$localDrive.DriveFormat; local_volume=$localDrive.Name; source_files=14; source_bytes=63041; warm_wsl=($Endpoint -ne 'local')}
    foreach ($image in $images) {
        $image | Add-Member -NotePropertyName sha256 -NotePropertyValue (Get-FileHash -LiteralPath $image.path).Hash
        Record @{kind='image'; label=$image.name; path=$image.path; sha256=$image.sha256; version=(Invoke-Child $image.path @('--version') $fixture).stdout.Trim()}
    }
    $endpoints = if ($Endpoint -eq 'both') { @('local','mixed') } else { @($Endpoint) }
    foreach ($endpointName in $endpoints) {
        $second = if ($endpointName -eq 'mixed') { '$wsl::"' + $remote + '/source"' } else { '$local::codex' }
        $script = @'
macro copy_codex_config(codex, des):
    codex/config.toml -> des/config.toml
    codex/agents -> des/agents/.

codex := $pwd / "source"
copy_codex_config($local::codex, $pwd::local)
copy_codex_config(SECOND, $pwd::wsl)
'@.Replace('SECOND', $second)
        $file = Join-Path $results "$endpointName.msync"
        [System.IO.File]::WriteAllText($file, $script)
        foreach ($command in $Commands) {
            for ($sample = 0; $sample -lt $Samples; $sample++) {
                $order = if ($sample % 2 -eq 0) { @($images) } else { @($images | Sort-Object name) }
                foreach ($image in $order) {
                    Prepare-Outputs $fixture
                    $trace = Join-Path $results "$endpointName-$command-$($image.name)-$sample.jsonl"
                    $outcome = Invoke-Child $image.path @($command, $file, '--trace', $trace) $fixture
                    $row = [pscustomobject]@{kind='sample'; endpoint=$endpointName; command=$command; image=$image.name; sample=$sample; milliseconds=$outcome.milliseconds; stdout=$outcome.stdout.Trim(); trace=$trace}
                    if ($command -eq 'run') { $row | Add-Member -NotePropertyName validation -NotePropertyValue (Verify-Outputs $fixture ($endpointName -eq 'mixed')) }
                    Record $row
                    $rows.Add($row)
                    $records = @(Get-Content -LiteralPath $trace | ForEach-Object { $_ | ConvertFrom-Json })
                    $spans = @($records | Where-Object { $_.type -eq 'span' -or $_.duration_ns -ne $null })
                    $transports = @($spans | Where-Object name -eq 'transport_process')
                    $wslTransports = @($transports | Where-Object { [System.IO.Path]::GetFileName($_.attributes.program) -match '^wsl(\.exe)?$' })
                    $metadataBatches = @($spans | Where-Object name -eq 'inspect_remote_batch')
                    $privateAcquisitions = @($spans | Where-Object name -eq 'ordinary.wsl.acquire')
                    Record @{kind='transport-count'; endpoint=$endpointName; command=$command; image=$image.name; sample=$sample; processes=$transports.Count; wsl_processes=$wslTransports.Count; metadata_batches=$metadataBatches.Count; private_acquisitions=$privateAcquisitions.Count}
                    $checkPurity = $RequirePureStatic -and (!$Candidate -or $image.name -eq 'after')
                    if ($checkPurity -and $command -ne 'run') {
                        $runtimeSpans = @($spans | Where-Object { $_.name -match '^(prepare|execute|inspect_remote_batch|transport_process|ordinary\.|transaction\.)' })
                        if ($runtimeSpans.Count -gt 0) {
                            throw "Static command performed runtime work: $($runtimeSpans.name -join ', ')"
                        }
                    }
                    foreach ($group in $spans | Group-Object name) {
                        Record @{kind='phase'; endpoint=$endpointName; command=$command; image=$image.name; sample=$sample; name=$group.Name; count=$group.Count; milliseconds=($group.Group.duration_ns | Measure-Object -Sum).Sum / 1000000.0}
                    }
                }
            }
        }
    }
    foreach ($group in $rows | Group-Object endpoint, command, image) {
        $sorted = @($group.Group.milliseconds | Sort-Object)
        Record @{kind='summary'; case=$group.Name; samples=$sorted.Count; p50_ms=$sorted[[int][Math]::Floor(($sorted.Count - 1) * 0.5)]; p95_nearest_rank_ms=$sorted[[int][Math]::Ceiling($sorted.Count * 0.95) - 1]}
    }
    foreach ($image in $images) {
        if ((Get-FileHash -LiteralPath $image.path).Hash -ne $image.sha256) {
            throw "Executable changed during measurement: $($image.path)"
        }
    }
} finally {
    if ($remote) {
        [void] (Wsl 'case "$1" in /tmp/msync-macro-latency.*) chmod -R u+rwX -- "$1"; rm -rf -- "$1";; *) exit 1;; esac' @($remote))
    }
    Remove-FixturePath $fixture
}
