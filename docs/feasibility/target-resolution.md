# POC-03: deterministic UI Automation target resolution

## Purpose

POC-03 validates a target descriptor and resolver suitable for later recording
and replay work. It does not record input, persist workflows, execute actions,
or implement replay.

## Descriptor contents

The descriptor records:

- Process ID and process name.
- Top-level window AutomationId, name, class, framework, and control type.
- Target AutomationId, name, control type, class, framework, password state,
  and supported UI Automation patterns.
- Control-view ancestor descriptors from the window to the target parent.

Runtime IDs, screen coordinates, and COM element references are deliberately
not persisted.

## Matching policy

Resolution is deterministic and non-heuristic:

1. The current window must match the recorded process and window context.
2. Candidates must match every recorded target property and required pattern.
3. Candidate ancestry must match the recorded control-view ancestry.
4. Exactly one candidate is required.
5. Zero candidates returns `NotFound`.
6. Two or more candidates returns `Ambiguous`; the resolver never guesses.

The POC binds the descriptor to the observed process ID as well as process
name. This intentionally prevents resolving an identically named control in a
different running instance; cross-process restart reacquisition is a later
policy decision, not silently enabled here.

The resolver reacquires fresh UI Automation elements from the current tree for
every call. It does not reuse the recorded element reference.

## Reproducible test

Run on Windows with an interactive desktop session:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc03.ps1
```

The script builds Release artifacts, launches both fixtures, and exercises:

- initial unique resolution,
- stale element after top-level window close,
- missing control,
- fresh reacquisition after control recreation,
- duplicate exact matches,
- deterministic restoration after duplicate removal.

The stale-element case accepts both the normal
`ElementNotAvailableException` and the observed UI Automation provider
`COMException` response `0x8000FFFF (E_UNEXPECTED)` as evidence that the
original element is no longer available. That provider-specific response is
classified as the expected stale-element result; it is not allowed to crash
the resolver process.

Reports:

```text
artifacts/poc-03/wpf-resolution.json
artifacts/poc-03/winforms-resolution.json
```

## Safety property

The duplicate case must return `Ambiguous` with at least two candidates and
must not return an element. This is the essential safety behavior for later
replay work.
