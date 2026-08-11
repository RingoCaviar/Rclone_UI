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
$work = New-Item -ItemType Directory -Path (Join-Path $outputDirectory ("mount-readiness-" + [Guid]::NewGuid().ToString("N"))) -Force
$config = Join-Path $work "rclone.conf"
[IO.File]::WriteAllText($config, "", [Text.UTF8Encoding]::new($false))
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$address = "http://127.0.0.1:$port"
$process = Start-Process -FilePath $rclone -ArgumentList @("rcd", "--rc-addr=127.0.0.1:$port", "--rc-no-auth", "--config=$config", "--log-level=ERROR") -PassThru -WindowStyle Hidden

function Invoke-Rc([string]$Endpoint, [hashtable]$Body = @{}) {
    Invoke-RestMethod -Method Post -Uri "$address/$Endpoint" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 15
}

function Wait-Until([scriptblock]$Predicate, [int]$Milliseconds = 10000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Predicate) { return $timer.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 100
    } while ($timer.ElapsedMilliseconds -lt $Milliseconds)
    return $null
}

try {
    $version = $null
    for ($attempt = 0; $attempt -lt 100 -and $null -eq $version; $attempt++) {
        try { $version = Invoke-Rc "core/version" } catch { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $version) { throw "rclone RC did not become ready." }
    $mountTypes = Invoke-Rc "mount/types"
    if (@($mountTypes.mountTypes) -notcontains "cmount") { throw "rclone cmount is unavailable; WinFsp is not usable." }

    $results = foreach ($mode in @("fixed-directory", "fixed-drive", "network-drive")) {
        $id = [Guid]::NewGuid().ToString("N")
        $source = New-Item -ItemType Directory -Path (Join-Path $work "source-$id") -Force
        $marker = "rclone-ui-readiness-$id"
        [IO.File]::WriteAllText((Join-Path $source "readiness.marker"), $marker)
        $mountParent = New-Item -ItemType Directory -Path (Join-Path $work "mounts") -Force
        $requested = switch ($mode) {
            "fixed-directory" { Join-Path $mountParent "directory-$id" }
            "fixed-drive" { "*" }
            "network-drive" { "\\rclone-ui-$($id.Substring(0,8))\mount" }
        }
        $sourceFs = $source.FullName.Replace('\','/') + "/"
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $mounted = Invoke-Rc "mount/mount" @{ fs = $sourceFs; mountPoint = $requested; mountType = "cmount"; mountOpt = @{ VolName = "Rclone UI $mode $($id.Substring(0,8))" } }
        $rcReturnMilliseconds = $timer.ElapsedMilliseconds
        $actual = [string]$mounted.mountPoint
        $readyMilliseconds = Wait-Until { Test-Path -LiteralPath (Join-Path $actual "readiness.marker") }
        $processAlive = -not $process.HasExited
        $namespacePresented = Test-Path -LiteralPath $actual
        $rootProbeSucceeded = $null -ne $readyMilliseconds
        $namespaceOwned = $rootProbeSucceeded -and ([IO.File]::ReadAllText((Join-Path $actual "readiness.marker")) -eq $marker)
        $driveType = if ($actual -match '^[A-Z]:') { [IO.DriveInfo]::new($actual.Substring(0, 1)).DriveType.ToString() } else { "Directory" }
        Invoke-Rc "mount/unmountall" | Out-Null
        $cleanupMilliseconds = Wait-Until { -not (Test-Path -LiteralPath $actual) } $(if ($mode -eq "network-drive") { 2000 } else { 10000 })
        [ordered]@{
            mode = $mode
            requestedMountPoint = $requested
            actualMountPoint = $actual
            rcReturnMilliseconds = $rcReturnMilliseconds
            readyMilliseconds = $readyMilliseconds
            processAlive = $processAlive
            namespacePresented = $namespacePresented
            namespaceOwnedByMarker = $namespaceOwned
            rootProbeSucceeded = $rootProbeSucceeded
            driveType = $driveType
            cleanupMilliseconds = $cleanupMilliseconds
            cleanupCompleted = ($null -ne $cleanupMilliseconds)
            cleanupRequiresRcdExit = $false
        }
    }
    $networkResult = @($results | Where-Object { $_.mode -eq "network-drive" } | Select-Object -First 1)
    if ($networkResult.Count -eq 1 -and -not $networkResult[0].cleanupCompleted) {
        try { Invoke-Rc "core/quit" | Out-Null } catch {}
        $process.WaitForExit(5000) | Out-Null
        $cleanupAfterExit = Wait-Until { -not (Test-Path -LiteralPath $networkResult[0].actualMountPoint) } 10000
        $networkResult[0].cleanupMilliseconds = $cleanupAfterExit
        $networkResult[0].cleanupCompleted = ($null -ne $cleanupAfterExit)
        $networkResult[0].cleanupRequiresRcdExit = $true
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $winFsp = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\WinFsp" -ErrorAction SilentlyContinue
    $document = [ordered]@{
        rcloneVersion = $version.version
        winFspLauncherRunning = ((Get-Service WinFsp.Launcher -ErrorAction SilentlyContinue).Status -eq "Running")
        winFspSideBySideDirectory = $winFsp.SxsDir
        windowsVersion = [Environment]::OSVersion.Version.ToString()
        currentTokenElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        results = @($results)
    }
    [IO.File]::WriteAllText($output, ($document | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    if (-not $document.winFspLauncherRunning -or @($results | Where-Object { -not $_.processAlive -or -not $_.namespacePresented -or -not $_.namespaceOwnedByMarker -or -not $_.rootProbeSucceeded -or -not $_.cleanupCompleted }).Count -gt 0) {
        throw "Windows Mount readiness validation failed closed."
    }
}
finally {
    try { Invoke-Rc "core/quit" | Out-Null } catch {}
    if (-not $process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
}
