[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repositoryRoot "artifacts\portable\$Runtime"

dotnet publish (Join-Path $repositoryRoot "src\RcloneUI.Desktop\RcloneUI.Desktop.csproj") --configuration $Configuration --runtime $Runtime --self-contained true --output (Join-Path $output "app")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $repositoryRoot "src\RcloneUI.Host\RcloneUI.Host.csproj") --configuration $Configuration --runtime $Runtime --self-contained true --output (Join-Path $output "host")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $repositoryRoot "src\RcloneUI.Updater\RcloneUI.Updater.csproj") --configuration $Configuration --runtime $Runtime --self-contained true --output (Join-Path $output "updater")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
