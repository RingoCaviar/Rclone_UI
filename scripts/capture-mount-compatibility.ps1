[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RcloneExecutable,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = "Stop"
$rclone = (Resolve-Path -LiteralPath $RcloneExecutable).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $output
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$work = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ("rclone-ui-mount-schema-" + [Guid]::NewGuid().ToString("N"))) -Force
$config = Join-Path $work "rclone.conf"
[IO.File]::WriteAllText($config, "", [Text.UTF8Encoding]::new($false))
$source = New-Item -ItemType Directory -Path (Join-Path $work "source") -Force
$mountPoint = Join-Path $work "mount"
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$address = "http://127.0.0.1:$port"
$process = Start-Process -FilePath $rclone -ArgumentList @("rcd", "--rc-addr=127.0.0.1:$port", "--rc-no-auth", "--config=$config", "--log-level=ERROR") -PassThru -WindowStyle Hidden

function Invoke-Rc([string]$Endpoint, [hashtable]$Body = @{}) {
    Invoke-RestMethod -Method Post -Uri "$address/$Endpoint" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 20) -TimeoutSec 15
}

try {
    $version = $null
    for ($attempt = 0; $attempt -lt 100 -and $null -eq $version; $attempt++) {
        try { $version = Invoke-Rc "core/version" } catch { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $version) { throw "rclone RC did not become ready." }
    $rcList = Invoke-Rc "rc/list"
    $mountTypes = Invoke-Rc "mount/types"
    $options = Invoke-Rc "options/info" @{ blocks = "mount,vfs" }
    $sourceFs = $source.FullName.Replace('\','/') + "/"
    $mounted = Invoke-Rc "mount/mount" @{ fs = $sourceFs; mountPoint = $mountPoint; mountType = "cmount"; vfsOpt = @{ CacheMode = 1 } }
    $vfsList = Invoke-Rc "vfs/list"
    $vfsName = [string]@($vfsList.vfses)[0]
    if ([string]::IsNullOrWhiteSpace($vfsName)) { throw "Mounted VFS was not discoverable." }
    $stats = Invoke-Rc "vfs/stats" @{ fs = $vfsName }
    $queue = Invoke-Rc "vfs/queue" @{ fs = $vfsName }
    $winFsp = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\WinFsp" -ErrorAction SilentlyContinue
    $document = [ordered]@{
        fixtureFormat = 1
        rcloneVersion = $version.version
        winFspSideBySideDirectory = $winFsp.SxsDir
        winFspLauncherRunning = ((Get-Service WinFsp.Launcher -ErrorAction SilentlyContinue).Status -eq "Running")
        rcList = $rcList
        mountTypes = $mountTypes
        optionsInfo = $options
        vfsStats = $stats
        vfsQueue = $queue
    }
    [IO.File]::WriteAllText($output, ($document | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}
finally {
    try { Invoke-Rc "mount/unmountall" | Out-Null } catch {}
    try { Invoke-Rc "core/quit" | Out-Null } catch {}
    if (-not $process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
    Remove-Item -LiteralPath $work.FullName -Recurse -Force -ErrorAction SilentlyContinue
}
