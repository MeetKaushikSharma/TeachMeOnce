# POC-05: DPI, resolution, monitor, and coordinate robustness

## Purpose

POC-05 measures whether semantic UI Automation targeting remains stable as a
fixture window is moved and resized across the available Windows display
configuration. It also validates that a coordinate fallback uses screen-space
coordinates by mapping a UI Automation clickable point back to the expected
control.

It does not implement replay, image matching, coordinate persistence, or
automatic display reconfiguration.

## Checks

For WPF and WinForms fixtures, the harness:

- captures the native window and current DPI,
- enumerates monitor work areas,
- moves the window to each available monitor,
- resizes the window within each work area,
- reacquires `ValueTextBox` semantically,
- obtains `InvokeButton`'s screen-space clickable point,
- maps that point back through `AutomationElement.FromPoint`,
- verifies the mapped control is still `InvokeButton`,
- records negative coordinates, window DPI values, and effective monitor DPI
  values.

## Reproducible test

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Poc05.ps1
```

Reports:

```text
artifacts/poc-05/wpf-display.json
artifacts/poc-05/winforms-display.json
```

The JSON distinguishes execution from environmental coverage:

- `executionPassed: true` means the checks that were available completed
  successfully.
- `coverageComplete: true` requires multi-monitor movement, mixed effective
  monitor DPI, and a negative screen coordinate to have all been exercised.
- `status: "Partial"` means the run is useful evidence but does not verify the
  unavailable scenarios. A skipped coverage case must have `status` set to
  `Unavailable` or `Unsupported`; it is never a pass.
- `passed: true` is reserved for a complete run with no failed or unavailable
  required coverage case.
- `monitors` records each display's physical bounds, work area, device name,
  and effective monitor DPI so resolution and negative-coordinate evidence can
  be reviewed without guessing from the movement cases.

The normal one-monitor result is therefore expected to have
`executionPassed: true`, `coverageComplete: false`, `status: "Partial"`, and
unavailable cases for multiple monitors, mixed DPI, and negative coordinates.

## Manual display test matrix

The automated harness measures the current Windows display configuration; it
does not change Windows settings. Use this matrix on a disposable Windows
desktop or VM. Before each row, close other test fixture instances and record
the Windows **Settings > System > Display** arrangement and scaling values.
Run the automated script for both fixtures after each configuration and retain
the two JSON reports.

| ID | Display layout and scaling | Window actions | Expected evidence |
| --- | --- | --- | --- |
| M1 | One monitor at 100% | Run the script; observe initial, move-to-monitor, and resize cases | Semantic and coordinate cases pass; multi-monitor, mixed-DPI, and negative-coordinate cases are `Unavailable` |
| M2 | One monitor at 125% | Repeat M1 | Window and monitor DPI report approximately 120; semantic and coordinate cases still pass |
| M3 | One monitor at 150% | Repeat M1 | Window and monitor DPI report approximately 144; semantic and coordinate cases still pass |
| M4 | Two monitors, both 100%, secondary to the right | Run both fixtures; move the window across each monitor and resize on each | One successful `monitor` case per display; `multiMonitorTested=true`; mixed DPI remains unavailable; negative coordinates remain unavailable |
| M5 | Two monitors, primary on the right, secondary to the left; use 100% on both | Repeat the movement and resize sequence | At least one successful coordinate point has `coordinateX < 0`; negative-coordinate case passes |
| M6 | Two monitors at different scaling levels, e.g. 100% and 150%, secondary to the right | Move the window from the 100% monitor to the 150% monitor and resize on both | `observedMonitorDpiValues` contains at least two values; `mixedDpiObserved=true`; semantic target and coordinate fallback pass on both |
| M7 | Two monitors at different scaling levels, secondary above or below primary | Repeat movement and resize | Monitor cases identify both displays; window bounds remain valid; any negative Y coordinate is recorded if produced |
| M8 | Two monitors with a 1920x1080 and a 2560x1440 (or 4K) mode | Repeat movement; change Windows resolution one display at a time and rerun | Reports retain successful semantic reacquisition and coordinate mapping; window dimensions change for resize cases |
| M9 | Any supported layout with taskbar/work-area reservations | Keep the taskbar visible; run movement to every display edge | Window placement remains within the reported work area and coordinate fallback maps to `InvokeButton` |

For M5–M7, verify the physical arrangement in Display Settings rather than
assuming monitor numbering. A monitor can have negative X or Y coordinates
depending on its arrangement. If the hardware or VM cannot provide a row,
leave that row explicitly marked **unavailable** in the test record; do not
convert it into a pass.

### Manual result checklist

For each fixture and matrix row, record:

1. Windows version, display resolution, scaling percentage, monitor position,
   and whether the session is local or remote.
2. `monitorCount`, `observedDpiValues`, and `observedMonitorDpiValues`.
3. Every `cases[].name`, `cases[].location`, `cases[].status`, and `reason`.
4. Whether `coverageComplete` is true. Do not start POC-06 unless it is true
   for the required display matrix or the project explicitly accepts the
   remaining limitations.

## Limitations

- The POC cannot change Windows display scaling settings automatically; it
  measures the current environment.
- A one-monitor machine cannot prove multi-monitor movement and the report
  marks that scenario as skipped.
- A single observed DPI value cannot prove mixed-DPI behavior; the report
  records that limitation with an `Unavailable` case status rather than
  treating it as tested.
- The coordinate check validates screen-coordinate mapping but does not click
  the control.
- Remote desktop, display hot-plug, rotation, and secure-desktop transitions
  are outside this experiment.
