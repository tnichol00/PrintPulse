param([string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & $DotNet build PrintPulse.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $DotNet run --project PrintPulse.Tests -c Release --no-build -- (Join-Path $PSScriptRoot '..\PrintPulse-test-evidence')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $DotNet publish PrintPulse.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o (Join-Path $PSScriptRoot '..\PrintPulse-win-x64')
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Close any running PrintPulse copy before rebuilding.' }
} finally { Pop-Location }
