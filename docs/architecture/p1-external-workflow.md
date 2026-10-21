# P1 external-application workflow runbook

This runbook verifies the P1 product path without treating the POC-02
executable as an application library.

## Automated checks

Run from the repository root on Windows with SDK `10.0.402`:

```powershell
dotnet build .\TeachMeOnce.sln --configuration Release
dotnet run --project .\src\TeachMeOnce.Application.Tests\TeachMeOnce.Application.Tests.csproj --configuration Release --no-build
```

The contract executable covers workflow read/write, SetValue persistence,
schema/malformed-file isolation, cancellation/result mapping, sensitive-value
redaction, action ordering, injected/unrelated event filtering, unsafe text
rejection, and idle hook-session lifecycle. It does not claim global hook or
UIA success without an interactive desktop.

## Notepad acceptance

1. Start `notepad.exe` as the same standard user that runs TeachMeOnce.
2. Start TeachMeOnce:

   ```powershell
   dotnet run --project .\src\TeachMeOnce.App\TeachMeOnce.App.csproj --configuration Release
   ```

3. Press **Refresh** in the target selector.
4. Select the unique Notepad top-level window and press **Attach**.
5. Confirm the target information shows the process, PID, HWND, executable
   identity when available, and `Medium`/same-integrity status.
6. Press **Record**.
7. Focus the Notepad edit surface and type ordinary non-sensitive text. Do not
   type a password, token, credential, or secret.
8. Press **Stop**. Confirm a `SetValue` or `TextInput` step appears and the workflow is saved.
9. Close Notepad and discard changes if prompted. Start Notepad again without
   opening another Notepad window.
10. Select the saved workflow and press **Replay**.
11. Confirm the replay resolves exactly one current Notepad window and edit
    control, sets the text, verifies the resulting value, and reports
    `Completed`.

## Expected refusals

- A higher-integrity or unavailable-integrity target cannot be attached.
- Input from TeachMeOnce, another process, another top-level window, or
  injected replay input is ignored.
- TeachMeOnce application windows are excluded from the external target list,
  including another visible TeachMeOnce.App window.
- Password and configured sensitive controls are refused; their values do not
  enter workflow JSON or diagnostics.
- A stale, missing, duplicate, or context-mismatched target fails explicitly.
- Text-entry descriptors do not require a mutable edit-control name to remain
  unchanged while the value is being typed; stable AutomationId/class/control
  type and the exact-one resolver remain required.
- A `ValuePattern` target is persisted as `SetValue`. A read-only `TextPattern`
  target such as some Notepad editing surfaces is persisted as explicit
  `TextInput`; replay focuses the deterministically resolved element, sends
  same-integrity keyboard text, and verifies the resulting UIA text. No
  coordinate fallback is involved.
- Coordinate fallback is not used for this P1 path.

## Evidence requirements

Record the exact command exit codes and inspect the contract-test output. For
Notepad, retain the visible final replay state and the diagnostic result. Do not
upgrade POC-05 from `Partial`, POC-06 cross-integrity from `Unsupported`, or
POC-08 `cleanMachineTested=false` based on this workflow.

Current verification note: the Release solution build and POC-02 WPF/WinForms
input-correlation regression have been run successfully on Windows. POC-02 does
not need to be rerun unless hooks, correlation, or integrity-inspection code is
changed. The remaining P1 evidence is the application contract test and the
interactive Notepad workflow below.
