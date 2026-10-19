$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$artifactDirectory = Join-Path $root 'artifacts\poc-02'

Push-Location $root
try {
    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

    dotnet build (Join-Path $root 'TeachMeOnce.sln') --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'The Release solution build failed.'
    }

    function Invoke-CorrelationForFixture {
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
                throw "Fixture '$ExecutableName' exited before correlation with code $($process.ExitCode)."
            }

            $report = Join-Path $artifactDirectory $ReportName
            if (Test-Path $report) {
                Remove-Item $report -Force
            }

            $harnessProject = Join-Path $root 'src\TeachMeOnce.InputCorrelation\TeachMeOnce.InputCorrelation.csproj'
            $harnessArguments = @(
                'run',
                '--project', $harnessProject,
                '--configuration', $configuration,
                '--no-build',
                '--',
                '--pid', $process.Id.ToString(),
                '--output', $report,
                '--scripted'
            )
            Write-Host "Running input correlation: dotnet $($harnessArguments -join ' ')"
            & dotnet @harnessArguments | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) {
                throw "Input correlation harness failed for process $($process.Id)."
            }

            if (-not (Test-Path $report)) {
                throw "Correlation report was not created: $report"
            }

            return [pscustomobject]@{
                ProcessId = $process.Id
                Report = (Get-Content -Raw $report | ConvertFrom-Json)
            }
        }
        finally {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
    }

    function Assert-CorrelationReport {
        param(
            [Parameter(Mandatory = $true)][pscustomobject]$Result,
            [Parameter(Mandatory = $true)][string]$FixtureName
        )

        $report = $Result.Report
        if ($report.eventCount -le 0) {
            throw "$FixtureName produced no correlated input events."
        }

        if ($report.droppedInputEvents -ne 0) {
            throw "$FixtureName dropped $($report.droppedInputEvents) input events."
        }

        $targetEvents = @($report.events | Where-Object { $_.targetProcessMatch -eq $true })
        if ($targetEvents.Count -eq 0) {
            throw "$FixtureName produced no events associated with target process $($Result.ProcessId)."
        }

        $wrongWindowProcessEvents = @($targetEvents | Where-Object { $_.window -eq $null -or $_.window.processId -ne $Result.ProcessId })
        if ($wrongWindowProcessEvents.Count -ne 0) {
            throw "$FixtureName produced $($wrongWindowProcessEvents.Count) target events with missing or mismatched window process identity."
        }

        $mouseEvents = @($targetEvents | Where-Object { $_.inputKind -eq 'Mouse' -and $_.targetElement -ne $null })
        if ($mouseEvents.Count -lt 3) {
            throw "$FixtureName produced fewer than three mouse events with UI Automation targets."
        }

        $keyboardEvents = @($targetEvents | Where-Object { $_.inputKind -eq 'Keyboard' -and $_.focusedElement -ne $null })
        if ($keyboardEvents.Count -lt 2) {
            throw "$FixtureName produced fewer than two keyboard events with focused UI Automation controls."
        }

        $mouseIds = @($mouseEvents | ForEach-Object { $_.targetElement.automationId })
        foreach ($requiredId in @('InvokeButton', 'ValueTextBox', 'ToggleCheckBox')) {
            if ($mouseIds -notcontains $requiredId) {
                throw "$FixtureName did not correlate a mouse event to '$requiredId'."
            }
        }

        $valueFocused = @($keyboardEvents | Where-Object { $_.focusedElement.automationId -eq 'ValueTextBox' })
        if ($valueFocused.Count -eq 0) {
            throw "$FixtureName did not correlate keyboard input to the ValueTextBox focus target."
        }

        $injectedMouseEvents = @($mouseEvents | Where-Object { $_.injected -eq $true })
        if ($injectedMouseEvents.Count -eq 0) {
            throw "$FixtureName did not mark scripted mouse input as injected."
        }

        $injectedKeyboardEvents = @($keyboardEvents | Where-Object { $_.injected -eq $true })
        if ($injectedKeyboardEvents.Count -eq 0) {
            throw "$FixtureName did not mark scripted keyboard input as injected."
        }

        $targetFailures = @($targetEvents | Where-Object { $_.failure })
        if ($targetFailures.Count -ne 0) {
            throw "$FixtureName reported $($targetFailures.Count) failures for target-process events."
        }

        Write-Host "$FixtureName correlation checks passed: $($report.eventCount) events, $($mouseEvents.Count) targeted mouse events, $($keyboardEvents.Count) focused keyboard events, $($injectedMouseEvents.Count) injected mouse events, $($injectedKeyboardEvents.Count) injected keyboard events." -ForegroundColor Green
    }

    $wpfResult = Invoke-CorrelationForFixture `
        -ProjectDirectory 'TeachMeOnce.WpfFixture' `
        -ExecutableName 'TeachMeOnce.WpfFixture.exe' `
        -ReportName 'wpf-correlation.json'
    Assert-CorrelationReport -Result $wpfResult -FixtureName 'WPF fixture'

    $winFormsResult = Invoke-CorrelationForFixture `
        -ProjectDirectory 'TeachMeOnce.WinFormsFixture' `
        -ExecutableName 'TeachMeOnce.WinFormsFixture.exe' `
        -ReportName 'winforms-correlation.json'
    Assert-CorrelationReport -Result $winFormsResult -FixtureName 'WinForms fixture'

    Write-Host 'POC-02 input correlation checks passed.' -ForegroundColor Green
    Write-Host "Reports: $artifactDirectory"
}
finally {
    Pop-Location
}
