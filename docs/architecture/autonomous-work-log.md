# Autonomous work log

## Current state

TeachMeOnce has a product boundary under `src/TeachMeOnce.App`,
`src/TeachMeOnce.Application`, and `src/TeachMeOnce.Automation`. The POC
fixtures remain isolated. P1 external recording is implemented and can produce
a persisted `TextInput` action for the current Windows Notepad document
surface. The P1 replay gate is verified by the saved-workflow UIA probe with
one visible Notepad window; physical-input recording remains manual evidence.

## Changes in this session

- Investigated the recorded Notepad workflow and replay timeout using the
  actual safe workflow descriptor shape and application diagnostics.
- Made external window matching ignore only absent optional metadata while
  retaining exact-one resolution and required control type matching.
- Made process-name comparisons case-insensitive while retaining executable
  identity validation.
- Propagated the external `matchWindowName=false` policy to the top-level
  ancestor stored in element descriptors, preventing a mutable Notepad title
  from invalidating the element path.
- Added visible-window and native HWND/process ownership checks to descriptor
  waits, plus privacy-safe mismatch counts in timeout reasons.
- Changed replay and text/verification waits to reacquire the current top-level
  window through the same descriptor policy instead of retaining one stale UIA
  root.
- Added a registry-backed adapter boundary for `Invoke`, `SetValue`, and
  `TextInput`; adapters declare capability requirements and map unsupported
  patterns separately from execution failures.
- Fixed semantic text replay to foreground and process-validate the resolved
  native window before sending input, and to prefer a writable `ValuePattern`
  when a `TextInput` target exposes both text patterns.
- Added the first additional UIA action: `Toggle`. Recording conservatively
  accepts one toggle candidate per session, reacquires it after hook drain,
  stores the observed canonical `On`/`Off`/`Indeterminate` state, and refuses
  ambiguous multi-toggle recordings. Replay checks enabled state, executes one
  `TogglePattern.Toggle()`, and verifies the exact expected state without
  retrying the toggle operation.
- Added deterministic `Select` support for one selection candidate per recording
  session. Replay uses `SelectionItemPattern.Select()` and verifies
  `IsSelected` after reacquiring the same target.
- Added deterministic `ExpandCollapse` support for one candidate per recording
  session. Recording persists the observed `Expanded` or `Collapsed` state;
  replay is idempotent when already at the desired state, otherwise performs
  one explicit expand/collapse operation and verifies the resulting state.
- Improved the workflow library panel with safe action details: action type,
  target process/control, required UIA patterns, expected state, schema, and
  sensitive-target warnings are reviewable without displaying recorded input
  values.
- Deduplicated top-level UIA candidates by native HWND during enumeration and
  descriptor waits so provider wrappers for one actual window cannot create a
  false ambiguity.
- Enforced adapter capability declarations against persisted target descriptor
  metadata before replay waits or execution. Malformed actions can no longer
  omit their required UIA pattern and rely on a later provider-side failure.
- Bounded the visible WPF diagnostic list to 500 entries and changed cross-thread
  diagnostic presentation to guarded asynchronous dispatch, preventing a
  long-running session or shutdown race from blocking the producer.
- Added safe workflow deletion: the library asks for confirmation, deletes only
  the selected workflow ID, reports missing files explicitly, and never touches
  the attached target application.
- Hardened atomic workflow saves so cancellation or serialization failure removes
  the temporary file instead of leaving stale `.tmp` artifacts; the contract
  test now exercises cancellation cleanup.
- Centralized the version-1 action vocabulary and made persistence reject
  unknown action types before writing them. Replay still retains registry-level
  rejection for workflows supplied through alternate stores.
- Added validated workflow export/import with atomic temporary-file cleanup and
  a fresh library identity on import, plus confirmation-backed deletion in the
  WPF workflow library.
- Added in-app workflow rename using the existing workflow identity and atomic
  save path; the library now supports rename, delete, import, and export without
  exposing recorded input values in its review panel.
- Added session-visible last-result metadata to workflow details, including
  status, completed action count, and the redacted execution message. Deleting
  a workflow clears its session result state.
- Added `TeachMeOnce.Application.IntegrationTests` and
  `scripts/Test-ApplicationAdapters.ps1`. The interactive integration test
  launches the real WPF and WinForms fixtures and verified the product executor
  end to end for Invoke, SetValue, Toggle, and Select across both frameworks,
  plus ExpandCollapse on WPF. The first WinForms attempt exposed a fixture
  naming difference (`List item two` versus the WPF AutomationId); the test was
  corrected to use the observed stable WinForms name.
