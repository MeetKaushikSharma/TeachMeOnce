$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$artifactDirectory = Join-Path $root 'artifacts\poc-08'
$publishRoot = Join-Path $artifactDirectory 'publish'
$appProject = Join-Path $root 'src\TeachMeOnce.DeploymentProbe\TeachMeOnce.DeploymentProbe.csproj'
$smokeProject = Join-Path $root 'src\TeachMeOnce.DeploymentSmoke\TeachMeOnce.DeploymentSmoke.csproj'

Push-Location $root
try {
    New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

    dotnet build (Join-Path $root 'TeachMeOnce.sln') --configuration $configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'The Release solution build failed.'
    }

    $modes = @(
        [ordered]@{ Name = 'fdd-folder'; SelfContained = 'false'; SingleFile = 'false'; NativeSelfExtract = 'false' },
        [ordered]@{ Name = 'fdd-single'; SelfContained = 'false'; SingleFile = 'true'; NativeSelfExtract = 'false' },
        [ordered]@{ Name = 'scd-folder'; SelfContained = 'true'; SingleFile = 'false'; NativeSelfExtract = 'false' },
        [ordered]@{ Name = 'scd-single'; SelfContained = 'true'; SingleFile = 'true'; NativeSelfExtract = 'true' }
    )

    function Publish-Project {
        param(
            [Parameter(Mandatory = $true)][string]$Project,
            [Parameter(Mandatory = $true)][string]$Output,
            [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Mode
        )

        New-Item -ItemType Directory -Force -Path $Output | Out-Null
        $publishArguments = @(
            'publish', $Project,
            '--configuration', $configuration,
            '--runtime', 'win-x64',
            '--self-contained', $Mode.SelfContained,
            '--output', $Output,
            '-p:PublishSingleFile=' + $Mode.SingleFile,
            '-p:IncludeNativeLibrariesForSelfExtract=' + $Mode.NativeSelfExtract,
            '-p:PublishTrimmed=false',
            '-p:DebugType=None'
        )
        Write-Host "Publishing $($Mode.Name): dotnet $($publishArguments -join ' ')"
        & dotnet @publishArguments | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed for $($Mode.Name): $Project"
        }
    }

    function Get-Inventory {
        param(
            [Parameter(Mandatory = $true)][string]$Directory,
            [Parameter(Mandatory = $true)][string]$ExecutableName
        )

        $files = @(Get-ChildItem -LiteralPath $Directory -File)
        $executable = Join-Path $Directory $ExecutableName
        [ordered]@{
            fileCount = $files.Count
            totalBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
            executablePresent = Test-Path $executable
            configurationPresent = Test-Path (Join-Path $Directory 'deployment.json')
            runtimeConfigPresent = Test-Path (Join-Path $Directory 'TeachMeOnce.DeploymentProbe.runtimeconfig.json')
            dependencyManifestPresent = Test-Path (Join-Path $Directory 'TeachMeOnce.DeploymentProbe.deps.json')
            files = @($files | ForEach-Object { [ordered]@{ name = $_.Name; bytes = $_.Length } })
        }
    }

    $modeResults = @()
    foreach ($mode in $modes) {
        $modeDirectory = Join-Path $publishRoot $mode.Name
        $appDirectory = Join-Path $modeDirectory 'app'
        $smokeDirectory = Join-Path $modeDirectory 'smoke'
        $appExecutable = Join-Path $appDirectory 'TeachMeOnce.DeploymentProbe.exe'
        $smokeExecutable = Join-Path $smokeDirectory 'TeachMeOnce.DeploymentSmoke.exe'
        $reportPath = Join-Path $artifactDirectory "$($mode.Name).json"

        try {
            if (Test-Path $modeDirectory) {
                Remove-Item -LiteralPath $modeDirectory -Recurse -Force
            }
            Publish-Project -Project $appProject -Output $appDirectory -Mode $mode
            Publish-Project -Project $smokeProject -Output $smokeDirectory -Mode $mode

            if (-not (Test-Path $appExecutable) -or -not (Test-Path $smokeExecutable)) {
                throw "Published executable missing for $($mode.Name)."
            }

            $smokeArguments = @('--app', $appExecutable, '--mode', $mode.Name, '--output', $reportPath)
            Write-Host "Running published smoke without dotnet: $smokeExecutable $($smokeArguments -join ' ')"
            & $smokeExecutable @smokeArguments | ForEach-Object { Write-Host $_ }
            $smokeExitCode = $LASTEXITCODE
            if (-not (Test-Path $reportPath)) {
                throw "Published smoke report was not created for $($mode.Name)."
            }

            $smokeReport = Get-Content -Raw $reportPath | ConvertFrom-Json
            $modeResults += [ordered]@{
                name = $mode.Name
                selfContained = [bool]::Parse($mode.SelfContained)
                singleFile = [bool]::Parse($mode.SingleFile)
                smokeExitCode = $smokeExitCode
                smoke = $smokeReport
                appInventory = Get-Inventory -Directory $appDirectory -ExecutableName 'TeachMeOnce.DeploymentProbe.exe'
                smokeInventory = Get-Inventory -Directory $smokeDirectory -ExecutableName 'TeachMeOnce.DeploymentSmoke.exe'
                status = if ($smokeExitCode -eq 0 -and $smokeReport.passed -eq $true) { 'Passed' } else { 'Failed' }
            }
        }
        catch {
            $reason = $_.Exception.Message
            $modeResults += [ordered]@{
                name = $mode.Name
                selfContained = [bool]::Parse($mode.SelfContained)
                singleFile = [bool]::Parse($mode.SingleFile)
                status = if ($mode.SelfContained -eq 'false') { 'Unavailable' } else { 'Failed' }
                reason = $reason
            }
            if ($mode.SelfContained -eq 'true') {
                throw
            }
            Write-Warning "Framework-dependent mode $($mode.Name) was not verified: $reason"
        }
    }

    $scdFolder = @($modeResults | Where-Object { $_.name -eq 'scd-folder' })[0]
    $scdSingle = @($modeResults | Where-Object { $_.name -eq 'scd-single' })[0]
    $fddResults = @($modeResults | Where-Object { $_.name -in @('fdd-folder', 'fdd-single') })
    $summary = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sdkRequirement = '10.0.402'
        targetFramework = 'net10.0-windows'
        runtimeIdentifier = 'win-x64'
        recommendedStrategy = 'self-contained folder publish'
        singleFileRecommendation = 'candidate; validate antivirus, extraction, diagnostics, and update behavior before adopting'
        cleanMachineTested = $false
        cleanMachineCompatibility = 'Unverified: this run used the current interactive development machine; copy the published directory to a clean Windows x64 environment for the final claim.'
        scdFolderPassed = $scdFolder.status -eq 'Passed'
        scdSinglePassed = $scdSingle.status -eq 'Passed'
        frameworkDependentResults = @($fddResults | ForEach-Object { [ordered]@{ name = $_.name; status = $_.status } })
        poc05Status = 'Partial unless artifacts/poc-05 reports prove all required display scenarios'
        poc06Limitation = 'Cross-integrity interaction is an explicit Unsupported MVP result; no elevated helper, UIAccess, or UAC bypass.'
        modes = $modeResults
        passed = ($scdFolder.status -eq 'Passed' -and $scdSingle.status -eq 'Passed')
    }
    $summary | ConvertTo-Json -Depth 12 | Set-Content -Path (Join-Path $artifactDirectory 'summary.json')

    if ($summary.passed) {
        Write-Host 'POC-08 published-output smoke checks passed for self-contained folder and single-file candidates.' -ForegroundColor Green
    }
    else {
        throw 'POC-08 recommended self-contained publish modes did not both pass.'
    }
    Write-Host "Reports: $artifactDirectory"
}
finally {
    Pop-Location
}
