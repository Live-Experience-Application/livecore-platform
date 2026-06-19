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
- the job collects coverage on both the SQLite leg and a **real Postgres + Valkey
  leg**, so the provider-divergent branches that only run there (the Npgsql `xmin`
  optimistic-concurrency token, the advisory-lock migration runner and the
  Redis/Valkey realtime backplane) are coverage-counted instead of no-ops; the gate
  **merges** both legs, which only flips no-op lines to covered, so the floor stays
  blocking and unchanged (CORE-TST-010);
- the gate is **blocking** (CORE-TST-009): it runs **without** `-ReportOnly`, so a
  production line-coverage regression below the floor **fails the build**. It ran
  report-only first, then `-ReportOnly` was dropped. The enforced floor is
  **`-MinimumLineCoverage 90`**, set just below the **92.79%** production coverage
  measured when the gate was made blocking; **ratchet it up** toward the current
  coverage over time, never down;
- the gate logic is itself tested (`scripts/test-coverage-gate.ps1`): a
  deliberately-untested new handler trips the threshold; the test proves the
  Postgres-leg merge (a SQLite-leg-only number that fails the gate passes once the
  Postgres-leg coverage is merged, staying fail-closed) and asserts the `coverage`
  job wires the Postgres/Valkey services and the `--collect` Postgres legs; and it
  asserts the enforced `90` floor and the blocking posture (the CI job runs the gate
  with no `-ReportOnly`), so re-adding `-ReportOnly`, dropping the Postgres-leg
  collection, or moving the floor undocumented fails CI.

See `docs/14_TESTING_STRATEGY.md` ("Coverage measurement and the CI gate") for
how to run coverage locally.
