# ADR-0001: Keep product responsibilities in library boundaries

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

The repository contains executable feasibility fixtures and harnesses as well
as the product application. The POCs are intentionally isolated and often own
process-launch or report-generation behavior that is not an application API.

## Decision

The product uses three primary boundaries: `TeachMeOnce.App` for WPF
presentation, `TeachMeOnce.Application` for use cases/contracts/persistence,
and `TeachMeOnce.Automation` for reusable UI Automation behavior. Executable
POCs remain clients/tests and are never referenced as product libraries.

## Consequences

- Product code can be tested without launching a POC fixture.
- POC evidence remains reproducible and isolated.
- A capability promoted from a POC must first be extracted or linked into a
  reusable library boundary.
- Some duplication of fixture-specific composition is intentional.

## Rejected alternatives

- Referencing an executable POC from the app: couples production behavior to a
  harness and violates the repository boundary.
- Copying POC implementations into the app: creates divergent behavior and
  loses the tested implementation boundary.
