param(
    [switch]$SkipElevatedPrompt
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$artifactDirectory = Join-Path $root 'artifacts\poc-06'
$harnessProject = Join-Path $root 'src\TeachMeOnce.PrivilegeBoundary\TeachMeOnce.PrivilegeBoundary.csproj'

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
        throw "POC-06 requires no existing baseline fixture processes. Close these exact processes through their own windows or an authorized elevated tool; do not force-terminate an elevated process from this standard shell: $($details -join '; ')"
    }

    function New-UnavailableReport {
        param(
            [Parameter(Mandatory = $true)][string]$Scenario,
            [Parameter(Mandatory = $true)][string]$Reason,
            [Parameter(Mandatory = $true)][string]$ReportPath,
            [Parameter(Mandatory = $false)][object]$Diagnostics = $null
        )

        $report = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            scenario = $Scenario
            harnessProcess = [ordered]@{ available = $false; level = 'Unavailable'; reason = 'Harness did not run.' }
            targetProcess = [ordered]@{ available = $false; level = 'Unavailable'; reason = $Reason }
            launchDiagnostics = $Diagnostics
            observedBoundary = 'IntegrityUnavailable'
            inspection = [ordered]@{
                status = 'Unavailable'
                accessible = $false
                uiAutomationRootFound = $false
                targetFound = $false
                reason = $Reason
            }
            interaction = [ordered]@{
                status = 'NotAttempted'
                attempted = $false
                passed = $false
                reason = 'Interaction was not attempted because the elevated target was unavailable; no privilege escalation or retry was used.'
            }
            testResult = 'Unavailable'
            classificationPassed = $false
            passed = $false
        }
        $report | ConvertTo-Json -Depth 8 | Set-Content -Path $ReportPath
        return $report
    }

    function Invoke-PrivilegeCheck {
        param(
            [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
            [Parameter(Mandatory = $true)][ValidateSet('same-integrity', 'cross-integrity')][string]$Scenario,
            [Parameter(Mandatory = $true)][string]$ReportName
        )

        $reportPath = Join-Path $artifactDirectory $ReportName
        if (Test-Path $reportPath) {
            Remove-Item $reportPath -Force
        }

        $harnessArguments = @(
            'run', '--project', $harnessProject, '--configuration', $configuration,
            '--no-build', '--', '--pid', $Process.Id.ToString(), '--scenario', $Scenario,
            '--output', $reportPath
        )
        Write-Host "Running privilege $Scenario check: dotnet $($harnessArguments -join ' ')"
        & dotnet @harnessArguments | ForEach-Object { Write-Host $_ }
        if (-not (Test-Path $reportPath)) {
            throw "Privilege boundary report was not created: $reportPath"
        }

        $report = Get-Content -Raw $reportPath | ConvertFrom-Json
        if ($Scenario -eq 'same-integrity' -and $report.testResult -ne 'Passed') {
            throw "Same-integrity privilege check failed: $($report.testResult). $($report.interaction.reason)"
        }

        if ($Scenario -eq 'same-integrity') {
            if ($report.observedBoundary -ne 'SameIntegrity' -or $report.inspection.status -ne 'Passed' -or $report.interaction.status -ne 'Supported') {
                throw "Same-integrity report did not prove inspection and interaction separately: boundary=$($report.observedBoundary), inspection=$($report.inspection.status), interaction=$($report.interaction.status)."
            }
        }

        if ($Scenario -eq 'cross-integrity' -and $report.testResult -notin @('Supported', 'Unsupported')) {
            Write-Warning "Cross-integrity scenario was not established: $($report.testResult). $($report.targetProcess.reason)"
        }

        if ($Scenario -eq 'cross-integrity' -and $report.testResult -in @('Supported', 'Unsupported')) {
            if ($report.observedBoundary -ne 'TargetHigherIntegrity') {
                throw "Cross-integrity report had an invalid observed boundary: $($report.observedBoundary)."
            }
        }

        return $report
    }

    function Get-FixtureDiagnostics {
        param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$FixtureProcess)

        try {
            $FixtureProcess.Refresh()
            $diagnostics = [ordered]@{
                processId = $FixtureProcess.Id
                processName = $FixtureProcess.ProcessName
                hasExited = $FixtureProcess.HasExited
                mainWindowHandle = $FixtureProcess.MainWindowHandle.ToInt64()
                mainWindowTitle = $FixtureProcess.MainWindowTitle
            }
            if ($FixtureProcess.HasExited) {
                $diagnostics.exitCode = $FixtureProcess.ExitCode
            }
            return $diagnostics
        }
        catch {
            return [ordered]@{
                processId = $FixtureProcess.Id
                diagnosticsError = $_.Exception.Message
            }
        }
    }

    function Start-Fixture {
        param(
            [Parameter(Mandatory = $true)][string]$ProjectDirectory,
            [Parameter(Mandatory = $true)][string]$ExecutableName,
            [switch]$Elevated,
            [int]$ExitAfterSeconds = 0,
            [string]$ShutdownEventName
        )

        $executable = Join-Path $root "src\$ProjectDirectory\bin\$configuration\net10.0-windows\win-x64\$ExecutableName"
        if (-not (Test-Path $executable)) {
            throw "Fixture executable was not found: $executable"
        }

        if ($Elevated) {
            $arguments = @('--exit-after-seconds', $ExitAfterSeconds.ToString())
            if (-not [string]::IsNullOrWhiteSpace($ShutdownEventName)) {
                $arguments += @('--shutdown-event', $ShutdownEventName)
            }
            return Start-Process -FilePath $executable -ArgumentList $arguments -Verb RunAs -PassThru
        }

        return Start-Process -FilePath $executable -PassThru
    }

    function Stop-Fixture {
        param(
            [Parameter(Mandatory = $true)][System.Diagnostics.Process]$FixtureProcess,
            [Parameter(Mandatory = $true)][string]$FixtureName,
            [switch]$Elevated,
            [System.Threading.EventWaitHandle]$ShutdownEvent
        )

        $fixturePid = $FixtureProcess.Id
        if ($Elevated) {
            try {
                if ($null -ne $ShutdownEvent) {
                    [void]$ShutdownEvent.Set()
                }

                $FixtureProcess.Refresh()
                if ($FixtureProcess.HasExited) {
                    return $true
                }

                if ($FixtureProcess.MainWindowHandle -ne 0) {
                    try {
                        [void]$FixtureProcess.CloseMainWindow()
                        if ($FixtureProcess.WaitForExit(2000)) {
                            return $true
                        }
                    }
                    catch {
                        # A medium-integrity test shell may not close a high-integrity window.
                    }
                }

                $deadline = (Get-Date).AddSeconds(5)
                while ((Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 250
                    $FixtureProcess.Refresh()
                    if ($FixtureProcess.HasExited) {
                        return $true
                    }
                }

                Write-Warning "The elevated $FixtureName fixture process ${fixturePid} did not close after the shutdown signal. Forced termination was intentionally not attempted across the integrity boundary; manual cleanup is required before another UI POC."
                return $false
            }
            catch {
                Write-Warning "Could not inspect elevated $FixtureName fixture process ${fixturePid}: $($_.Exception.Message). Forced termination was intentionally not attempted; manual cleanup may be required."
                return $false
            }
        }

        try {
            $FixtureProcess.Refresh()
            if ($FixtureProcess.HasExited) {
                return $true
            }

            if ($FixtureProcess.MainWindowHandle -ne 0) {
                [void]$FixtureProcess.CloseMainWindow()
                if ($FixtureProcess.WaitForExit(2000)) {
                    return $true
                }
            }

            Stop-Process -Id $fixturePid -Force -ErrorAction Stop
            return $true
        }
        catch {
            Write-Warning "Could not stop $FixtureName fixture process ${fixturePid}: $($_.Exception.Message). The test result is retained, but manual cleanup may be required."
            return $false
        }
    }

    function Wait-Fixture {
        param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)
        Start-Sleep -Milliseconds 500
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Fixture process $($Process.Id) exited before POC-06 with code $($Process.ExitCode). Diagnostics: $((Get-FixtureDiagnostics -FixtureProcess $Process | ConvertTo-Json -Compress))."
        }
    }

    $sameReports = @()
    $cleanupResults = @()
    foreach ($fixture in @(
        @{ Directory = 'TeachMeOnce.WpfFixture'; Executable = 'TeachMeOnce.WpfFixture.exe'; Name = 'wpf' },
        @{ Directory = 'TeachMeOnce.WinFormsFixture'; Executable = 'TeachMeOnce.WinFormsFixture.exe'; Name = 'winforms' }
    )) {
        $fixtureProcess = Start-Fixture -ProjectDirectory $fixture.Directory -ExecutableName $fixture.Executable
        try {
            Wait-Fixture -Process $fixtureProcess
            $sameReports += Invoke-PrivilegeCheck -Process $fixtureProcess -Scenario 'same-integrity' -ReportName "$($fixture.Name)-same-integrity.json"
        }
        finally {
            $cleanupResults += [ordered]@{
                fixture = $fixture.Name
                elevated = $false
                passed = Stop-Fixture -FixtureProcess $fixtureProcess -FixtureName $fixture.Name
            }
        }
    }

    $crossReports = @()
    if ($SkipElevatedPrompt) {
        foreach ($fixtureName in @('wpf', 'winforms')) {
            $crossReports += New-UnavailableReport -Scenario 'cross-integrity' -Reason 'The elevated-process prompt was disabled with -SkipElevatedPrompt.' -ReportPath (Join-Path $artifactDirectory "$fixtureName-cross-integrity-unavailable.json")
        }
    }
    else {
        foreach ($fixture in @(
            @{ Directory = 'TeachMeOnce.WpfFixture'; Executable = 'TeachMeOnce.WpfFixture.exe'; Name = 'wpf' },
            @{ Directory = 'TeachMeOnce.WinFormsFixture'; Executable = 'TeachMeOnce.WinFormsFixture.exe'; Name = 'winforms' }
        )) {
            $fixtureProcess = $null
            $shutdownEvent = $null
            try {
                $eventName = "TeachMeOnce.Poc06.Shutdown.$([guid]::NewGuid().ToString('N'))"
                $shutdownEvent = New-Object -TypeName System.Threading.EventWaitHandle -ArgumentList @($false, [System.Threading.EventResetMode]::ManualReset, $eventName)
                $fixtureProcess = Start-Fixture -ProjectDirectory $fixture.Directory -ExecutableName $fixture.Executable -Elevated -ExitAfterSeconds 10 -ShutdownEventName $eventName
                Wait-Fixture -Process $fixtureProcess
                $crossReports += Invoke-PrivilegeCheck -Process $fixtureProcess -Scenario 'cross-integrity' -ReportName "$($fixture.Name)-cross-integrity.json"
            }
            catch {
                if ($null -eq $fixtureProcess) {
                    $crossReports += New-UnavailableReport -Scenario 'cross-integrity' -Reason "UAC could not start the elevated fixture: $($_.Exception.Message)" -ReportPath (Join-Path $artifactDirectory "$($fixture.Name)-cross-integrity-unavailable.json")
                }
                else {
                    $crossReports += New-UnavailableReport -Scenario 'cross-integrity' -Reason "Elevated fixture could not be prepared for testing: $($_.Exception.Message)" -ReportPath (Join-Path $artifactDirectory "$($fixture.Name)-cross-integrity-unavailable.json") -Diagnostics (Get-FixtureDiagnostics -FixtureProcess $fixtureProcess)
                }
            }
            finally {
                if ($null -ne $fixtureProcess) {
                    $cleanupResults += [ordered]@{
                        fixture = $fixture.Name
                        elevated = $true
                        passed = Stop-Fixture -FixtureProcess $fixtureProcess -FixtureName $fixture.Name -Elevated -ShutdownEvent $shutdownEvent
                    }
                }
                if ($null -ne $shutdownEvent) {
                    $shutdownEvent.Dispose()
                }
            }
        }
    }

    $allReports = @($sameReports) + @($crossReports)
    $summary = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        recommendation = 'Automate only same-integrity targets by default. Detect a higher-integrity target and stop with an explicit unsupported result; do not elevate the harness, add a helper, use UIAccess, or bypass UAC.'
        sameIntegrityPassed = @($sameReports | Where-Object { $_.testResult -eq 'Passed' }).Count -eq 2
        crossIntegrityTested = @($crossReports | Where-Object { $_.testResult -in @('Supported', 'Unsupported') }).Count -eq 2
        cleanupPassed = @($cleanupResults | Where-Object { $_.passed -ne $true }).Count -eq 0
        cleanup = $cleanupResults
        crossIntegrityResults = @($crossReports | ForEach-Object { $_.testResult })
        reports = @($allReports | ForEach-Object { [ordered]@{ scenario = $_.scenario; testResult = $_.testResult; status = if ($_.passed) { 'Passed' } else { 'Unverified' } } })
        passed = (@($sameReports | Where-Object { $_.testResult -eq 'Passed' }).Count -eq 2) -and (@($crossReports | Where-Object { $_.testResult -in @('Supported', 'Unsupported') }).Count -eq 2) -and (@($cleanupResults | Where-Object { $_.passed -ne $true }).Count -eq 0)
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $artifactDirectory 'summary.json')

    if ($summary.passed) {
        Write-Host 'POC-06 privilege boundary checks completed: same-integrity passed and both cross-integrity outcomes were explicitly classified.' -ForegroundColor Green
    }
    else {
        Write-Warning 'POC-06 execution completed with an unavailable or unverified cross-integrity scenario. Do not start POC-07.'
    }
    Write-Host "Reports: $artifactDirectory"
}
finally {
    Pop-Location
}
