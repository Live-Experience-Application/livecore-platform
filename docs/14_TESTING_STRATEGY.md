# Testing Strategy

## Test pyramid

```text
Unit tests -> domain rules and policies
Integration tests -> API/database/realtime behavior
Contract tests -> SDK/API/event compatibility
End-to-end tests -> critical user flows
Security tests -> authorization and visibility negative cases
```

## Required test areas

- organization isolation
- workspace membership checks
- participant feed filtering
- visibility rule evaluation
- reveal idempotency
- event replay filtering
- asset authorization
- audit log creation
- migration correctness

## Test naming

Tests should read like behavior specs.

Example:

```text
Participant_cannot_read_content_block_when_visibility_rule_excludes_participant
```

## No feature without tests

A story that changes behavior is incomplete without tests.

## Which provider the tests run against (SQLite vs PostgreSQL)

The test projects run by default against an in-memory SQLite database
(repository tests open their own `SqliteConnection`; the integration suite's
`WorkspaceApiFactory` swaps the production Npgsql registration for SQLite). That
keeps a default `dotnet test` and every developer machine free of any database
server, and it exercises the real model mapping and SQL translation for the
relational semantics SQLite and PostgreSQL share.

A few behaviors are **provider-specific** and genuinely diverge from SQLite —
collation, case-sensitivity, JSON, and the optimistic-concurrency token (the
PostgreSQL system column `xmin`, mapped **only** on the Npgsql provider, see
`docs/10_DATABASE_SCHEMA.md` and CORE-CONC-001). Those must be exercised on real
PostgreSQL, not only SQLite, so two CI legs run the suites against a real
PostgreSQL service container, selected through the
`LIVECORE_TEST_DB_PROVIDER`/`LIVECORE_TEST_POSTGRES` environment variables (unset
locally, so a default run stays on SQLite):

- the **`integration-postgres`** job runs the integration project against real
  PostgreSQL, where each test's schema is applied by the real, checked-in
  migrations (CORE-OPS-002); and
- the **`unit-smoke-postgres`** job runs the **unit and smoke** suites against
  real PostgreSQL (CORE-TST-004). Before it, the `dotnet` job's whole-solution
  step ran every unit + smoke test on SQLite only — that step was also mislabeled
  "Run smoke tests" though it runs the whole solution, now renamed to match what
  it runs. Unit/repository tests that assert provider-divergent behavior opt into
  the real database through the `ProviderTestDatabase` helper (it provisions a
  throwaway, migration-schema PostgreSQL database when the environment selects
  PostgreSQL and falls back to in-memory SQLite otherwise); the
  `ProviderDivergentConcurrencyTests` repository test proves the `xmin`
  cross-context concurrency conflict on PostgreSQL and the last-write-wins absence
  of the token on SQLite, so it passes on **both** providers while exercising the
  PostgreSQL-only semantics on the PostgreSQL leg.

## Coverage measurement and the CI gate

"No feature without tests" is enforced, not just expected (CORE-TST-001). CI
measures code coverage and runs a threshold gate so a new untested production
handler cannot silently regress coverage.

### Collecting coverage locally

The test projects reference `coverlet.collector`, so coverage is collected by
passing the data collector to `dotnet test`. From the repository root:

```bash
dotnet test LiveCore.slnx --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

Each test project writes a `coverage.cobertura.xml` under `TestResults/`.

### Reporting and the threshold gate

`scripts/assert-coverage.ps1` merges those Cobertura reports into a single
production line-coverage number and checks it against a minimum:

```bash
pwsh -NoProfile -File scripts/assert-coverage.ps1 -ReportDirectory ./TestResults -MinimumLineCoverage 80 -ReportOnly
```

The number is **production-focused** and **de-duplicated** (the gate logic lives
in `scripts/LiveCoreCoverage.psm1`):

- lines are merged across every report by type and source file, so a line
  exercised by the integration suite but not the unit suite counts as covered
  exactly once - there is no double counting when several test projects cover the
  same assembly (the merge is invariant to the differing source-path roots the
  test projects record);
- test assemblies (named `*Tests`) and generated EF migration files (under a
  `Migrations` directory) are excluded from the denominator, so the gate measures
  hand-written production code.

The gate is **fail-closed**: no reports found, or a missing/malformed report,
blocks rather than passing silently.

### Staged enforcement: non-blocking, then blocking

The gate **starts non-blocking**. The CI `coverage` job runs it with
`-ReportOnly`, so it reports coverage and warns on a regression below the minimum
without failing the build. It is flipped to **blocking** by dropping
`-ReportOnly`, and `-MinimumLineCoverage` is ratcheted toward the current
production coverage over time. This mirrors the supply-chain dry-run's
report-only posture (`scripts/assert-image-scan.ps1`).

The gate logic is itself tested (`scripts/test-coverage-gate.ps1`, run first in
the CI `coverage` job): a deliberately-untested new handler trips the threshold
once blocking is enabled, coverage from several reports merges without double
counting, and test/generated code is excluded.
