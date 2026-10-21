# TeachMeOnce

TeachMeOnce is a Windows desktop automation platform under development. The
current milestone is the **first TeachMeOnce application vertical slice**. The
isolated POC fixtures remain as regression evidence; the product shell and
workflow services are under `src/TeachMeOnce.App` and `src/TeachMeOnce.Application`.

## Prerequisites

- Windows 10 22H2 or Windows 11 x64.
- .NET SDK `10.0.402`, pinned exactly by `global.json`.
- An interactive desktop session. UI Automation does not provide a useful
  result from a headless Linux environment.

## Build

```powershell
dotnet build TeachMeOnce.sln --configuration Release
```

## Run the WPF fixture coverage scan

Build the fixture and harness, then pass the fixture executable to the harness:

```powershell
dotnet build src/TeachMeOnce.WpfFixture/TeachMeOnce.WpfFixture.csproj --configuration Debug
dotnet run --project src/TeachMeOnce.UiAutomationCoverage/TeachMeOnce.UiAutomationCoverage.csproj --configuration Debug -- `
  --launch src/TeachMeOnce.WpfFixture/bin/Debug/net10.0-windows/win-x64/TeachMeOnce.WpfFixture.exe `
  --output artifacts/wpf-coverage.json
```

The command reports the top-level target, element count, element ancestry,
automation IDs, control types, framework IDs, and supported UI Automation
control patterns. It does not click controls or change their values.

## Run the WinForms fixture coverage scan

```powershell
dotnet build src/TeachMeOnce.WinFormsFixture/TeachMeOnce.WinFormsFixture.csproj --configuration Debug
dotnet run --project src/TeachMeOnce.UiAutomationCoverage/TeachMeOnce.UiAutomationCoverage.csproj --configuration Debug -- `
  --launch src/TeachMeOnce.WinFormsFixture/bin/Debug/net10.0-windows/win-x64/TeachMeOnce.WinFormsFixture.exe `
  --output artifacts/winforms-coverage.json
```

## Inspect an existing application

Use a process ID, a partial top-level window title, or the current foreground
window. Inspecting an external application is read-only from this harness.

```powershell
dotnet run --project src/TeachMeOnce.UiAutomationCoverage/TeachMeOnce.UiAutomationCoverage.csproj -- `
  --title-contains Notepad --output artifacts/notepad-coverage.json

dotnet run --project src/TeachMeOnce.UiAutomationCoverage/TeachMeOnce.UiAutomationCoverage.csproj -- `
  --foreground --max-elements 250
```

## Verification

The exact POC acceptance criteria and result template are in
[`docs/feasibility/uia-coverage.md`](docs/feasibility/uia-coverage.md).

On Windows, run the fixture smoke test and JSON assertions with:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Test-Poc.ps1
```

The script builds the solution in Release configuration, launches each fixture,
runs the read-only coverage harness against its process ID, checks the required
WPF patterns and automation IDs, and stops the fixture processes. It writes reports below
`artifacts/poc-01/`.

The current execution environment is Linux and cannot run this Windows-only
POC. Do not treat a Linux build or static review as evidence that UI
Automation coverage works; run the commands above on a real Windows desktop
or Windows VM.

## POC-02 input correlation

After POC-01 passes, run the isolated input-correlation experiment:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc02.ps1
```

It builds Release artifacts, observes low-level mouse and keyboard hooks on a
dedicated hook thread, correlates events on an MTA UI Automation worker, and
tests both WPF and WinForms fixtures using visibly marked `SendInput` events.
It does not implement recording, workflow persistence, target resolution, or
replay. Details are in
[`docs/feasibility/input-correlation.md`](docs/feasibility/input-correlation.md).

## POC-03 deterministic target resolution

After POC-01 and POC-02 pass, run the isolated target resolver experiment:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc03.ps1
```

It builds Release artifacts and tests descriptor-based UI Automation
reacquisition against both fixtures, including stale elements, missing
controls, and duplicate exact matches. Ambiguous matches are rejected rather
than guessed. It does not implement the recorder or replay engine. Details
are in [`docs/feasibility/target-resolution.md`](docs/feasibility/target-resolution.md).

## POC-04 asynchronous waits and cancellation

After POC-01 through POC-03 pass, run the asynchronous wait experiment:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc04.ps1
```

It tests delayed dialog appearance, condition satisfaction, bounded timeout,
prompt cancellation, and missing targets for both fixtures using cancellable
`Task.Delay` polling with UI Automation error handling. It does not implement
the recorder or replay engine. Details are in
[`docs/feasibility/async-waits.md`](docs/feasibility/async-waits.md).

## POC-05 display and DPI robustness

After POC-01 through POC-04 pass, run the display robustness experiment:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc05.ps1
```

It moves and resizes both fixtures across available monitor work areas,
reacquires semantic UI Automation targets, and validates screen-coordinate
mapping for a coordinate fallback. It records observed DPI values and marks
unavailable multi-monitor, mixed-DPI, or negative-coordinate scenarios as
limitations. A run with unavailable hardware reports `status: "Partial"` and
does not claim complete coverage. Do not begin POC-06 until
`coverageComplete: true` has been demonstrated. Details are in
[`docs/feasibility/display-robustness.md`](docs/feasibility/display-robustness.md).

## POC-06 Windows privilege-boundary validation

After the POC-05 display work is verified, run the isolated privilege-boundary
experiment from a standard interactive desktop:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc06.ps1
```

