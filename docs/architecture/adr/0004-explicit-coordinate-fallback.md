# ADR-0004: Treat coordinate fallback as a guarded explicit action

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

Custom-rendered surfaces may not expose usable UIA patterns. POC-07 showed
that coordinate input can be demonstrated safely only with current window
ownership, foreground, geometry, containment, uniqueness, and confirmation
checks. It is not generic visual discovery.

## Decision

Coordinates are stored and executed only as a distinct `CoordinateFallback`
action. The action carries coordinate-space, DPI/display, geometry, evidence,
and postcondition data. The executor performs a fresh preflight and rejects
unknown, stale, unsafe, or ambiguous evidence without trying another point.

## Consequences

- Semantic actions do not silently degrade into clicks.
- Workflows contain explicit risk and can show a clear review warning.
- Multi-monitor and mixed-DPI support requires a real hardware matrix; POC-05
  remains `Partial` until then.
- Some custom controls remain unsupported when safe evidence is unavailable.

## Rejected alternatives

- Automatic fallback after a semantic miss: hides target identity failure.
- Blind replay of recorded screen coordinates: unsafe after layout/DPI changes.
- Generic screenshot matching in the MVP: not proven by the POC and adds a new
  discovery subsystem with different privacy and false-positive risks.
