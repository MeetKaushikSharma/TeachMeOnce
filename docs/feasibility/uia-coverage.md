# POC-01: UI Automation coverage matrix

## Purpose

Determine whether Microsoft UI Automation exposes enough semantic information
and control patterns for the proposed MVP before implementing the recorder,
target resolver, or replay engine.

The harness is intentionally read-only. It launches a deterministic WPF or
WinForms fixture, walks the target's UI Automation subtree, and
records properties and supported control patterns. It can also inspect an
existing top-level window by process ID, title substring, or foreground state.
Pattern reporting uses the provider's `GetSupportedPatterns` result rather
than assuming every native pattern has a managed wrapper; the native
LegacyIAccessible pattern is normalized from its documented ID `10018`.

## Acceptance criteria

The POC passes only when all of the following are true on a supported Windows
machine:

1. The WPF fixture is discovered after launch and produces a non-empty report.
2. The WinForms fixture is discovered after launch and produces a non-empty
   report.
3. The WPF report contains at least one element with each expected pattern:
   `Invoke`, `Value`, `Toggle`, `Selection` or `SelectionItem`,
   `ExpandCollapse`, and `Scroll`.
4. The report includes a stable automation ID for the fixture controls that
   define one, plus control type, framework ID, enabled state, and ancestry.
5. The WinForms report identifies the expected button, editable control,
   password control, checkbox, combo box, and list box.
6. A report can be generated for a separately launched application by process
   ID or partial window title.
7. The harness does not send input or mutate a target while inspecting it.
8. A missing target exits non-zero and explains the failure.

The POC does **not** claim that every application will satisfy these criteria.
It measures the representative fixture surface and records unsupported or
missing behavior for later decisions.

## Commands

See the exact PowerShell commands in the repository README. The main output is
JSON so later experiments can compare reports without screen-scraping console
text.

Useful options:

| Option | Meaning |
| --- | --- |
| `--launch <path>` | Launch an executable and find its first top-level UIA window. |
| `--pid <id>` | Inspect a top-level window owned by a process. |
| `--title-contains <text>` | Inspect a top-level window whose UIA name contains text. |
| `--foreground` | Use the current foreground window if no other target resolves. |
| `--output <path>` | Write indented JSON instead of only printing it. |
| `--timeout-seconds <n>` | Window discovery deadline; default is 10 seconds. |
| `--max-elements <n>` | Bound traversal work; default is 500. |

## Expected fixture controls

The WPF fixture intentionally includes:

- Invoke button.
- Editable text value.
- Password field.
- Toggle checkbox.
- Selection combo box and list box.
- Expandable section.
- Scroll viewer.
- Delayed modal dialog for later synchronization experiments.

The WinForms fixture includes equivalent button, value, password, toggle,
selection, and delayed-dialog controls. It does not attempt to make WinForms
look identical to WPF; framework differences are part of the measurement.

## Result worksheet

Copy this table into the experiment notes after running the POC:

| Target | OS/build | DPI/monitors | Elements | Required patterns | Missing patterns | Reacquisition tested | Result |
| --- | --- | --- | ---: | --- | --- | --- | --- |
| WPF fixture |  |  |  |  |  | Not in this POC |  |
| WinForms fixture |  |  |  |  |  | Not in this POC |  |
| External application |  |  |  |  |  | Not in this POC |  |

## Interpretation rules

- A supported pattern is evidence that a provider exposes that capability for
  the inspected element; it is not proof that every state transition is
  reliable.
- Missing UI Automation events or properties must not be treated as proof that
  the application has no corresponding state. Microsoft explicitly notes that
  providers do not necessarily raise every possible event.
- A visible name is not a durable selector by itself.
- Runtime element references must not be persisted. The eventual product must
  reacquire fresh elements during replay.
- A fixture pass does not establish support for custom-rendered, remote, game,
  elevated, UAC, or secure-desktop interfaces.

## Research sources

These sources support the POC design:

- [UI Automation for Win32 apps](https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiauto-win32): UI Automation provides programmatic access to most desktop UI elements and is designed for automated testing.
- [UI Automation tree overview](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-treeoverview): the tree is dynamic, has raw/control/content views, and is built as needed.
- [Control patterns overview](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-controlpatternsoverview): patterns expose independent capabilities such as Invoke, Value, Selection, Toggle, Scroll, and Window.
- [UI Automation events](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-eventsforclients): events improve efficiency, but providers do not necessarily raise every possible event.
- [UI Automation threading](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading): clients interacting with their own UI should make desktop UI Automation calls on a separate MTA thread.
