# TeachMeOnce first application vertical slice

## Boundaries

- `TeachMeOnce.Automation` is the reusable UI Automation boundary. It owns the
  deterministic target descriptor/resolver and cancellable UIA wait code that
  originated in POC-03 and POC-04. The executable POC harnesses remain isolated
  consumers; the product does not reference an executable POC project.
- `TeachMeOnce.Application` owns versioned workflow contracts, JSON persistence,
  diagnostics, semantic recording, and replay orchestration.
- `TeachMeOnce.App` is the WPF desktop shell. It owns presentation state and
  connects the application services to the visible workflow list, controls, demo
  target, execution state, and diagnostic log.
- `TeachMeOnce.Application.Tests` is a dependency-free executable test project
  for persistence/schema behavior that can run without an interactive desktop.

## Current MVP flow

1. Start the WPF app.
2. Press **Record**.
3. Click **Semantic demo button**.
4. Press **Stop**. The app captures exactly one `InvokePattern` action with a
   deterministic UI Automation descriptor and saves a version-1 workflow below
   `%LOCALAPPDATA%\TeachMeOnce\Workflows`.
5. Select the saved workflow and press **Replay**.
6. The executor waits for the app window, resolves exactly one target, invokes
   it, and waits for `ReplayDemoStatus` to reach `Status: invoked`.

The self-targeting slice records `ProcessName` identity policy in its semantic
descriptor and replay reacquires the current TeachMeOnce window by process and
window context. This allows the workflow to survive the documented
close/relaunch boundary; external targets additionally retain executable and
integrity identity and are revalidated before action.

Close and relaunch the app between steps 4 and 5 to verify persistence across
process restarts. Multiple valid workflow files are loaded together. Malformed
or unsupported-schema files are skipped individually and appear as warnings in
the diagnostic log; they do not hide valid workflows.

Ambiguous, missing, context-mismatched, cancelled, timed-out, and unsupported
actions are returned as explicit execution results and logged. The current
vertical slice targets its own same-integrity demo window; it does not silently
cross an integrity boundary or use coordinate fallback.

## Run

Build the full solution:

```powershell
dotnet build .\TeachMeOnce.sln --configuration Release
```

Run the application on an interactive Windows desktop:

```powershell
dotnet run --project .\src\TeachMeOnce.App\TeachMeOnce.App.csproj --configuration Release
```

Run the non-UI application contract tests:

```powershell
dotnet run --project .\src\TeachMeOnce.Application.Tests\TeachMeOnce.Application.Tests.csproj --configuration Release --no-build
```

The contract tests cover restart-style reload, multiple workflows, malformed
JSON, unsupported schema versions, pre-cancelled replay, and explicit missing
versus ambiguous target status mapping. The actual WPF replay click requires
the interactive run above.

The app log is written to:

```text
%LOCALAPPDATA%\TeachMeOnce\teachmeonce.log
```

Workflow JSON files are written to:

```text
%LOCALAPPDATA%\TeachMeOnce\Workflows\<workflow-id>.json
```

The P1 external slice adds an explicit target list/Attach flow and a real
same-integrity input session for Invoke and SetValue actions. It still requires
an interactive acceptance run against Notepad and dedicated fixture regression
evidence. Custom-rendered visual discovery, multi-monitor completion, elevated
automation, installers, and updates remain outside this slice.
