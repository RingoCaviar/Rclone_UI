[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "0.0.0-local",
    [string]$SigningThumbprint = "",
    [string]$RcloneExecutable = "",
    [string]$RcloneVersion = "",
    [string]$LibArgon2Library = "",
    [string]$LibArgon2Version = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $repositoryRoot "artifacts\portable\$Runtime"
$release = Join-Path $repositoryRoot "artifacts\release"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging, $release -Force | Out-Null

foreach ($project in @("Desktop", "Host", "Updater")) {
    $projectPath = Join-Path $repositoryRoot "src\RcloneUI.$project\RcloneUI.$project.csproj"
    dotnet publish $projectPath --configuration $Configuration --runtime $Runtime --self-contained true --no-restore --output (Join-Path $staging $project.ToLowerInvariant())
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Copy-Item (Join-Path $repositoryRoot "README.md") $staging
Copy-Item (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") $staging
if ($RcloneExecutable) {
    if (-not $RcloneVersion) { throw "-RcloneVersion is required with -RcloneExecutable." }
    $rcloneDirectory = New-Item -ItemType Directory -Path (Join-Path $staging "components\rclone") -Force
    Copy-Item -LiteralPath (Resolve-Path $RcloneExecutable) (Join-Path $rcloneDirectory "rclone.exe")
    $rcloneHash = (Get-FileHash (Join-Path $rcloneDirectory "rclone.exe") -Algorithm SHA256).Hash
    $rcloneManifest = [ordered]@{ format = 1; version = $RcloneVersion; sha256 = $rcloneHash; executable = "rclone.exe" }
    [IO.File]::WriteAllText((Join-Path $rcloneDirectory "manifest.json"), ($rcloneManifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
if ($LibArgon2Library) {
    if (-not $LibArgon2Version) { throw "-LibArgon2Version is required with -LibArgon2Library." }
    $argon2Directory = New-Item -ItemType Directory -Path (Join-Path $staging "components\libargon2") -Force
    Copy-Item -LiteralPath (Resolve-Path $LibArgon2Library) (Join-Path $argon2Directory "argon2.dll")
    $argon2Hash = (Get-FileHash (Join-Path $argon2Directory "argon2.dll") -Algorithm SHA256).Hash
    $argon2Manifest = [ordered]@{ format = 1; version = $LibArgon2Version; sha256 = $argon2Hash; library = "argon2.dll" }
    [IO.File]::WriteAllText((Join-Path $argon2Directory "manifest.json"), ($argon2Manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
if ($SigningThumbprint) {
    $signTool = Get-Command signtool.exe -ErrorAction Stop
    Get-ChildItem $staging -Recurse -Filter *.exe | ForEach-Object { & $signTool.Source sign /sha1 $SigningThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $_.FullName; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
}

$files = Get-ChildItem $staging -Recurse -File | Sort-Object { $_.FullName.Substring($staging.Length) }
$manifestFiles = @($files | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($staging.Length + 1).Replace('\','/'); size = $_.Length; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } })
$manifest = [ordered]@{ format = 1; version = $Version; runtime = $Runtime; minimumOs = "Windows 10 22H2 x64"; files = $manifestFiles }
$manifestPath = Join-Path $staging "release-manifest.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

$zipPath = Join-Path $release "RcloneUI-$Version-$Runtime.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        Get-ChildItem $staging -Recurse -File | Sort-Object { $_.FullName.Substring($staging.Length) } | ForEach-Object {
            $name = $_.FullName.Substring($staging.Length + 1).Replace('\','/')
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $input = [IO.File]::OpenRead($_.FullName); $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
Get-FileHash $zipPath -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $(Split-Path $zipPath -Leaf)" } | Set-Content (Join-Path $release "SHA256SUMS") -Encoding ascii
