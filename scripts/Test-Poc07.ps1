$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$artifactDirectory = Join-Path $root 'artifacts\poc-07'
$harnessProject = Join-Path $root 'src\TeachMeOnce.CustomRenderedFallback\TeachMeOnce.CustomRenderedFallback.csproj'

Push-Location $root
try {
    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

    dotnet build (Join-Path $root 'TeachMeOnce.sln') --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'The Release solution build failed.'
    }

    $existingFixtures = @(
        Get-Process -Name 'TeachMeOnce.WpfFixture', 'TeachMeOnce.WinFormsFixture' -ErrorAction SilentlyContinue |
            Select-Object Id, ProcessName, MainWindowTitle
    )
    if ($existingFixtures.Count -gt 0) {
        $details = $existingFixtures | ForEach-Object { "$($_.ProcessName) PID=$($_.Id) Window='$($_.MainWindowTitle)'" }
        throw "POC-07 requires no existing baseline or POC-06 fixture windows because they can own the foreground window. Close these exact processes manually (do not force-terminate an elevated process) and rerun: $($details -join '; ')"
    }

    function Invoke-CustomRenderedChecks {
        param(
            [Parameter(Mandatory = $true)][string]$ProjectDirectory,
            [Parameter(Mandatory = $true)][string]$ExecutableName,
            [Parameter(Mandatory = $true)][ValidateSet('WPF', 'WinForms')][string]$Framework,
            [Parameter(Mandatory = $true)][string]$ReportName
        )

        $executable = Join-Path $root "src\$ProjectDirectory\bin\$configuration\net10.0-windows\win-x64\$ExecutableName"
        if (-not (Test-Path $executable)) {
            throw "Custom-rendered fixture executable was not found: $executable"
        }

        $process = Start-Process -FilePath $executable -ArgumentList @('--exit-after-seconds', '120') -PassThru
        try {
            Start-Sleep -Milliseconds 500
            $process.Refresh()
            if ($process.HasExited) {
                throw "Custom-rendered $Framework fixture exited before POC-07 with code $($process.ExitCode)."
            }

            $reportPath = Join-Path $artifactDirectory $ReportName
            if (Test-Path $reportPath) {
                Remove-Item $reportPath -Force
            }

            $arguments = @(
                'run', '--project', $harnessProject, '--configuration', $configuration,
                '--no-build', '--', '--pid', $process.Id.ToString(), '--framework', $Framework,
                '--output', $reportPath
            )
            Write-Host "Running custom-rendered $Framework checks: dotnet $($arguments -join ' ')"
            & dotnet @arguments | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) {
                throw "Custom-rendered $Framework harness failed for process $($process.Id)."
            }

            if (-not (Test-Path $reportPath)) {
                throw "Custom-rendered report was not created: $reportPath"
            }

            return Get-Content -Raw $reportPath | ConvertFrom-Json
        }
        finally {
            try {
                $process.Refresh()
                if (-not $process.HasExited) {
                    if ($process.MainWindowHandle -ne 0) {
                        [void]$process.CloseMainWindow()
                        if (-not $process.WaitForExit(2000)) {
                            Stop-Process -Id $process.Id -Force -ErrorAction Stop
                        }
                    }
                    else {
                        Stop-Process -Id $process.Id -Force -ErrorAction Stop
                    }
                }
            }
            catch {
                Write-Warning "Could not clean up custom-rendered $Framework fixture process $($process.Id): $($_.Exception.Message). Its test-only self-exit timer remains active."
            }
        }
    }

    function Assert-CustomRenderedReport {
        param(
            [Parameter(Mandatory = $true)][pscustomobject]$Report,
            [Parameter(Mandatory = $true)][string]$Framework
        )

        if ($Report.executionPassed -ne $true -or $Report.coverageComplete -ne $true -or $Report.passed -ne $true -or $Report.status -ne 'Passed') {
            throw "$Framework POC-07 report was not a complete pass: execution=$($Report.executionPassed), coverage=$($Report.coverageComplete), status=$($Report.status), passed=$($Report.passed)."
        }

        if ($Report.semanticBaseline.semanticStatus -ne 'SemanticUnavailable' -or $Report.semanticBaseline.semanticTargetMatchCount -ne 0) {
            throw "$Framework custom target was semantically exposed; POC-07 must not pretend coordinate targeting is required."
        }

        $expectedPasses = @('valid initial coordinate click', 'valid coordinate after window move', 'valid coordinate after window resize')
        foreach ($caseName in $expectedPasses) {
            $matches = @($Report.cases | Where-Object { $_.name -eq $caseName })
            if ($matches.Count -ne 1 -or $matches[0].status -ne 'CoordinatePassed' -or $matches[0].injected -ne $true -or $matches[0].postClickVerified -ne $true) {
                throw "$Framework did not complete the guarded coordinate pass '$caseName'."
            }
        }

        $expectedRejections = @('stale coordinate after window move', 'point outside current target', 'decoy region', 'ambiguous visual candidates')
        foreach ($caseName in $expectedRejections) {
            $matches = @($Report.cases | Where-Object { $_.name -eq $caseName })
            if ($matches.Count -ne 1 -or $matches[0].status -ne 'CoordinateRejected' -or $matches[0].inputAttempted -ne $false -or $matches[0].injected -ne $false -or $matches[0].safetyOutcomeVerified -ne $true -or $matches[0].passed -ne $true) {
                throw "$Framework did not safely reject '$caseName'."
            }
        }

        Write-Host "$Framework POC-07 checks passed: semantic target unavailable as expected, 3 guarded coordinate clicks verified, 4 unsafe/ambiguous cases rejected." -ForegroundColor Green
    }

    $wpfReport = Invoke-CustomRenderedChecks -ProjectDirectory 'TeachMeOnce.WpfCustomFixture' -ExecutableName 'TeachMeOnce.WpfCustomFixture.exe' -Framework WPF -ReportName 'wpf-custom-rendered.json'
    Assert-CustomRenderedReport -Report $wpfReport -Framework WPF

    $winFormsReport = Invoke-CustomRenderedChecks -ProjectDirectory 'TeachMeOnce.WinFormsCustomFixture' -ExecutableName 'TeachMeOnce.WinFormsCustomFixture.exe' -Framework WinForms -ReportName 'winforms-custom-rendered.json'
    Assert-CustomRenderedReport -Report $winFormsReport -Framework WinForms

    Write-Host 'POC-07 custom-rendered fallback checks passed.' -ForegroundColor Green
    Write-Host "Reports: $artifactDirectory"
}
finally {
    Pop-Location
}
