# Definition of Done

A Core feature is done only when all apply:

- story acceptance criteria met
- domain model implemented cleanly
- API contract implemented and documented
- database migration added if needed
- authorization implemented server-side
- negative authorization tests added
- event/audit behavior implemented if relevant
- logs/metrics added if relevant
- docs updated
- no forbidden vertical terms in Core source
- no unapproved dependencies
- CI passes
- code coverage measured and the coverage gate satisfied
- human review completed

No exceptions for "temporary MVP" shortcuts.

## Coverage gate (CORE-TST-001)

"No feature without tests" (`docs/14_TESTING_STRATEGY.md`) is no longer enforced
by author discipline alone. CI measures code coverage and runs a threshold gate,
so a new untested production handler cannot silently regress coverage:

- the `coverage` CI job collects coverage with `coverlet.collector`, uploads the
  per-project Cobertura reports as the `coverage-cobertura` artifact, and runs
  `scripts/assert-coverage.ps1` over the merged, de-duplicated, production-focused
  number (test assemblies and generated EF migrations are excluded);
- the gate **starts non-blocking** (`-ReportOnly`): it reports coverage and warns
  when the number falls below the minimum without failing the build. It is flipped
  to **blocking** by dropping `-ReportOnly`, and the minimum is ratcheted toward
  the current production coverage over time;
- the gate logic is itself tested (`scripts/test-coverage-gate.ps1`): a
  deliberately-untested new handler trips the threshold once blocking is enabled.

See `docs/14_TESTING_STRATEGY.md` ("Coverage measurement and the CI gate") for
how to run coverage locally.
