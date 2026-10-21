# TeachMeOnce agent instructions

## Toolchain and platform

- Build with .NET SDK `10.0.402`; `global.json` disables SDK roll-forward, so do not upgrade or substitute the SDK.
- The solution targets `net10.0-windows` and `win-x64`; UI Automation checks require an interactive Windows desktop, not headless Linux.
- `Directory.Build.props` enables nullable reference types, implicit usings, warnings-as-errors, and `LangVersion=latest`.
- Run commands from the repository root: `C:\WORKSPACE\Coding\DEVELOPEMENT\TeachmeOnce`.

## Repository shape

- `TeachMeOnce.sln` contains isolated executable POC fixtures and harnesses; preserve their boundaries and existing behavior.
- Baseline fixtures are `src/TeachMeOnce.WpfFixture` and `src/TeachMeOnce.WinFormsFixture`.
- POC harnesses are `UiAutomationCoverage`, `InputCorrelation`, `TargetResolution`, `AsyncWaits`, `DisplayRobustness`, `PrivilegeBoundary`, and `CustomRenderedFallback`.
- Custom-rendered fixtures are separate from the baseline fixtures: `WpfCustomFixture` and `WinFormsCustomFixture`.
- Deployment validation uses `DeploymentProbe` and the published-output `DeploymentSmoke` companion.
- The product boundary is now `src/TeachMeOnce.App`; reusable workflow services/contracts are in `src/TeachMeOnce.Application`, UIA code is in `src/TeachMeOnce.Automation`, and dependency-free contract tests are in `src/TeachMeOnce.Application.Tests`.
- The first product-slice runbook is `docs/architecture/first-vertical-slice.md`; keep UI presentation in `TeachMeOnce.App` and persistence/replay contracts in libraries.
- After POC-08, use `docs/architecture/integrated-architecture-backlog.md` for the product architecture; do not add another exploratory POC unless explicitly requested.
- A product application must use library boundaries; do not reference an executable POC project as an application library or duplicate its implementation.

## Build and verification

- The canonical build is:

  ```powershell
  dotnet build .\TeachMeOnce.sln --configuration Release
  ```

- Run the POC scripts sequentially, not concurrently; each script generally rebuilds the full solution and launches interactive windows:

  ```powershell
  powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc02.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc03.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc04.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc05.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc06.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc07.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc08.ps1
  ```

- Never report a check as passed unless the command actually ran and its JSON/exit code was inspected. A Release build alone does not verify UI Automation or deployment.
- `bin/`, `obj/`, `artifacts/`, `.vs/`, and `TestResults/` are ignored. JSON reports under `artifacts/` are local evidence unless deliberately preserved elsewhere.
- If MSBuild reports `MSB4242` about a worker node being shut down, treat it as a cancellation/secondary error and rerun the build sequentially after fixing the first compiler error.

## UI Automation and safety rules

- UI Automation is the primary targeting mechanism. Target resolution must produce exactly one match; zero matches, duplicate matches, stale elements, and context mismatches are explicit failures—never guess.
- POC-03 treats `ElementNotAvailableException` and the observed stale-provider COM errors (`0x8000FFFF`/`E_UNEXPECTED`, `0x80040201`) as expected stale-element results after a window closes.
- POC-04 waits are asynchronous, cancellable, bounded, and must preserve timeout/cancellation/error result states.
- Coordinate fallback is a separate guarded path. Verify current HWND/process ownership, foreground state, visibility, current coordinate conversion, safe point containment, unique evidence, and post-action confirmation; reject unsafe or ambiguous actions without retrying elsewhere.
- POC-05 is allowed to report `Partial` when the machine cannot exercise multi-monitor, mixed-DPI, or negative-coordinate scenarios. Do not convert unavailable hardware into a pass.
- POC-06 permits same-integrity automation only for the MVP. Higher-integrity interaction is explicitly `Unsupported`; do not add an elevated helper, service, UIAccess executable, UAC bypass, or silent elevated retry.
- POC-06 elevated fixtures may prompt for UAC and must be cleaned up through the test-owned shutdown path; do not force-terminate an elevated process from a standard shell. Stale fixture windows can steal foreground ownership and invalidate later UI tests.

## Deployment rules

- POC-08 validates published output, not just `bin/Release`: it compares framework-dependent and self-contained folder/single-file modes and runs the published smoke executable directly.
- The current recommendation is self-contained `win-x64` folder publishing; single-file remains a candidate pending operational testing.
- `artifacts/poc-08/summary.json` records `cleanMachineTested=false` unless a separate clean Windows x64 environment was actually used. Never claim clean-machine compatibility from the development workstation.
- Preserve the explicit POC-05 `Partial` status and POC-06 cross-integrity `Unsupported` limitation in product documentation and reports.

## Product transition

- The next product work is the integrated application described in `docs/architecture/integrated-architecture-backlog.md`, not more feasibility harnesses.
- Keep POC fixtures and harnesses isolated while adding the real application under a new product boundary in `src/`.
- Preserve existing Git history; inspect status and recent history before edits, make small verified commits when Git metadata is available, and never reset, rewrite, force-push, or discard unrelated work.
