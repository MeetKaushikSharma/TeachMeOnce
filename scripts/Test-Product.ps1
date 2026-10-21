$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'

Push-Location $root
try {
    dotnet build .\TeachMeOnce.sln --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'The Release solution build failed.'
    }

    dotnet run --project .\src\TeachMeOnce.Application.Tests\TeachMeOnce.Application.Tests.csproj --configuration $configuration --no-build
    if ($LASTEXITCODE -ne 0) {
        throw 'Application contract tests failed.'
    }

    & (Join-Path $PSScriptRoot 'Test-ApplicationAdapters.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Application adapter integration checks failed.'
    }

    Write-Host 'TeachMeOnce product verification passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
