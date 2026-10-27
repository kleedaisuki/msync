$ErrorActionPreference = 'Stop'
$cache = Join-Path $PWD '.cache/sandboxie/1.18.8'
$logs = Join-Path $PWD '.temp/sandboxie-ci'
New-Item -ItemType Directory -Force $cache, $logs | Out-Null
$installer = Join-Path $cache 'Sandboxie-Plus-x64-v1.18.8.exe'
Invoke-WebRequest 'https://github.com/sandboxie-plus/Sandboxie/releases/download/v1.18.8/Sandboxie-Plus-x64-v1.18.8.exe' -OutFile $installer
$expected = 'f27078ff74a07906fd51476079e1a7a8f5e20c8bb75a9d2ae86169d47179ae76'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash -ine $expected) {
    throw 'The pinned Sandboxie installer digest did not match'
}
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid') { throw "Invalid installer signature: $($signature.Status)" }
$signature.SignerCertificate.Subject
$installLog = Join-Path $logs 'install.log'
$install = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=""', "/LOG=`"$installLog`"") -Wait -PassThru -WindowStyle Hidden
if ($install.ExitCode -ne 0) { throw "Sandboxie installation failed or needs a reboot: $($install.ExitCode)" }
$service = Get-Service -Name SbieSvc
$service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
$driver = Get-CimInstance Win32_SystemDriver -Filter "Name='SbieDrv'"
if (-not $driver -or $driver.State -ne 'Running') { throw 'The signed Sandboxie driver is not running' }
$home = Join-Path $env:ProgramFiles 'Sandboxie-Plus'
if (-not (Test-Path -LiteralPath (Join-Path $home 'SbieDll.dll'))) { throw 'The installed backend DLL was not found' }
if ($env:GITHUB_ENV) { "MSYNC_SANDBOXIE_HOME=$home" >> $env:GITHUB_ENV }
Write-Output "Verified Sandboxie installation: $home"
