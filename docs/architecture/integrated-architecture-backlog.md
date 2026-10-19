# Integrated application architecture backlog after POC-08

POC-08 is the final exploratory feasibility checkpoint. This document is a
planning handoff only; it does not implement the main TeachMeOnce application.

## Architecture direction

1. **Recorder boundary** — capture low-level input only after target identity,
   process ownership, and privilege policy checks succeed.
2. **Target model** — persist deterministic semantic descriptors, not COM
   element references or unvalidated screen coordinates.
3. **Execution policy** — resolve exactly one target, wait asynchronously,
   reject ambiguity, and surface timeout/cancellation/error states.
4. **Coordinate fallback** — permit only a guarded, explicitly classified
   fallback for custom-rendered surfaces; require current window identity,
   foreground ownership, safe containment, unique candidate evidence, and a
   post-action confirmation. Never guess.
5. **Display context** — retain monitor, DPI, resolution, and coordinate-space
   evidence with each fallback action. POC-05 display coverage remains partial
   until the unavailable hardware matrix is executed.
6. **Privilege policy** — same-integrity automation is the MVP boundary.
   Higher-integrity targets produce `Unsupported`; no elevated helper,
   UIAccess executable, service, UAC bypass, or silent retry.
7. **Deployment boundary** — ship a self-contained `win-x64` folder candidate
   first; keep single-file optional until clean-machine and operational checks
   pass.

## Implementation backlog

### Foundation

- [ ] Create the main solution/application shell without changing the isolated
  POC fixtures.
- [ ] Add structured versioned configuration and per-user data/log locations.
- [ ] Add a central diagnostic/error-reporting contract with sanitized output.
- [ ] Add published-output CI validation using the POC-08 smoke pattern.

### Recording

- [ ] Integrate hook capture and bounded correlation from POC-02.
- [ ] Store process/window context and integrity level for every recorded step.
- [ ] Reject unsupported privilege boundaries before recording an action.

### Resolution and execution

- [ ] Integrate deterministic target descriptors and ambiguity rejection from
  POC-03.
- [ ] Integrate cancellable asynchronous waits from POC-04.
- [ ] Implement action adapters for supported UIA patterns.
- [ ] Implement guarded custom-rendered fallback as a separate explicit action
  type, not as a semantic-target substitute.

### Display and operations

- [ ] Complete the POC-05 monitor/DPI/manual matrix on supported hardware.
- [ ] Define coordinate-space serialization and migration rules.
- [ ] Decide whether single-file publishing is operationally acceptable after
  clean-machine, antivirus, diagnostics, and update testing.
- [ ] Define installer, signing, update, rollback, and uninstall requirements.

### Verification gates

- [ ] Preserve POC-01–07 regression evidence.
- [ ] Run POC-08 on a clean Windows x64 environment and record actual results.
- [ ] Do not claim POC-05 complete while its reports remain `Partial`.
- [ ] Do not treat POC-06 `Unsupported` cross-integrity interaction as a
  supported product feature.
