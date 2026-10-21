param(
    [string]$OutputPath = 'artifacts\publish\TeachMeOnce.App'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\TeachMeOnce.App\TeachMeOnce.App.csproj'
$output = Join-Path $root $OutputPath

Push-Location $root
try {
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }

    dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $output
    if ($LASTEXITCODE -ne 0) {
        throw 'TeachMeOnce.App publishing failed.'
    }

    $executable = Join-Path $output 'TeachMeOnce.App.exe'
    if (-not (Test-Path -LiteralPath $executable)) {
        throw "Published executable was not found: $executable"
    }

    Write-Host "TeachMeOnce.App self-contained folder published to $output" -ForegroundColor Green
}
finally {
    Pop-Location
}
