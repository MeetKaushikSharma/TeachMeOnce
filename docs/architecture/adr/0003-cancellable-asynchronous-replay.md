# ADR-0003: Make replay waits bounded, asynchronous, and cancellable

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

Target windows and controls may appear after a workflow starts, and UIA
providers can be temporarily unavailable. Blocking the WPF thread or treating
timeout and cancellation as success makes the tool unsafe and unresponsive.

## Decision

Replay is an application operation with a cancellation token, an explicit
deadline for every wait, and observable states for satisfied, not found,
timed out, cancelled, provider error, and unsupported. It checks cancellation
before each action, wait, retry, and verification. It never blocks the WPF UI
thread.

## Consequences

- The UI can remain responsive and report progress.
- Every action needs a timeout and a diagnostic reason.
- Retry is narrowly limited to safe stale-provider reacquisition; it cannot
  turn ambiguity into a guess.
- Tests must cover cancellation and timeout paths, not only successful clicks.

## Rejected alternatives

- Unbounded polling: can hang workflows indefinitely.
- `Thread.Sleep` on the UI thread: freezes the app and prevents cancellation.
- Treating cancellation as failure-only: loses the user-requested terminal state.
