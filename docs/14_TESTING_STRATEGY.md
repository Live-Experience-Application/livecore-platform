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

The money paths additionally have **real write-concurrency** coverage on the
PostgreSQL leg (CORE-TST-006, `MoneyPathWriteConcurrencyTests`). The duplicate /
race-resolution branches in the billing repositories — the
`DbUpdateException -> re-read -> resolve` recovery in
`BillingAccountLinkRepository` / `PurchaseTransactionRepository` /
`SubjectEntitlementRepository`, and the `xmin` `DbUpdateConcurrencyException` guard
on a purchase-status change — were until then only reasoned, never exercised by two
genuinely concurrent writers (the integration suite's single shared SQLite
connection serializes every write, and the cross-subject tests were sequential
A-then-B, so the loser's re-read never fired). The new tests race two writers — each
on its own `LiveCoreDbContext`, and so its own Npgsql connection — at the same
colliding key: the same `billing_account_link` for one purchase, the same
`(subject, entitlement)` grant, the same purchase recording, and the same
purchase-status change. On PostgreSQL exactly one writer wins and the loser re-reads
and resolves (a duplicate, or a loud `xmin` conflict that converges) — **never a
double-grant**; on SQLite the writers run sequentially (the same branch, without the
true concurrency). The final test races the whole verify → record → link → grant
chain over real HTTP for two buyers submitting one receipt: a unique violation
inside the endpoint's explicit transaction aborts it on PostgreSQL, so the duplicate
surfaces either as a clean **409** (the requests serialize and the loser's
find-first sees the committed link) or as a **500 that succeeds on retry** (the
inserts truly collide and abort the loser's transaction) — but the persisted result
is always one billing link and one buyer's worth of entitlements, never a
double-grant.

The store-notification unit of work also has **revoke-failure rollback** coverage
(CORE-TST-008, `StoreNotificationRevokeFailureRollbackTests`). On a revoking
notification (a refund) `StoreNotificationService.HandleAsync` revokes the granted
entitlement BEFORE the purchase status change and the dedup-ledger write, all inside
one `TransactionalUnitOfWork` (CORE-MON-010 / CORE-MON-004) — but until then the
atomic-rollback claim for that revoke + status change + ledger trio was asserted in
prose only, never exercised by a failing revoke. The new tests swap the
`ISubjectEntitlementRepository` the revoke writes through for a decorator that throws
on its `UpdateAsync`, so the failure lands squarely in the revoke step, and assert
that **nothing** persists: the purchase stays `Active` (no downgrade), no ledger row
is written, no downgrade purchase event is appended, and the buyer keeps every
entitlement. A second test grants a plan with **two** entitlements and fails the
**second** revoke write, so an earlier revoke has already committed inside the
transaction — proving even a partially-persisted revoke is rolled back, so a
re-delivery replays the whole effect from scratch rather than finding a half-revoked
subject.

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

## Static analysis (SAST) and the CI gate (CORE-SEC-006)

Coverage and the test suite prove the first-party code does what it should; SAST
proves it does not do something dangerous. CI scanned _dependencies_ for known
vulnerabilities (the Trivy CVE scan and pinned lock files, CORE-DEP-003 /
CORE-CMP-002) but, until this gate, never statically analyzed the C# and
TypeScript the project itself writes — so an injection, a cleartext-secret or a
similar high-severity defect in first-party code had no automated tripwire.

The CI `codeql` job closes that gap. It runs **CodeQL** (GitHub's first-party
SAST engine) over both first-party languages on every push and pull request:

- **C#** is analyzed in CodeQL `manual` build mode against the same
  `dotnet build LiveCore.slnx` the `dotnet` job runs, so the analysis sees the
  exact compiled sources;
- **TypeScript** (`javascript-typescript`) is analyzed buildless
  (`build-mode: none`), because CodeQL extracts it from source directly.

### The severity gate and how it fails closed

The acceptance bar is that the **build fails on a high/critical finding**. The
gate keys on CodeQL's `security-severity` property — the CVSS-style score it
attaches to a security query's rule — and GitHub's documented bands:

```text
critical  9.0 - 10.0
high      7.0 -  8.9
medium    4.0 -  6.9
low       0.1 -  3.9
```

so the gate **blocks the build on any result scoring `security-severity >= 7.0`**
(high and critical). A medium/low or a non-security maintainability finding (no
`security-severity`) is reported but never blocking — it is a _security_ gate.
The floor is configurable (`scripts/assert-codeql-findings.ps1
-MinimumSecuritySeverity`).

The gate is **self-contained and fail-closed**, the same posture as the
supply-chain image-scan and coverage gates:

- CodeQL writes its SARIF result file locally (`upload: false`) instead of relying
  on GitHub code scanning being enabled, and `scripts/assert-codeql-findings.ps1`
  makes the pass/fail decision over it — so the gate needs no GitHub Advanced
  Security and cannot be silently disabled by a repository setting;
- a missing, empty or malformed SARIF file, or a results directory with no SARIF
  at all, **blocks** the build (an analysis that produced no readable output is
  not a clean analysis);
- the decision logic lives in `scripts/LiveCoreCodeQL.psm1` as pure functions, so
  it is deterministically testable without running CodeQL.

### Proving the gate fails closed

Committing a real vulnerability to "prove the gate fails" would itself be a
finding, so the required "fails closed on a high/critical finding" test is the
gate-logic test `scripts/test-codeql-gate.ps1`, run **first** in the `codeql` job
(before the real analysis) exactly like `test-coverage-gate.ps1` and
`test-image-scan.ps1`. It feeds the gate seeded SARIF fixtures and asserts:

- a SARIF carrying a **high (7.5) and a critical (9.8)** security finding fails
  the gate (and, end to end, the `assert-codeql-findings.ps1` CLI exits non-zero);
- a **clean** SARIF, and a **medium/low-only** SARIF, pass the default
  high/critical floor;
- lowering the floor to 4.0 then blocks the medium finding (the floor is
  configurable);
- severity is resolved whether the rule metadata lives under `tool.driver.rules`
  or a `tool.extensions[].rules` entry;
- a malformed document, a document with no `runs` array, an empty results
  directory and a missing directory all **fail closed**.

### Wired into the required-checks set

The `codeql` job is part of the same required-checks set the release jobs depend
on: both `publish` and `publish-packages` `needs:` every gate including `codeql`,
so a high/critical finding fails the build and, transitively, blocks any release
(no image push, no npm publish). See the README "Continuous integration" section
and `docs/07_SECURITY_THREAT_MODEL.md` ("Static analysis of first-party code").