- Added an always-visible operating-policy summary to the product shell so
  users can see the same-integrity, UIA-first, sensitive-value, coordinate, and
  storage boundaries without consulting JSON or architecture documents.
- Added stable diagnostic entry identifiers to in-memory entries, persisted
  logs, and the visible WPF diagnostic list; the contract test verifies IDs are
  present while redaction behavior remains unchanged.
- Added best-effort diagnostic file retention: logs trim to the newest roughly
  2 MB after crossing a 4 MB limit, while retention failures do not break the
  application diagnostic path and temporary trim files are cleaned up.
- Added startup cleanup for orphaned `*.json.tmp` workflow files left by a
  terminated or cancelled save; valid workflows continue loading and the
  orphan-cleanup path is covered by the contract test.
- Strengthened workflow persistence validation for missing process/window/element
  descriptor fields and missing pattern/ancestor metadata; invalid descriptors
  now fail before they can reach UIA replay.
- Hardened descriptor-window waits against recoverable COM/UIA enumeration
  failures and now retain an error count in the bounded wait result instead of
  allowing a transient provider exception to escape as an unclassified replay
  failure.
- Updated the Notepad runbook to accept either `SetValue` or `TextInput`.

## Verification actually run

From the repository root:

```text
dotnet build .\TeachMeOnce.sln --configuration Release
```

Passed with zero warnings and zero errors.

```text
dotnet run --project .\src\TeachMeOnce.Application.Tests\TeachMeOnce.Application.Tests.csproj --configuration Release --no-build
```

Passed: `TeachMeOnce.Application.Tests passed.`

The Release product executable was also launched from
`src/TeachMeOnce.App/bin/Release/net10.0-windows/win-x64/TeachMeOnce.App.exe`,
reached the `TeachMeOnce` main window, and closed with exit code `0`. This
verified the updated workflow-library XAML and startup/load path, but not a new
physical-input acceptance run.

After adding the import/export controls, the same Release product startup
smoke again reached the `TeachMeOnce` main window and exited with code `0`.

An automated UIA replay probe was first refused as
`Ambiguous: Multiple top-level windows matched process 'Notepad' and its window
context`; this was a correct safety refusal. After closing the extra visible
Notepad windows through their normal close message and starting one fresh
window, the saved workflow replay probe completed and verified `hello world`.
The probe therefore verifies the persisted TextInput workflow, deterministic
window reacquisition, direct ValuePattern execution, and post-action text
verification. It does not replace the manual physical-input recording step.

POC-02 was not rerun because its previously successful evidence remains valid
for the unchanged hook/correlation behavior; the current edits affect replay
and target waiting rather than the capture implementation.

Because target resolution and native-window candidate handling were part of the
current reliability work, the focused resolver regression was also run:

```text
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc03.ps1
```

Passed for both WPF and WinForms fixtures: six resolver cases each, including
reacquisition, duplicate exact-match rejection, deterministic restoration, and
stale-window handling. Each duplicate case reported two candidates and no
returned element.

The bounded-wait regression was also run because descriptor waiting changed:

```text
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc04.ps1
```

Passed for both WPF and WinForms fixtures: five cases each covering delayed
appearance, condition satisfaction, bounded timeout, prompt cancellation, and
missing-target handling.

Additional fixture capability inspection confirmed:

- WPF `ToggleCheckBox` exposes `TogglePattern` and starts `Off`.
- WPF `ListItemTwo` exposes `SelectionItemPattern` and starts unselected.
- WPF `ExpandableSection` exposes `ExpandCollapsePattern` and starts
  `Collapsed`.
- WinForms `ToggleCheckBox` exposes `TogglePattern`.

These are capability checks only; physical-input recording and full adapter
replay against those fixtures remain interactive acceptance work.

## Remaining work

1. Run interactive WPF/WinForms Toggle recording and replay evidence; injected
   SendInput is intentionally filtered by the recorder and is not a substitute
   for physical input acceptance.
2. Add fixture coverage for adapter execution and stale-provider retry paths,
   keeping the existing dependency-free test boundary.
3. Add interactive fixture evidence for physical Select and ExpandCollapse
   recording, then audit replay cancellation and UI recovery before starting
   another action type.
