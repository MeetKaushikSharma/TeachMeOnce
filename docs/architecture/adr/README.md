# Architecture decision records

These ADRs record durable product decisions after the feasibility POCs. They
are concise by design; implementation details belong in
[`../system-architecture.md`](../system-architecture.md) and the backlog.

| ADR | Decision |
| --- | --- |
| [0001](0001-library-boundaries.md) | Keep presentation, application, and automation in library boundaries |
| [0002](0002-uia-first-deterministic-targeting.md) | Prefer UI Automation and require exactly one target |
| [0003](0003-cancellable-asynchronous-replay.md) | Make waits bounded, asynchronous, cancellable, and observable |
| [0004](0004-explicit-coordinate-fallback.md) | Treat coordinates as a guarded explicit action |
| [0005](0005-same-integrity-mvp.md) | Support same-integrity automation only in the MVP |
| [0006](0006-versioned-json-workflows.md) | Persist versioned per-user JSON workflows with atomic writes |
| [0007](0007-self-contained-folder-deployment.md) | Prefer self-contained `win-x64` folder deployment first |
