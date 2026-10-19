# POC-07: custom-rendered controls and guarded coordinate fallback

## Purpose

POC-07 measures the boundary between semantic UI Automation and custom-painted
surfaces. It uses isolated WPF and WinForms fixtures that draw a target and a
visually similar decoy without exposing a custom target AutomationId, name, or
actionable UI Automation pattern.

This experiment is not a general image-recognition system, recorder, replay
engine, or unrestricted coordinate clicker. The coordinate geometry is a
fixture-only contract used to test the safety and input-routing pipeline.

## Reproducible test

From an interactive Windows desktop, with no existing `TeachMeOnce.WpfFixture`
or `TeachMeOnce.WinFormsFixture` windows left by POC-01–06, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc07.ps1
```

The script builds Release, launches both custom-rendered fixtures, and writes:

```text
artifacts/poc-07/wpf-custom-rendered.json
artifacts/poc-07/winforms-custom-rendered.json
```

If baseline fixture processes are still present, the script reports their exact
PIDs and window titles and stops before sending any input. Do not force-kill an
elevated POC-06 process; close it through its own window or an authorized
elevated Task Manager, then rerun POC-07.

Each fixture runs with a test-only self-exit timer. The harness restores the
original window bounds and performs bounded cleanup; it does not touch the
existing POC-01–06 fixtures.

## What is measured

### Semantic baseline

The harness inspects the top-level window and its UI Automation descendants.
The expected result is:

```text
semanticStatus = SemanticUnavailable
semanticTargetMatchCount = 0
```

The top-level window may still be discoverable and the rendered surface may be
reported as a generic `Custom`, `Pane`, or framework element. That does not
identify the painted target. If a stable target identity unexpectedly appears,
the report uses `UnexpectedSemanticExposure` and the smoke test fails rather
than pretending the original negative premise still holds.

### Guarded coordinate cases

The harness keeps the target geometry in client coordinates until the final
`ClientToScreen` conversion. Before any input, it verifies:

- target process and top-level HWND identity;
- visible, enabled, non-minimized window;
- verified foreground ownership;
- point inside the current client area;
- point inside a safe inset of the current target rectangle;
- `WindowFromPoint` root ownership;
- unique candidate count;
- diagnostic `AutomationElement.FromPoint` result.

The valid cases send exactly one `SendInput` click and independently verify the
painted state changed by sampling the target color. The UIA point result is
diagnostic only; a generic UIA element is not treated as semantic targeting.

The negative cases must be refused with no input:

- stale screen point after moving the window;
- point outside the target;
- documented decoy region;
- ambiguous candidate set.

No rejected case retries at another coordinate or falls back to UIA invocation.

## Report statuses

- `SemanticUnavailable`: expected negative semantic result.
- `UnexpectedSemanticExposure`: the fixture unexpectedly exposed a stable
  target identity; the experiment is not accepted as coordinate-only.
- `CoordinatePassed`: guarded input was injected and the visual state changed.
- `CoordinateRejected`: safety checks refused the action and no input was sent.
- `Failed`: an expected safe operation failed or a negative action was not
  refused.

Rejected cases report `safetyOutcomeVerified: true` and
`postClickVerified: false`; they never claim that a click occurred.

Top-level `passed: true` requires both WPF and WinForms reports to have complete
coverage, all three valid coordinate clicks to be independently verified, and
all four unsafe/ambiguous cases rejected without input.

`groundTruthSource: "fixture-contract"` is important: it proves coordinate
execution and safety validation against a known test surface. It does not prove
that arbitrary external applications can be visually discovered.

## Limitations and unsupported scenarios

- The target and decoy geometry is known to the harness; general visual target
  discovery remains unimplemented.
- The experiment samples target colors rather than using a general screenshot
  matcher. Occlusion, animations, and visual similarity beyond this fixture are
  not solved.
- UIA `FromPoint` may return no element, a generic element, or the top-level
  window. None of those results identifies the painted target.
- The harness does not click when candidate discovery is zero, ambiguous, stale,
  outside the safe target, or owned by another window.
- Secure desktops, remote sessions, display hot-plug, rotation, and
  cross-integrity coordinate fallback are unsupported. POC-06's
  same-integrity-only privilege policy remains in force.
- DPI conversion is measured for WPF using `GetDpiForWindow`; WinForms uses its
  client-pixel contract. The current display environment does not establish
  POC-05's missing multi-monitor or mixed-DPI coverage.

## Completion gate

Do not start POC-08 until:

1. the Release build passes;
2. both POC-07 reports have `executionPassed: true`, `coverageComplete: true`,
   and `passed: true`;
3. semantic unavailability is explicitly recorded for both fixtures;
4. valid clicks, movement, resize, stale-point handling, decoy rejection, and
   ambiguity rejection are all present in the reports;
5. POC-01–06 regression results are rerun and preserved, including POC-05's
   honest `Partial` status when display hardware is unavailable.
