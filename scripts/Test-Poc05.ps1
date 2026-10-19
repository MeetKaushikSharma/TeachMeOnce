$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$artifactDirectory = Join-Path $root 'artifacts\poc-05'

Push-Location $root
try {
    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

    dotnet build (Join-Path $root 'TeachMeOnce.sln') --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'The Release solution build failed.'
    }

    function Invoke-DisplayChecksForFixture {
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
                throw "Fixture '$ExecutableName' exited before display checks with code $($process.ExitCode)."
            }

            $report = Join-Path $artifactDirectory $ReportName
            if (Test-Path $report) {
                Remove-Item $report -Force
            }

            $harnessProject = Join-Path $root 'src\TeachMeOnce.DisplayRobustness\TeachMeOnce.DisplayRobustness.csproj'
            $harnessArguments = @(
                'run', '--project', $harnessProject, '--configuration', $configuration,
                '--no-build', '--', '--pid', $process.Id.ToString(), '--output', $report
            )
            Write-Host "Running display robustness: dotnet $($harnessArguments -join ' ')"
            & dotnet @harnessArguments | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) {
                throw "Display robustness harness failed for process $($process.Id)."
            }

            if (-not (Test-Path $report)) {
                throw "Display robustness report was not created: $report"
            }

            return Get-Content -Raw $report | ConvertFrom-Json
        }
        finally {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
    }

    function Assert-DisplayReport {
        param(
            [Parameter(Mandatory = $true)][pscustomobject]$Report,
            [Parameter(Mandatory = $true)][string]$FixtureName
        )

        if ($Report.executionPassed -ne $true) {
            throw "$FixtureName display robustness execution failed."
        }

        if ([int]$Report.schemaVersion -lt 3) {
            throw "$FixtureName report schema is stale; expected schema version 3 or newer."
        }

        if ($Report.coverageComplete -eq $true -and $Report.status -ne 'Passed') {
            throw "$FixtureName reported complete coverage without status 'Passed'."
        }

        if ($Report.coverageComplete -ne $true -and $Report.status -notin @('Partial', 'Failed')) {
            throw "$FixtureName reported incomplete coverage with invalid status '$($Report.status)'."
        }

        $positionCases = @($Report.cases | Where-Object { $_.name -in @('initial', 'monitor', 'resize') })
        if ($positionCases.Count -lt 2) {
            throw "$FixtureName did not produce enough display-position cases."
        }

        $monitors = @($Report.monitors)
        if ($monitors.Count -ne [int]$Report.monitorCount -or $monitors.Count -lt 1) {
            throw "$FixtureName monitor inventory did not match monitorCount."
        }

        foreach ($monitor in $monitors) {
            if ($monitor.boundsWidth -le 0 -or $monitor.boundsHeight -le 0 -or $monitor.workAreaWidth -le 0 -or $monitor.workAreaHeight -le 0) {
                throw "$FixtureName reported invalid bounds or work area for '$($monitor.deviceName)'."
            }

            if ($monitor.monitorDpi -le 0) {
                throw "$FixtureName did not report a valid inventory DPI for '$($monitor.deviceName)'."
            }
        }

        foreach ($case in $positionCases) {
            if ($case.status -ne 'Passed' -or $case.skipped -eq $true) {
                throw "$FixtureName position case '$($case.location)' was not executed successfully."
            }

            if ($case.semanticTargetAutomationId -ne 'ValueTextBox') {
                throw "$FixtureName semantic target was not stable for '$($case.location)'."
            }

            if ($case.coordinateTargetAutomationId -ne 'InvokeButton') {
                throw "$FixtureName screen coordinate mapped to '$($case.coordinateTargetAutomationId)' for '$($case.location)'."
            }

            if ($case.dpi -le 0) {
                throw "$FixtureName did not report a valid DPI for '$($case.location)'."
            }

            if ($case.monitorDpi -le 0) {
                throw "$FixtureName did not report a valid monitor DPI for '$($case.location)'."
            }
        }

        $monitorCases = @($Report.cases | Where-Object { $_.name -eq 'monitor' })
        $monitorLocations = @($monitorCases | ForEach-Object { $_.location } | Select-Object -Unique)
        if ($monitorCases.Count -ne [int]$Report.monitorCount -or $monitorLocations.Count -ne [int]$Report.monitorCount) {
            throw "$FixtureName did not produce one successful movement case per enumerated monitor."
        }

        $initialCase = $positionCases | Where-Object { $_.name -eq 'initial' } | Select-Object -First 1
        $resizeCase = $positionCases | Where-Object { $_.name -eq 'resize' } | Select-Object -First 1
        if ($initialCase.windowWidth -eq $resizeCase.windowWidth -and $initialCase.windowHeight -eq $resizeCase.windowHeight) {
            throw "$FixtureName resize case did not change the window dimensions."
        }

        $coverageCases = @($Report.cases | Where-Object { $_.name -in @('multiple monitors', 'mixed DPI settings', 'negative coordinates') })
        foreach ($coverageCase in $coverageCases) {
            if ($coverageCase.skipped -eq $true -and $coverageCase.status -notin @('Unavailable', 'Unsupported')) {
                throw "$FixtureName coverage case '$($coverageCase.name)' was skipped without an unavailable/unsupported status."
            }

            if ($coverageCase.status -eq 'Passed' -and $coverageCase.skipped -eq $true) {
                throw "$FixtureName coverage case '$($coverageCase.name)' was marked passed while skipped."
            }
        }

        if ($Report.coverageComplete -eq $true) {
            Write-Host "$FixtureName display checks passed with complete coverage: monitors=$($Report.monitorCount), window DPI values=$($Report.observedDpiValues.Count), monitor DPI values=$($Report.observedMonitorDpiValues.Count), negativeCoordinates=$($Report.negativeCoordinatesObserved)." -ForegroundColor Green
        }
        else {
            $unavailable = @($coverageCases | Where-Object { $_.status -in @('Unavailable', 'Unsupported') } | ForEach-Object { $_.name })
            Write-Warning "$FixtureName display execution passed with partial coverage. Unverified/unavailable: $($unavailable -join ', ')."
        }
    }

    $wpfReport = Invoke-DisplayChecksForFixture -ProjectDirectory 'TeachMeOnce.WpfFixture' -ExecutableName 'TeachMeOnce.WpfFixture.exe' -ReportName 'wpf-display.json'
    Assert-DisplayReport -Report $wpfReport -FixtureName 'WPF fixture'

    $winFormsReport = Invoke-DisplayChecksForFixture -ProjectDirectory 'TeachMeOnce.WinFormsFixture' -ExecutableName 'TeachMeOnce.WinFormsFixture.exe' -ReportName 'winforms-display.json'
    Assert-DisplayReport -Report $winFormsReport -FixtureName 'WinForms fixture'

    if ($wpfReport.coverageComplete -eq $true -and $winFormsReport.coverageComplete -eq $true) {
        Write-Host 'POC-05 display robustness checks passed with complete coverage.' -ForegroundColor Green
    }
    else {
        Write-Warning 'POC-05 execution completed, but coverage is partial. Multi-monitor, mixed-DPI, and/or negative-coordinate scenarios remain unverified; do not start POC-06.'
    }
    Write-Host "Reports: $artifactDirectory"
}
finally {
    Pop-Location
}
