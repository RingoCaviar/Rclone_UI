[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot "RcloneUI.slnx"

Push-Location $repositoryRoot
try {
    dotnet restore $solution --locked-mode
    dotnet format $solution --verify-no-changes --no-restore
    dotnet build $solution --configuration $Configuration --no-restore
    dotnet test $solution --configuration $Configuration --no-build
}
finally {
    Pop-Location
}
