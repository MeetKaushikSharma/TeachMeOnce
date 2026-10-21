$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$fixture = Join-Path $root 'src\TeachMeOnce.WpfFixture\bin\Release\net10.0-windows\win-x64\TeachMeOnce.WpfFixture.exe'
$winFormsFixture = Join-Path $root 'src\TeachMeOnce.WinFormsFixture\bin\Release\net10.0-windows\win-x64\TeachMeOnce.WinFormsFixture.exe'
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
    if (-not (Test-Path -LiteralPath $winFormsFixture)) {
        throw "The WinForms fixture executable was not found: $winFormsFixture"
    }

    dotnet run --project $integrationProject --configuration $configuration --no-build -- --fixture $fixture
    if ($LASTEXITCODE -ne 0) {
        throw 'WPF application adapter integration checks failed.'
    }

    dotnet run --project $integrationProject --configuration $configuration --no-build -- --fixture $winFormsFixture --winforms
    if ($LASTEXITCODE -ne 0) {
        throw 'WinForms application adapter integration checks failed.'
    }

    Write-Host 'Application adapter integration checks passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
