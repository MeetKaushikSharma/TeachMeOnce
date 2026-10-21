# ADR-0006: Persist versioned per-user JSON workflows atomically

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

Workflows must survive process restarts, be inspectable for support, and remain
independent of live UIA COM objects. A corrupt or future-version file must not
hide all valid workflows.

## Decision

Persist a versioned workflow document as one JSON file per workflow below the
user's LocalAppData directory. Validate schema and action data on load and save.
Write to a temporary file and atomically replace the destination. Load valid
files independently and return per-file issues. Future schema versions are
rejected until an explicit migration exists.

## Consequences

- Restart recovery and manual support inspection are straightforward.
- Schema migrations become deliberate, testable pure transforms.
- File ACLs and sensitive-value redaction remain part of the product security
  design.
- JSON is not an encrypted secret store; sensitive input is refused/redacted by
  policy rather than hidden behind serialization.

## Rejected alternatives

- Persisting COM/UIA objects: not serializable or stable across processes.
- One monolithic library file: one corrupt record can damage all workflows.
- Silent best-effort loading of future schemas: can execute with incorrect
  semantics.
