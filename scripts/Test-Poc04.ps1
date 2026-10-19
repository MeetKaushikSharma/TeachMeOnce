$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$artifactDirectory = Join-Path $root 'artifacts\poc-04'

Push-Location $root
try {
    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

    dotnet build (Join-Path $root 'TeachMeOnce.sln') --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'The Release solution build failed.'
    }

    function Invoke-WaitChecksForFixture {
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
                throw "Fixture '$ExecutableName' exited before async-wait checks with code $($process.ExitCode)."
            }

            $report = Join-Path $artifactDirectory $ReportName
            if (Test-Path $report) {
                Remove-Item $report -Force
            }

            $harnessProject = Join-Path $root 'src\TeachMeOnce.AsyncWaits\TeachMeOnce.AsyncWaits.csproj'
            $harnessArguments = @(
                'run',
                '--project', $harnessProject,
                '--configuration', $configuration,
                '--no-build',
                '--',
                '--pid', $process.Id.ToString(),
                '--output', $report
            )
            Write-Host "Running async waits: dotnet $($harnessArguments -join ' ')"
            & dotnet @harnessArguments | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) {
                throw "Async wait harness failed for process $($process.Id)."
            }

            if (-not (Test-Path $report)) {
                throw "Async wait report was not created: $report"
            }

            return Get-Content -Raw $report | ConvertFrom-Json
        }
        finally {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
    }

    function Assert-WaitReport {
        param(
            [Parameter(Mandatory = $true)][pscustomobject]$Report,
            [Parameter(Mandatory = $true)][string]$FixtureName
        )

        if ($Report.passed -ne $true) {
            throw "$FixtureName async-wait report did not pass."
        }

        $expected = @{
            'delayed element appearance' = 'Satisfied'
            'condition satisfaction' = 'Satisfied'
            'bounded timeout' = 'TimedOut'
            'prompt cancellation' = 'Cancelled'
            'missing target' = 'NotFound'
        }

        foreach ($case in $Report.cases) {
            if ($case.passed -ne $true) {
                throw "$FixtureName case '$($case.name)' failed: $($case.reason)"
            }

            if ($expected.ContainsKey($case.name) -and $case.actualStatus -ne $expected[$case.name]) {
                throw "$FixtureName case '$($case.name)' returned '$($case.actualStatus)' instead of '$($expected[$case.name])'."
            }
        }

        foreach ($requiredCase in $expected.Keys) {
            if (@($Report.cases | Where-Object { $_.name -eq $requiredCase }).Count -ne 1) {
                throw "$FixtureName did not produce exactly one '$requiredCase' case."
            }
        }

        $timeoutMatches = @($Report.cases | Where-Object { $_.name -eq 'bounded timeout' })
        $timeout = $timeoutMatches[0]
        if ($timeout.probeCount -lt 2) {
            throw "$FixtureName timeout case did not perform asynchronous polling."
        }

        $cancelledMatches = @($Report.cases | Where-Object { $_.name -eq 'prompt cancellation' })
        $cancelled = $cancelledMatches[0]
        if ($cancelled.elapsedMilliseconds -ge 2000) {
            throw "$FixtureName cancellation was not prompt: $($cancelled.elapsedMilliseconds) ms."
        }

        Write-Host "$FixtureName async-wait checks passed: $($Report.cases.Count) cases." -ForegroundColor Green
    }

    $wpfReport = Invoke-WaitChecksForFixture `
        -ProjectDirectory 'TeachMeOnce.WpfFixture' `
        -ExecutableName 'TeachMeOnce.WpfFixture.exe' `
        -ReportName 'wpf-waits.json'
    Assert-WaitReport -Report $wpfReport -FixtureName 'WPF fixture'

    $winFormsReport = Invoke-WaitChecksForFixture `
        -ProjectDirectory 'TeachMeOnce.WinFormsFixture' `
        -ExecutableName 'TeachMeOnce.WinFormsFixture.exe' `
        -ReportName 'winforms-waits.json'
    Assert-WaitReport -Report $winFormsReport -FixtureName 'WinForms fixture'

    Write-Host 'POC-04 async wait checks passed.' -ForegroundColor Green
    Write-Host "Reports: $artifactDirectory"
}
finally {
    Pop-Location
}
