$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$artifactDirectory = Join-Path $root 'artifacts\poc-01'
$requiredSdk = '10.0.402'

Push-Location $root
try {
    $installedSdks = @(& dotnet --list-sdks 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to query installed .NET SDKs. Install .NET SDK $requiredSdk and ensure 'dotnet' is on PATH."
    }

    $matchingSdk = $installedSdks | Where-Object { $_ -match "^\s*$([regex]::Escape($requiredSdk))\s" }
    if (-not $matchingSdk) {
        throw "Required .NET SDK $requiredSdk is not installed. Install that exact SDK version; global.json disables SDK roll-forward."
    }

    $selectedSdk = (& dotnet --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "The .NET SDK selected by global.json could not be resolved. Install .NET SDK $requiredSdk and retry. Details: $selectedSdk"
    }

    if ($selectedSdk -ne $requiredSdk) {
        throw "global.json selected .NET SDK '$selectedSdk', but this workspace requires exactly '$requiredSdk'."
    }

    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

    dotnet build (Join-Path $root 'TeachMeOnce.sln') --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'The solution build failed.'
    }

function Invoke-CoverageForFixture {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectDirectory,
        [Parameter(Mandatory = $true)][string]$ExecutableName,
        [Parameter(Mandatory = $true)][string]$ReportName
    )

    $executable = Join-Path $root "src\$ProjectDirectory\bin\$configuration\net10.0-windows\win-x64\$ExecutableName"
    if (-not (Test-Path $executable)) {
        throw "Fixture executable was not found: $executable"
    }

    $process = Start-Process -FilePath $executable -PassThru
    try {
        try {
            [void]$process.WaitForInputIdle(5000)
        }
        catch {
            throw "Fixture '$ExecutableName' did not become input-idle: $($_.Exception.Message)"
        }

        $process.Refresh()
        if ($process.HasExited) {
            throw "Fixture '$ExecutableName' exited before inspection with code $($process.ExitCode)."
        }

        $report = Join-Path $artifactDirectory $ReportName
        if (Test-Path $report) {
            Remove-Item $report -Force
        }
        $harnessProject = Join-Path $root 'src\TeachMeOnce.UiAutomationCoverage\TeachMeOnce.UiAutomationCoverage.csproj'
        $harnessArguments = @(
            'run',
            '--project', $harnessProject,
            '--configuration', $configuration,
            '--no-build',
            '--',
            '--pid', $process.Id.ToString(),
            '--output', $report,
            '--timeout-seconds', '15'
        )
        Write-Host "Running coverage harness: dotnet $($harnessArguments -join ' ')"
        & dotnet @harnessArguments | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            throw "Coverage harness failed for process $($process.Id)."
        }
        if (-not (Test-Path $report)) {
            throw "Coverage report was not created: $report"
        }

        return Get-Content -Raw $report | ConvertFrom-Json
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }
}

$wpf = Invoke-CoverageForFixture `
    -ProjectDirectory 'TeachMeOnce.WpfFixture' `
    -ExecutableName 'TeachMeOnce.WpfFixture.exe' `
    -ReportName 'wpf-coverage.json'

$winForms = Invoke-CoverageForFixture `
    -ProjectDirectory 'TeachMeOnce.WinFormsFixture' `
    -ExecutableName 'TeachMeOnce.WinFormsFixture.exe' `
    -ReportName 'winforms-coverage.json'

if ($wpf.elementCount -le 0 -or $winForms.elementCount -le 0) {
    throw 'Both fixture reports must contain at least one UI Automation element.'
}

$wpfPatterns = @($wpf.elements | ForEach-Object { $_.supportedPatterns } | Sort-Object -Unique)
foreach ($requiredPattern in @('Invoke', 'Value', 'Toggle', 'ExpandCollapse', 'Scroll')) {
    if ($wpfPatterns -notcontains $requiredPattern) {
        throw "WPF fixture did not expose required pattern: $requiredPattern"
    }
}

if (($wpfPatterns -notcontains 'Selection') -and ($wpfPatterns -notcontains 'SelectionItem')) {
    throw 'WPF fixture did not expose Selection or SelectionItem.'
}

$wpfAutomationIds = @($wpf.elements | ForEach-Object { $_.automationId })
foreach ($requiredId in @('InvokeButton', 'ValueTextBox', 'PasswordBox', 'ToggleCheckBox')) {
    if ($wpfAutomationIds -notcontains $requiredId) {
        throw "WPF fixture did not expose expected automation ID: $requiredId"
    }
}

$passwordElements = @($wpf.elements | Where-Object { $_.automationId -eq 'PasswordBox' -and $_.isPassword -eq $true })
if ($passwordElements.Count -ne 1) {
    throw 'WPF password fixture was not identified with IsPassword=true.'
}

    dotnet run --project (Join-Path $root 'src\TeachMeOnce.UiAutomationCoverage\TeachMeOnce.UiAutomationCoverage.csproj') --configuration $configuration --no-build -- --title-contains 'TeachMeOnce target that does not exist' --timeout-seconds 1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -eq 0) {
        throw 'Missing target inspection unexpectedly returned success.'
    }

    Write-Host "Using .NET SDK $selectedSdk." -ForegroundColor Cyan
    Write-Host 'POC-01 fixture coverage checks passed.' -ForegroundColor Green
    Write-Host "Reports: $artifactDirectory"
}
finally {
    Pop-Location
}
