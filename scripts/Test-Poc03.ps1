$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$artifactDirectory = Join-Path $root 'artifacts\poc-03'

Push-Location $root
try {
    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

    dotnet build (Join-Path $root 'TeachMeOnce.sln') --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'The Release solution build failed.'
    }

    function Invoke-ResolverForFixture {
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
                throw "Fixture '$ExecutableName' exited before resolution with code $($process.ExitCode)."
            }

            $report = Join-Path $artifactDirectory $ReportName
            if (Test-Path $report) {
                Remove-Item $report -Force
            }

            $harnessProject = Join-Path $root 'src\TeachMeOnce.TargetResolution\TeachMeOnce.TargetResolution.csproj'
            $harnessArguments = @(
                'run',
                '--project', $harnessProject,
                '--configuration', $configuration,
                '--no-build',
                '--',
                '--pid', $process.Id.ToString(),
                '--output', $report
            )
            Write-Host "Running target resolver: dotnet $($harnessArguments -join ' ')"
            & dotnet @harnessArguments | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) {
                throw "Target resolver failed for process $($process.Id)."
            }

            if (-not (Test-Path $report)) {
                throw "Resolver report was not created: $report"
            }

            return Get-Content -Raw $report | ConvertFrom-Json
        }
        finally {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
    }

    function Assert-ResolverReport {
        param(
            [Parameter(Mandatory = $true)][pscustomobject]$Report,
            [Parameter(Mandatory = $true)][string]$FixtureName
        )

        if ($Report.passed -ne $true) {
            throw "$FixtureName resolver report did not pass."
        }

        if ($Report.descriptor.process.processId -ne $Report.processId) {
            throw "$FixtureName descriptor process identity does not match the report process."
        }

        if ([string]::IsNullOrWhiteSpace($Report.descriptor.element.automationId)) {
            throw "$FixtureName descriptor did not contain an AutomationId."
        }

        $expected = @{
            'initial unique match' = 'Resolved'
            'missing control' = 'NotFound'
            'reacquisition after UI recreation' = 'Resolved'
            'duplicate exact matches' = 'Ambiguous'
            'deterministic restoration after duplicate removal' = 'Resolved'
            'stale element after window close' = 'NotFound'
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

        $ambiguousMatches = @($Report.cases | Where-Object { $_.name -eq 'duplicate exact matches' })
        $ambiguous = $ambiguousMatches[0]
        if ($ambiguous.candidateCount -lt 2) {
            throw "$FixtureName duplicate case did not report at least two candidates."
        }

        if ($ambiguous.returnedElement -ne $false) {
            throw "$FixtureName duplicate case returned an element instead of rejecting the ambiguity."
        }

        Write-Host "$FixtureName resolver checks passed: $($Report.cases.Count) cases; duplicate candidates=$($ambiguous.candidateCount)." -ForegroundColor Green
    }

    $wpfReport = Invoke-ResolverForFixture `
        -ProjectDirectory 'TeachMeOnce.WpfFixture' `
        -ExecutableName 'TeachMeOnce.WpfFixture.exe' `
        -ReportName 'wpf-resolution.json'
    Assert-ResolverReport -Report $wpfReport -FixtureName 'WPF fixture'

    $winFormsReport = Invoke-ResolverForFixture `
        -ProjectDirectory 'TeachMeOnce.WinFormsFixture' `
        -ExecutableName 'TeachMeOnce.WinFormsFixture.exe' `
        -ReportName 'winforms-resolution.json'
    Assert-ResolverReport -Report $winFormsReport -FixtureName 'WinForms fixture'

    Write-Host 'POC-03 target resolver checks passed.' -ForegroundColor Green
    Write-Host "Reports: $artifactDirectory"
}
finally {
    Pop-Location
}
