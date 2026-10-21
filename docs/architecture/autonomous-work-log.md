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

## Remaining work

1. Review the full diff and create a cohesive commit for the verified P1/P2.1
   increment.
2. Run interactive WPF/WinForms Toggle recording and replay evidence; injected
   SendInput is intentionally filtered by the recorder and is not a substitute
   for physical input acceptance.
3. Add fixture coverage for adapter execution and stale-provider retry paths,
   keeping the existing dependency-free test boundary.
4. Add interactive fixture evidence for physical Select and ExpandCollapse
   recording, then audit replay cancellation and UI recovery before starting
   another action type.
