# ADR-0005: Limit MVP interaction to same-integrity targets

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

Windows integrity boundaries restrict input and UIA interaction. POC-06
verified same-integrity behavior and intentionally reports higher-integrity
interaction as `Unsupported`.

## Decision

The MVP runs and automates at the standard user's integrity level. It may
inspect and report a higher-integrity target, but it must not interact with it.
There is no elevated helper, service, UIAccess executable, UAC bypass, or
silent elevated retry.

## Consequences

- The security boundary is simple and explicit.
- Elevated applications require a future separately authorized product design.
- Users receive a diagnosable Unsupported result rather than a partial or
  misleading replay.
- POC-06 cross-integrity Unsupported evidence remains a product limitation.

## Rejected alternatives

- Silent elevation: violates user control and creates an unsafe privilege path.
- An always-on elevated helper/service: expands attack surface and deployment
  complexity beyond the MVP.
- UIAccess as a shortcut: requires signing/deployment/security work not proven
  by the feasibility evidence.