It reports process integrity levels and tests inspection separately from
interaction for same-integrity and standard-to-elevated fixture processes. It
does not add an elevated helper, service, UIAccess binary, UAC bypass, or
elevated retry. Cross-integrity outcomes are explicit and unavailable hardware
or cancelled UAC prompts remain unverified. Do not begin POC-07 until the
POC-06 completion gate is met. Details are in
[`docs/feasibility/privilege-boundary.md`](docs/feasibility/privilege-boundary.md).

The complete regression sequence before POC-08 is:

```powershell
dotnet build .\TeachMeOnce.sln --configuration Release
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc02.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc03.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc04.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc05.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc06.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc07.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc08.ps1
```

POC-05 may report `Partial` when the current display hardware cannot exercise
all required layouts, but that limitation must remain visible in its JSON.

## POC-07 custom-rendered controls and guarded coordinate fallback

After POC-01 through POC-06 pass, run the isolated custom-rendered experiment:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc07.ps1
```

It uses separate WPF and WinForms owner-drawn surfaces, records what UI
Automation can and cannot identify, verifies guarded coordinate clicks after
movement and resize, and refuses stale, outside, decoy, and ambiguous actions.
It does not implement general visual discovery or pretend that generic UIA
elements semantically identify a painted target. Do not begin POC-08 until both
reports are complete passes. Details are in
[`docs/feasibility/custom-rendered-fallback.md`](docs/feasibility/custom-rendered-fallback.md).

## POC-08 clean deployment validation

POC-08 is the final exploratory feasibility checkpoint. It publishes a
representative WPF desktop probe for `win-x64`, compares framework-dependent,
self-contained, and single-file output, then runs a published smoke companion
without `dotnet run` or source-tree dependencies:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc08.ps1
```

The current deployment recommendation is a self-contained folder publish.
Single-file remains a candidate pending clean-machine and operational testing.
POC-08 explicitly reports configuration, UI Automation, writable data, logging,
and error-reporting checks. It does not claim clean-machine compatibility until
the published output is run on a separate clean Windows environment. Details
are in [`docs/feasibility/deployment.md`](docs/feasibility/deployment.md).

After POC-08 verification, stop adding exploratory POCs and use
[`docs/architecture/integrated-architecture-backlog.md`](docs/architecture/integrated-architecture-backlog.md)
to guide the integrated application implementation.

## TeachMeOnce desktop application

The product now begins at `src/TeachMeOnce.App`, with reusable application
services in `src/TeachMeOnce.Application` and UI Automation boundaries in
`src/TeachMeOnce.Automation`. The product slice also supports explicitly
selecting and attaching to a running same-integrity external window, recording
semantic Invoke, Toggle, Select, ExpandCollapse, SetValue, and non-sensitive
TextInput actions through a bounded global input session, saving a versioned
workflow, and replaying it through deterministic UI Automation resolution. The
workflow library supports safe rename, delete, import, export, and action-detail
review without displaying recorded input values. The built-in demo remains
available as a regression surface.

Run the product from an interactive Windows desktop with:

```powershell
dotnet run --project .\src\TeachMeOnce.App\TeachMeOnce.App.csproj --configuration Release
```

Run the dependency-free application contract checks with:

```powershell
dotnet run --project .\src\TeachMeOnce.Application.Tests\TeachMeOnce.Application.Tests.csproj --configuration Release
```

The external Notepad acceptance steps and explicit refusal cases are documented
in [`docs/architecture/p1-external-workflow.md`](docs/architecture/p1-external-workflow.md).
The current MVP supports same-integrity targets only; higher-integrity targets,
ambiguous windows, sensitive text fields, and coordinate fallback are refused.

Build and run it on an interactive Windows desktop:

```powershell
dotnet build .\TeachMeOnce.sln --configuration Release
dotnet run --project .\src\TeachMeOnce.App\TeachMeOnce.App.csproj --configuration Release
```

Run the persistence/schema tests:

```powershell
dotnet run --project .\src\TeachMeOnce.Application.Tests\TeachMeOnce.Application.Tests.csproj --configuration Release --no-build
```

For the P1 interactive acceptance flow, start Notepad before launching the
application, press **Refresh**, select the Notepad window, press **Attach**, and
then use **Record**. Type ordinary non-sensitive text in Notepad, press
**Stop**, close and reopen Notepad without saving, select the saved workflow,
and press **Replay**. The workflow must report completion only after the
resolved Notepad edit control reaches the recorded value. Passwords, tokens,
and configured sensitive controls are refused and never persisted.

Architecture and limitations are documented in
[`docs/architecture/first-vertical-slice.md`](docs/architecture/first-vertical-slice.md)
and the P1 acceptance steps are in
[`docs/architecture/p1-external-workflow.md`](docs/architecture/p1-external-workflow.md).
