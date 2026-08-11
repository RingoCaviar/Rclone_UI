[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RcloneExecutable,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateSet("copy", "sync")][string]$Operation = "sync",
    [ValidateSet("local", "crypt", "memory")][string]$Backend = "local"
)

$ErrorActionPreference = "Stop"
$executable = (Resolve-Path -LiteralPath $RcloneExecutable).Path
$absoluteOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $absoluteOutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$work = Join-Path $outputDirectory ("preview-" + [Guid]::NewGuid().ToString("N"))
$source = New-Item -ItemType Directory -Path (Join-Path $work "source") -Force
$target = New-Item -ItemType Directory -Path (Join-Path $work "target") -Force
$targetSeed = New-Item -ItemType Directory -Path (Join-Path $work "target-seed") -Force
[IO.File]::WriteAllText((Join-Path $source "copy.txt"), "source-copy")
[IO.File]::WriteAllText((Join-Path $source "Case-Path.txt"), "source-replace")
[IO.File]::WriteAllText((Join-Path $targetSeed "case-path.txt"), "target-replace")
[IO.File]::WriteAllText((Join-Path $targetSeed "delete.txt"), "target-only")
$combined = Join-Path $work "combined.txt"
$configPath = Join-Path $work "rclone.conf"
if ($Backend -eq "local") {
    Copy-Item -Path (Join-Path $targetSeed "*") -Destination $target -Force
    [IO.File]::WriteAllText($configPath, "", [Text.UTF8Encoding]::new($false))
}
elseif ($Backend -eq "crypt") {
    $obscuredPassword = (& $executable obscure "preview-validation-password").Trim()
    if ($LASTEXITCODE -ne 0 -or -not $obscuredPassword) { throw "Could not create the crypt fixture password." }
    $remotePath = $target.FullName.Replace('\','/')
    $config = "[encrypted]`ntype = crypt`nremote = $remotePath`npassword = $obscuredPassword`nfilename_encryption = standard`ndirectory_name_encryption = true`n"
    [IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($false))
    & $executable copy $targetSeed.FullName "encrypted:" "--config=$configPath" --log-level ERROR
    if ($LASTEXITCODE -ne 0) { throw "Could not populate the crypt fixture." }
}
else {
    [IO.File]::WriteAllText($configPath, "[memory]`ntype = memory`n", [Text.UTF8Encoding]::new($false))
}
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$address = "http://127.0.0.1:$port"
$process = Start-Process -FilePath $executable -ArgumentList @("rcd", "--rc-addr=127.0.0.1:$port", "--rc-no-auth", "--config=$configPath", "--log-level=ERROR") -PassThru -WindowStyle Hidden

function Invoke-Rc([string]$Endpoint, [hashtable]$Body = @{}) {
    Invoke-RestMethod -Method Post -Uri "$address/$Endpoint" -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 10
}

try {
    $version = $null
    for ($attempt = 0; $attempt -lt 100 -and $null -eq $version; $attempt++) {
        try { $version = Invoke-Rc "core/version" } catch { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $version) { throw "rclone RC did not become ready." }
    $localSourceFs = $source.FullName.Replace('\','/') + "/"
    $localTargetSeedFs = $targetSeed.FullName.Replace('\','/') + "/"
    if ($Backend -eq "memory") {
        Invoke-Rc "sync/copy" @{ srcFs = $localSourceFs; srcRemote = ""; dstFs = "memory:source"; dstRemote = ""; _config = @{ Retries = 1 } } | Out-Null
        Invoke-Rc "sync/copy" @{ srcFs = $localTargetSeedFs; srcRemote = ""; dstFs = "memory:target"; dstRemote = ""; _config = @{ Retries = 1 } } | Out-Null
        $sourceFs = "memory:source"
        $targetFs = "memory:target"
    }
    else {
        $sourceFs = $localSourceFs
        $targetFs = if ($Backend -eq "crypt") { "encrypted:" } else { $target.FullName.Replace('\','/') + "/" }
    }
    $listOptions = @{ recurse = $true; showHash = $true }
    $beforeSource = Invoke-Rc "operations/list" @{ fs = $sourceFs; remote = ""; opt = $listOptions }
    $beforeTarget = Invoke-Rc "operations/list" @{ fs = $targetFs; remote = ""; opt = $listOptions }
    $endpoint = "sync/$Operation"
    $started = Invoke-Rc $endpoint @{ srcFs = $sourceFs; srcRemote = ""; dstFs = $targetFs; dstRemote = ""; combined = $combined; _async = $true; _group = "preview-validation-$Operation"; _config = @{ DryRun = $true; Retries = 1 } }
    do { Start-Sleep -Milliseconds 100; $status = Invoke-Rc "job/status" @{ jobid = $started.jobid } } while (-not $status.finished)
    $errorStarted = Invoke-Rc $endpoint @{ srcFs = "undefined-preview-remote:"; srcRemote = ""; dstFs = $targetFs; dstRemote = ""; _async = $true; _group = "preview-validation-error-$Operation"; _config = @{ DryRun = $true; Retries = 1 } }
    do { Start-Sleep -Milliseconds 100; $errorStatus = Invoke-Rc "job/status" @{ jobid = $errorStarted.jobid } } while (-not $errorStatus.finished)
    $check = Invoke-Rc "operations/check" @{ srcFs = $sourceFs; srcRemote = ""; dstFs = $targetFs; dstRemote = ""; oneWay = $false }
    $afterTarget = Invoke-Rc "operations/list" @{ fs = $targetFs; remote = ""; opt = $listOptions }
    $changeFields = @("combined", "differ", "missingOnSrc", "missingOnDst", "match", "destAfter")
    $returnedFields = @($changeFields | Where-Object { $status.PSObject.Properties.Name -contains $_ })
    $checkFields = @("combined", "missingOnSrc", "missingOnDst", "match", "differ", "error")
    $checkStructuredFields = @($checkFields | Where-Object { $check.PSObject.Properties.Name -contains $_ })
    $result = [ordered]@{
        rcloneVersion = $version.version
        architecture = $version.arch
        operation = $endpoint
        backend = $Backend
        serverSideOperationCandidate = ($Backend -eq "memory")
        dryRun = $true
        retries = 1
        jobSucceeded = [bool]$status.success
        errorFixtureFailedClosed = (-not [bool]$errorStatus.success -and -not [string]::IsNullOrWhiteSpace([string]$errorStatus.error))
        jobStatusChangeFields = $returnedFields
        rcCombinedParameterHonored = Test-Path -LiteralPath $combined
        loggerLineCount = if (Test-Path -LiteralPath $combined) { @(Get-Content -LiteralPath $combined).Count } else { 0 }
        sourceListingCount = @($beforeSource.list).Count
        targetListingCountBefore = @($beforeTarget.list).Count
        targetListingCountAfter = @($afterTarget.list).Count
        sourceListingsContainHashes = (@($beforeSource.list | Where-Object { $_.Hashes.PSObject.Properties.Count -gt 0 }).Count -eq @($beforeSource.list).Count)
        targetListingsContainHashes = (@($beforeTarget.list | Where-Object { $_.Hashes.PSObject.Properties.Count -gt 0 }).Count -eq @($beforeTarget.list).Count)
        caseInsensitiveFixture = @{ source = "Case-Path.txt"; target = "case-path.txt" }
        dryRunLeftTargetUnchanged = (@($beforeTarget.list).Count -eq @($afterTarget.list).Count)
        checkStructuredFields = $checkStructuredFields
        checkHasStructuredArrays = ($checkStructuredFields.Count -gt 0)
        directRcChangeSetComplete = ($returnedFields.Count -gt 0)
        conclusion = if ($returnedFields.Count -eq 0 -and -not (Test-Path -LiteralPath $combined)) { "rc-sync-exposes-neither-change-set-nor-command-logger" } else { "unexpected-contract" }
    }
    [IO.File]::WriteAllText($absoluteOutputPath, ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $hashExpectationMet = ($Backend -eq "crypt") -or ($result.sourceListingsContainHashes -and $result.targetListingsContainHashes)
    if (-not $status.success -or -not $result.errorFixtureFailedClosed -or -not $result.dryRunLeftTargetUnchanged -or -not $hashExpectationMet -or -not $result.checkHasStructuredArrays -or $result.conclusion -ne "rc-sync-exposes-neither-change-set-nor-command-logger") { throw "rclone preview validation failed closed." }
}
finally {
    try { Invoke-Rc "core/quit" | Out-Null } catch {}
    if (-not $process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
}
