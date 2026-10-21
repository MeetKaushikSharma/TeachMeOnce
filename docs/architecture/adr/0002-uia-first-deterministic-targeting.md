# ADR-0002: Use UI Automation first and require exactly one target

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

Recorded windows can move, resize, change DPI, or contain repeated controls.
Coordinates alone cannot establish semantic identity. POC-03 demonstrated
deterministic descriptor resolution and explicit zero/duplicate failures.

## Decision

Semantic UI Automation descriptors and supported control patterns are the
primary recording and replay mechanism. Each resolution must produce exactly
one current element. Zero matches, duplicates, stale elements, process/window
context mismatches, and missing patterns are explicit results; the resolver
never chooses a nearest or best candidate.

## Consequences

- Workflows survive ordinary layout movement better than raw coordinates.
- Replay needs current-tree resolution and bounded waits for dynamic UIs.
- Weak or unstable application metadata can make a step unrecordable or
  require user review.
- Custom-rendered controls need the separate coordinate fallback decision.

## Rejected alternatives

- Nearest-match heuristics: unsafe and nondeterministic.
- Persisting `AutomationElement`/COM references: references are process- and
  lifetime-bound, not portable workflow data.
- Coordinates as the default: fragile under display/layout changes.
