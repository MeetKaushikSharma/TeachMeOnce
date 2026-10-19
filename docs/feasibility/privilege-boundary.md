# POC-06: Windows privilege-boundary validation

## Purpose

POC-06 measures the existing UI Automation behavior between processes at the
same Windows integrity level and between a standard harness and an elevated
fixture. It is an isolated validation harness; it does not add a privileged
component to TeachMeOnce.

The experiment deliberately does **not** add or use:

- an elevated helper executable;
- a Windows service;
- a `uiAccess=true` executable;
- an injected process;
- a UAC bypass;
- an elevated retry after an access or interaction failure.

The `-Verb RunAs` option in the smoke script is used only to start the
existing fixture at high integrity for the test. The harness remains at its
original integrity and never elevates itself.

## Reproducible test

Run from a standard, interactive Windows desktop so the harness starts at
medium integrity:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc06.ps1
```

This builds Release, then runs four cases:

1. standard harness -> standard WPF fixture;
2. standard harness -> standard WinForms fixture;
3. standard harness -> elevated WPF fixture;
4. standard harness -> elevated WinForms fixture.

Two UAC prompts are expected for the elevated cases. To explicitly record
that elevated launch was not attempted:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc06.ps1 -SkipElevatedPrompt
```

Reports are written to:

```text
artifacts/poc-06/wpf-same-integrity.json
artifacts/poc-06/winforms-same-integrity.json
artifacts/poc-06/wpf-cross-integrity.json
artifacts/poc-06/winforms-cross-integrity.json
artifacts/poc-06/summary.json
```

If UAC is unavailable or cancelled, the cross-integrity report is explicitly
written as `Unavailable`; it is not treated as a pass.

The elevated fixture is started with a test-only 10-second self-exit timer and
an explicit named shutdown event.
The standard test shell never force-terminates that process. It may request a
signals the event immediately after the check. If the event is unavailable, the
fixture exits on its own after the short timer. This is
intentional: a medium-integrity shell should not attempt to obtain terminate
access to a high-integrity process. POC-06 waits through the self-exit grace
period before completing, so a successful POC-06 run should not leave an
elevated fixture window behind.

The script does not use `WaitForInputIdle` as the authoritative readiness
check. That API can report “no graphical interface” for a live GUI process or
for a process whose startup state is changing. The privilege harness performs
the bounded UI Automation window discovery instead. If startup fails, the
unavailable report records the exact fixture PID and available process/window
diagnostics.

## Separate inspection and interaction checks

Each report records:

- harness and target process IDs and names;
- integrity RID and name (`Medium`, `High`, and so on);
- token elevation state and elevation type;
- observed boundary (`SameIntegrity`, `TargetHigherIntegrity`, or another
  explicit state);
- **inspection** status, including UIA root accessibility and unique semantic
  target counts;
- **interaction** status, including whether `InvokePattern` was attempted and
  whether the fixture status changed;
- detailed diagnostics for access denial, unavailable elements, setup
  mismatch, and non-privilege failures.

Cross-integrity interaction has an explicit result:

- `Supported`: Windows permitted the UIA interaction in this exact setup;
- `Unsupported`: inspection or interaction was blocked, or the element became
  unavailable in a way classified as an access boundary;
- `Unavailable`: the elevated target could not be started;
- `Failed`: an unexpected failure occurred and requires investigation.

The harness never converts `Unsupported` to `Supported` by retrying elevated,
using coordinates, or guessing another target.

When the harness detects `TargetHigherIntegrity`, it records inspection as a
separate read-only result and sets interaction to `Unsupported` with
`attempted: false`. This is the expected product-policy result even if Windows
allows the client to inspect some UIA properties. The harness does not invoke
the target merely to discover whether the boundary blocks it.

For clarity, an `Unsupported` cross-integrity result has
`classificationPassed: true` because the boundary behavior was classified,
but `passed: false` because interaction support was not established. This is
intentional and prevents an unsupported interaction from appearing to be a
functional pass.

## Product recommendation

TeachMeOnce should default to a **same-integrity-only privilege policy**:

1. Record the harness integrity and target integrity before inspection.
2. Permit semantic inspection and interaction only when the integrity policy
   allows it; same-integrity is the baseline supported case.
3. If the target has higher integrity, stop before interaction and report a
   clear unsupported privilege-boundary result.
4. Do not silently elevate, prompt for an untracked helper, use UIAccess, or
   bypass UAC. A future explicitly designed privileged architecture would be a
   separate security review, not an implicit fallback.

An observed `Supported` cross-integrity result is evidence about that Windows
configuration, not a recommendation to make cross-integrity automation a
product guarantee.

## Completion gate

POC-06 is verified only when:

- both same-integrity fixture cases pass;
- both cross-integrity fixture cases are either `Supported` or explicitly
  `Unsupported` with integrity and UIA diagnostics;
- no report is `Unavailable`, `SetupMismatch`, or `Failed`;
- POC-01 through POC-05 regression tests pass;
- POC-05 may remain `Partial` when its display hardware is unavailable, but
  its reports must retain that limitation explicitly.

Do not begin POC-07 until this gate has been met.

## Interpreting the previous cleanup failure

The earlier run identified these exact fixture process IDs in its unavailable
reports:

- WPF fixture: PID `32148`;
- WinForms fixture: PID `35796`.

Both reports failed before the privilege harness ran because the script used
`WaitForInputIdle`, which returned “This could be because the process does not
have a graphical interface.” That message is not proof of a UIA access denial
and did not identify a UAC dialog. The subsequent `Access Denied` cleanup was
the standard shell attempting to force-stop an elevated fixture. The revised
script records process/window diagnostics, does not force-stop elevated
fixtures, and uses the fixture-owned self-exit timer. A UAC consent dialog is
owned by Windows on the secure desktop and is not inspected or automated by
this POC; approve or cancel it manually.
