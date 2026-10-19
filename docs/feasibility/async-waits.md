# POC-04: asynchronous UI Automation waits and cancellation

## Purpose

POC-04 validates bounded, cancellable waits around UI Automation state. It is
an isolated synchronization experiment and does not implement the recorder,
workflow model, target resolver, or replay engine.

## Wait behavior

- Probes use a bounded interval (`50ms` in the POC).
- The wait uses `Task.Delay`, not a tight loop or fixed sleep.
- Every wait has a deadline.
- Cancellation is checked before each probe and passed to `Task.Delay`.
- `ElementNotAvailableException`, COM UI Automation errors, and transient
  invalid-operation errors are recorded and treated safely during polling.
- Results distinguish `Satisfied`, `TimedOut`, `Cancelled`, `NotFound`, and
  `Error`.

## Reproducible test

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc04.ps1
```

The script builds Release artifacts, launches both fixtures, invokes the
fixture-owned delayed UI action, and checks:

- delayed element appearance,
- enabled-condition satisfaction,
- bounded timeout for a never-appearing window,
- prompt cancellation of a long wait,
- immediate missing-target handling.

Reports:

```text
artifacts/poc-04/wpf-waits.json
artifacts/poc-04/winforms-waits.json
```

## Sources

- [WaitForInputIdle](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-waitforinputidle): startup input-idle is not a general completion condition, so this POC waits for observable UI Automation conditions.
- [UI Automation events](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-eventsforclients): providers do not necessarily raise every event; bounded polling remains a required fallback.
- [UI Automation threading](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading): UI Automation calls should be isolated from the application UI thread.
