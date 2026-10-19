# POC-08: clean deployment validation

## Scope

POC-08 validates the published output of a representative WPF desktop
application. It does not implement the TeachMeOnce product application,
installer, signing, updater, or rollback system.

The representative application is:

```text
src/TeachMeOnce.DeploymentProbe/
```

The published smoke companion is:

```text
src/TeachMeOnce.DeploymentSmoke/
```

The smoke companion is itself published and launched as an `.exe`; the smoke
run does not use `dotnet run`, a source project, or a development `bin` output
to exercise the application.

## Deployment strategy decision

The initial product candidate is a **self-contained, folder-based `win-x64`

```text
SelfContained=true
PublishSingleFile=false
RuntimeIdentifier=win-x64
TargetFramework=net10.0-windows
```

Reasons:

- customers do not need a separately installed .NET 10 Windows Desktop Runtime;
- WPF and UI Automation dependencies remain visible for diagnosis;
- native files and configuration are easier to inspect, patch, sign, and
  support than an extracted single-file bundle;
- trimming remains disabled because WPF, UI Automation, reflection, and native
  interop are outside this POC's trimming compatibility scope.

POC-08 also tests:

- framework-dependent folder publish;
- framework-dependent single-file publish;
- self-contained single-file publish with native-library self-extraction.

Single-file is a candidate only. Before adopting it, test antivirus/application
control, extraction locations, startup time, crash diagnostics, updates, and
rollback. The POC does not claim those behaviors are verified.

## Reproducible test

On Windows x64 with an interactive desktop:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc08.ps1
```

The script:

1. builds the complete Release solution with the pinned SDK;
2. publishes four output modes into `artifacts/poc-08/publish/`;
3. checks executable, configuration, runtime, and dependency files;
4. runs the **published** smoke companion directly;
5. launches the published application;
6. verifies UI Automation window and action targets;
7. invokes the action through `InvokePattern` and checks the status change;
8. verifies configuration loading from the published directory;
9. verifies writable `%LOCALAPPDATA%` data and log paths;
10. triggers and verifies a sanitized error report;
11. writes one report per mode plus `summary.json`.

Reports:

```text
artifacts/poc-08/fdd-folder.json
artifacts/poc-08/fdd-single.json
artifacts/poc-08/scd-folder.json
artifacts/poc-08/scd-single.json
artifacts/poc-08/summary.json
```

The recommended self-contained folder and single-file modes must pass for the
script to succeed. Framework-dependent modes are reported as `Unavailable` if
the required runtime is absent; that is not silently treated as a pass.

## Runtime and installation instructions

### Self-contained folder candidate

Copy the complete `scd-folder` application directory to a Windows x64 machine
and launch:

```powershell
.\TeachMeOnce.DeploymentProbe.exe
```

Do not copy only the `.exe`; the folder contains the runtime and native desktop
dependencies required by the self-contained deployment.

### Self-contained single-file candidate

Copy the published `scd-single` directory and launch the published executable.
The `deployment.json` configuration file remains beside the executable in this
POC, so it must be copied with the executable. Native-library extraction and
application-control behavior remain limitations pending clean-machine tests.

### Framework-dependent comparison

The `fdd-folder` and `fdd-single` modes require the matching x64 .NET 10
Windows Desktop Runtime. The SDK is a build prerequisite, not an end-user
runtime installation instruction. The exact runtime patch policy is not yet
defined by this POC.

## Verified behavior and limitations

The smoke report verifies:

- published executable launch;
- UI Automation dependency and process identity;
- semantic `AutomationId` lookup for the probe action and status;
- basic `InvokePattern` functionality;
- published configuration presence and loading;
- writable per-user data under `%LOCALAPPDATA%`;
- appendable application logging;
- sanitized error-report creation.

The smoke report explicitly records `cleanMachineTested: false` for this local
run. Running `dotnet publish` successfully is not evidence of clean-machine
compatibility. To make that claim, copy each required publish mode to a clean
Windows x64 VM with no SDK, source tree, or development tools and rerun the
published smoke executable from the copied directory.

Known limitations:

- no MSI/MSIX/installer or uninstall flow;
- no code signing or trust-chain validation;
- no update or rollback test;
- no antivirus or application-control test;
- no clean-machine result is claimed until a separate Windows environment is
  used;
- `win-x64` only;
- POC-05 remains `Partial` when multi-monitor, mixed-DPI, or negative-coordinate
  hardware is unavailable;
- POC-06 cross-integrity interaction remains an explicit unsupported MVP
  limitation; the product must not silently elevate or bypass UAC.
