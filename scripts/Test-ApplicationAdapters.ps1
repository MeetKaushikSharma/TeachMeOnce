$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$fixture = Join-Path $root 'src\TeachMeOnce.WpfFixture\bin\Release\net10.0-windows\win-x64\TeachMeOnce.WpfFixture.exe'
$integrationProject = Join-Path $root 'src\TeachMeOnce.Application.IntegrationTests\TeachMeOnce.Application.IntegrationTests.csproj'

Push-Location $root
try {
    dotnet build (Join-Path $root 'TeachMeOnce.sln') --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'The Release solution build failed.'
    }

    if (-not (Test-Path -LiteralPath $fixture)) {
        throw "The WPF fixture executable was not found: $fixture"
    }

    dotnet run --project $integrationProject --configuration $configuration --no-build -- --fixture $fixture
    if ($LASTEXITCODE -ne 0) {
        throw 'Application adapter integration checks failed.'
    }

    Write-Host 'Application adapter integration checks passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
