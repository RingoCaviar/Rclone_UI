[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RcloneExecutable,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = "Stop"
$executable = (Resolve-Path -LiteralPath $RcloneExecutable).Path
$absoluteOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $absoluteOutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$work = Join-Path $outputDirectory ("preview-" + [Guid]::NewGuid().ToString("N"))
$source = New-Item -ItemType Directory -Path (Join-Path $work "source") -Force
$target = New-Item -ItemType Directory -Path (Join-Path $work "target") -Force
[IO.File]::WriteAllText((Join-Path $source "copy.txt"), "source-copy")
[IO.File]::WriteAllText((Join-Path $source "replace.txt"), "source-replace")
[IO.File]::WriteAllText((Join-Path $target "replace.txt"), "target-replace")
[IO.File]::WriteAllText((Join-Path $target "delete.txt"), "target-only")
$combined = Join-Path $work "combined.txt"
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$address = "http://127.0.0.1:$port"
$process = Start-Process -FilePath $executable -ArgumentList @("rcd", "--rc-addr=127.0.0.1:$port", "--rc-no-auth", "--config=NUL", "--log-level=ERROR") -PassThru -WindowStyle Hidden

function Invoke-Rc([string]$Endpoint, [hashtable]$Body = @{}) {
    Invoke-RestMethod -Method Post -Uri "$address/$Endpoint" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 10
}

try {
    $version = $null
    for ($attempt = 0; $attempt -lt 100 -and $null -eq $version; $attempt++) {
        try { $version = Invoke-Rc "core/version" } catch { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $version) { throw "rclone RC did not become ready." }
    $sourceFs = $source.FullName.Replace('\','/') + "/"
    $targetFs = $target.FullName.Replace('\','/') + "/"
    $beforeSource = Invoke-Rc "operations/list" @{ fs = $sourceFs; remote = ""; recurse = $true }
    $beforeTarget = Invoke-Rc "operations/list" @{ fs = $targetFs; remote = ""; recurse = $true }
    $started = Invoke-Rc "sync/sync" @{ srcFs = $sourceFs; srcRemote = ""; dstFs = $targetFs; dstRemote = ""; combined = $combined; _async = $true; _group = "preview-validation"; _config = @{ DryRun = $true; Retries = 1 } }
    do { Start-Sleep -Milliseconds 100; $status = Invoke-Rc "job/status" @{ jobid = $started.jobid } } while (-not $status.finished)
    $check = Invoke-Rc "operations/check" @{ srcFs = $sourceFs; srcRemote = ""; dstFs = $targetFs; dstRemote = ""; oneWay = $false }
    $afterTarget = Invoke-Rc "operations/list" @{ fs = $targetFs; remote = ""; recurse = $true }
    $changeFields = @("combined", "differ", "missingOnSrc", "missingOnDst", "match", "destAfter")
    $returnedFields = @($changeFields | Where-Object { $status.PSObject.Properties.Name -contains $_ })
    $checkFields = @("combined", "missingOnSrc", "missingOnDst", "match", "differ", "error")
    $checkStructuredFields = @($checkFields | Where-Object { $check.PSObject.Properties.Name -contains $_ })
    $result = [ordered]@{
        rcloneVersion = $version.version
        architecture = $version.arch
        operation = "sync/sync"
        dryRun = $true
        retries = 1
        jobSucceeded = [bool]$status.success
        jobStatusChangeFields = $returnedFields
        rcCombinedParameterHonored = Test-Path -LiteralPath $combined
        loggerLineCount = if (Test-Path -LiteralPath $combined) { @(Get-Content -LiteralPath $combined).Count } else { 0 }
        sourceListingCount = @($beforeSource.list).Count
        targetListingCountBefore = @($beforeTarget.list).Count
        targetListingCountAfter = @($afterTarget.list).Count
        dryRunLeftTargetUnchanged = (@($beforeTarget.list).Count -eq @($afterTarget.list).Count)
        checkStructuredFields = $checkStructuredFields
        checkHasStructuredArrays = ($checkStructuredFields.Count -gt 0)
        directRcChangeSetComplete = ($returnedFields.Count -gt 0)
        conclusion = if ($returnedFields.Count -eq 0 -and -not (Test-Path -LiteralPath $combined)) { "rc-sync-exposes-neither-change-set-nor-command-logger" } else { "unexpected-contract" }
    }
    [IO.File]::WriteAllText($absoluteOutputPath, ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    if (-not $status.success -or -not $result.dryRunLeftTargetUnchanged -or -not $result.checkHasStructuredArrays -or $result.conclusion -ne "rc-sync-exposes-neither-change-set-nor-command-logger") { throw "rclone preview validation failed closed." }
}
finally {
    try { Invoke-Rc "core/quit" | Out-Null } catch {}
    if (-not $process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
}
