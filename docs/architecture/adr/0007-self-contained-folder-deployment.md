# ADR-0007: Prefer self-contained win-x64 folder deployment first

- **Status:** Accepted pending operational validation
- **Date:** 2026-10-20

## Context

POC-08 compared framework-dependent and self-contained folder/single-file
publishes. All modes passed on the development machine, but no separate clean
Windows x64 machine has been tested (`cleanMachineTested=false`).

## Decision

The first operational candidate is a self-contained `win-x64` folder publish.
Keep single-file and framework-dependent modes as tested candidates, not the
default claim, until clean-machine launch, diagnostics, antivirus, update, and
rollback behavior are verified.

## Consequences

- The runtime is bundled and the deployment behavior is easier to inspect.
- The installation footprint contains multiple files and needs an installer
  or managed folder lifecycle.
- POC-08 local success does not become a clean-machine compatibility claim.
- Publish validation remains part of the release gate.

## Rejected alternatives

- Framework-dependent as the default: depends on a compatible installed runtime.
- Single-file immediately: operational extraction, diagnostics, update, and
  antivirus behavior are not yet verified.
