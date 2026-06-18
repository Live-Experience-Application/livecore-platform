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
production line-coverage number and checks it against a minimum. The CI
`coverage` job runs it **blocking** at the enforced floor:

```bash
pwsh -NoProfile -File scripts/assert-coverage.ps1 -ReportDirectory ./TestResults -MinimumLineCoverage 90
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

### Staged enforcement: report-only, now blocking at the floor (CORE-TST-009)

The gate followed the same staged posture as the supply-chain image scan: it
**started non-blocking** (`-ReportOnly`, reporting coverage and warning on a
regression without failing the build), then was flipped to **blocking** by
dropping `-ReportOnly` once coverage was strong enough to defend a floor.

It is now **blocking**. The CI `coverage` job runs `assert-coverage.ps1`
**without** `-ReportOnly`, so a production line-coverage regression below the
floor **fails the build** — a new untested production handler that drags the
number under the floor cannot merge.

The enforced floor is **`-MinimumLineCoverage 90`**. It was set just below the
**92.79%** production line coverage measured when the gate was made blocking
(the production-focused number: test assemblies and generated EF migrations
excluded), leaving a small margin for the run-to-run variance of the
concurrency/retry integration tests (the money-path race tests above
deliberately exercise timing-dependent branches). **Ratchet the floor up** toward
the current coverage as it rises — never lower it; this is the documented value
to raise over time.

The gate logic is itself tested (`scripts/test-coverage-gate.ps1`, run first in
the CI `coverage` job): a deliberately-untested new handler trips the threshold
once blocking is enabled, coverage from several reports merges without double
counting, and test/generated code is excluded. The same test also **asserts the
enforced floor and the blocking posture** — coverage exactly at the floor passes
and a hair below fails, and the CI `coverage` job invokes `assert-coverage.ps1`
with no `-ReportOnly` and pins `-MinimumLineCoverage` to the documented `90` — so
re-adding `-ReportOnly`, or moving the floor in `ci.yml` without updating the
test and these docs, fails CI.

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

## Dependency vulnerability audit (SCA) and the CI gate (CORE-DEP-005)

SAST proves the first-party code is not dangerous; the dependency audit proves
the code the project _depends on_ carries no known-vulnerable package. CI already
scanned the published container images for CVEs (Trivy, CORE-DEP-003), pinned the
dependency closure with lock files (CORE-CMP-002) and statically analyzed the
sources (CodeQL, CORE-SEC-006) — but it never audited the project's **own
declared dependency graph**, so a first-party direct or transitive dependency
with a published advisory had no automated tripwire.

The CI `dependency-audit` job closes that gap. It audits **both** ecosystems on
every push and pull request and **fails closed on a high/critical advisory**:

- **.NET** — after a locked-mode restore (CORE-CMP-002), it runs
  `dotnet list LiveCore.slnx package --vulnerable --include-transitive --format json`
  over every project, so a vulnerable **transitive** package is caught as well as
  a direct one;
- **TypeScript workspace** — after `pnpm install --frozen-lockfile`, it runs
  `pnpm audit --json` over the workspace. `pnpm audit` exits non-zero on _any_
  advisory (including moderate/low), so its own exit code is ignored and the gate
  decides the verdict by severity.

### The agreed severity and how it fails closed

The agreed blocking bar is **HIGH and CRITICAL**: a high/critical advisory on a
first-party direct or transitive dependency fails the build, while a
**moderate/low/info** advisory is reported but does not block. Both ecosystems
report severities from the same vocabulary (`low` / `moderate` / `high` /
`critical`; npm/pnpm adds `info`), normalized to upper case so one gate spans
both. The bar is configurable (`scripts/assert-dependency-audit.ps1
-FailOnSeverity`).

The gate is **self-contained and fail-closed**, the same posture as the
supply-chain image-scan, coverage and CodeQL gates:

- the pass/fail decision and the two report parsers live in
  `scripts/LiveCoreDependencyAudit.psm1` as pure functions, so the verdict is
  deterministically testable from seeded fixtures without a network, a registry
  or a real restore;
- a missing or malformed audit report, or no report supplied at all, **blocks**
  the build (an audit that produced no readable output is not a clean audit);
- `scripts/assert-dependency-audit.ps1` is the CLI the job runs; it accepts a
  `-ReportOnly` switch for symmetry with the other gates, but the job runs it
  **blocking**, because the story requires CI to fail on a known-vulnerable
  dependency.

### Proving the gate fails closed

Committing a real vulnerable dependency to "prove the gate fails" would itself be
the finding, so the required "a seeded vulnerable-package fixture proves it fails
closed" test is the gate-logic test `scripts/test-dependency-audit.ps1`, run
**first** in the `dependency-audit` job (before the real audit) exactly like
`test-codeql-gate.ps1` and `test-image-scan.ps1`. It feeds the gate seeded
`dotnet list --vulnerable` and `pnpm audit` reports and asserts:

- a report carrying a **critical** top-level and a **high** transitive package
  fails the gate (and, end to end, the `assert-dependency-audit.ps1` CLI exits
  non-zero), while the **moderate** package in the same report does not block;
- a **clean** report, and a **moderate-only** report, pass the default
  high/critical bar;
- widening the failing set to `MODERATE` then blocks the moderate-only report
  (the bar is configurable);
- a malformed or empty report, and supplying no report at all, all **fail
  closed**.

Run it locally with `pwsh -NoProfile -File scripts/test-dependency-audit.ps1`.

### The PR image CVE scan is now blocking on critical (CORE-DEP-005)

The same story promotes the **PR-time image CVE scan** from report-only to
**blocking on critical**. The image scan (Trivy, CORE-DEP-003) blocked only on
the release `publish` job; on a pull request the `publish-dry-run` job ran the
gate with `-ReportOnly`, so a critical base-image CVE documented itself without
failing the PR. That step now runs the gate **without** `-ReportOnly`, so a
**critical** vulnerability (or a missing/empty SBOM) fails the pull request at the
same critical-only bar the release publish enforces. Clear a flagged base-image
CVE by bumping the pinned base-image digest in `apps/api/Dockerfile`,
`apps/worker/Dockerfile` and `apps/api/Migrations.Dockerfile`. The image-scan
gate-logic test (`scripts/test-image-scan.ps1`) is unchanged and still proves a
seeded critical CVE fails the gate.

### Wired into the required-checks set

The `dependency-audit` job is part of the same required-checks set the release
jobs depend on: both `publish` and `publish-packages` `needs:` it, so a
high/critical dependency advisory fails the build and, transitively, blocks any
release (no image push, no npm publish). This audits the project's source
dependency graph; **GitHub Actions pinning and the Dependabot update-PR policy are
the next section (CORE-DEP-008)**. See the README "Continuous integration" and
"Supply chain" sections.

## GitHub Actions pinning and the dependency-update policy (CORE-DEP-008)

The dependency audit above proves the project's _declared_ packages carry no known
vulnerability, but the **build pipeline itself** has a dependency surface of its
own: every third-party GitHub Action a workflow runs. Those actions were referenced
by a **mutable major tag** (`actions/checkout@v4`, `actions/setup-dotnet@v4`,
`actions/setup-node@v4`, `actions/upload-artifact@v4`, `github/codeql-action@v3`),
so the tag could be re-pointed at a different commit at any time. A compromised or
retagged action would then run inside CI — including the `publish` job that holds
the registry's `packages: write` token — with no gate noticing.

CORE-DEP-008 closes that surface by extending the **digest-pinning discipline the
Dockerfiles already apply to their base images** (`name:tag@sha256:...`,
`apps/api/Dockerfile`) to the workflows, and adds the automation that keeps the
pins from going stale:

- **Every `uses:` is pinned to an immutable commit SHA.** Each reference in
  `.github/workflows/*` is a full 40-char commit SHA, with the readable version
  kept in a trailing comment (`uses: actions/checkout@34e1148… # v4.3.1`), so the
  pin is immutable but a human can still see what it resolves to.
- **A fail-closed CI lint (`action-pin-lint`).** `scripts/lint-action-pins.ps1`
  scans the workflows on every push and pull request and **fails the build** on any
  `uses:` reference that is not a 40-char SHA — a floating tag (`@v4`), a
  pinned-looking but still-mutable semver tag (`@v4.3.1`), a branch (`@main`), a
  ref-less action, an un-digested `docker://` image, or a SHA with no readable
  version comment. A first-party in-repo action (`./.github/actions/...`) has no
  third-party ref and is allowed.
- **`.github/dependabot.yml` keeps the pins current.** Dependabot is GitHub-native
  (no heavyweight tool, no extra supply-chain dependency). It raises grouped weekly
  update PRs for the three ecosystems this repo declares dependencies in —
  `github-actions`, `npm` (the pnpm workspace) and `nuget` (the .NET solution). For
  a SHA-pinned action it bumps the SHA **and** rewrites the `# vX.Y.Z` comment, so
  immutability never means staleness, and a Dependabot PR runs the full pipeline
  (including `action-pin-lint` and `dependency-audit`), so an update only merges
  green.

### Proving the lint fails closed

The gate decision is pure logic (`scripts/LiveCoreActionPinLint.psm1`), so it is
deterministically testable with no network and no GitHub. The required test
`scripts/test-action-pin-lint.ps1` runs **first** in the `action-pin-lint` job
(before the real lint), exactly like the other gate-logic tests, and asserts:

- a 40-char SHA ref **with** a version comment is accepted, and one **without** a
  comment is rejected;
- a floating major tag (`@v4`), a semver tag (`@v4.3.1`), a branch (`@main`) and a
  ref-less action are each rejected as unpinned — **the required "a seeded
  floating-tag ref fails the lint" case** — while a `docker://` digest ref and a
  local in-repo action are handled correctly;
- a `uses:` appearing inside a `run:` script body or a comment is **not** mistaken
  for a step key;
- end to end, a seeded floating-tag workflow makes the directory-level review fail,
  and the real `.github/workflows/*` tree passes (every `uses:` is SHA-pinned).

Run it locally with `pwsh -NoProfile -File scripts/test-action-pin-lint.ps1`.

### Wired into the required-checks set

The `action-pin-lint` job is part of the same required-checks set the release jobs
depend on: both `publish` and `publish-packages` `needs:` it, so an unpinned
`uses:` fails the build and, transitively, blocks any release. This is the
workflow-side analogue of the base-image digest pinning the supply-chain story
(CORE-DEP-003) applies to the Dockerfiles. See the README "Continuous integration"
and "Supply chain" sections.

## Image signing and SBOM attestation and the CI gate (CORE-SEC-008)

The CVE scan proves a published image carries no known-critical vulnerability and
the SBOM records what is inside it (CORE-DEP-003), but neither proves the image
was **built by this pipeline**. The release `publish` job now signs each published
image and attaches its CycloneDX SBOM as a verifiable attestation with
[Sigstore cosign](https://docs.sigstore.dev/), so a self-hoster can prove a
`ghcr.io` image's provenance — the container-image analogue of the npm build
provenance the `@livecore/*` packages carry (CORE-PUB-004).

The flow on a **release tag**, after the CVE gate passes and the images are
pushed (the signature binds to the published digest, so it must run after push):

- **Keyless `cosign sign`** against each published digest. cosign requests a
  short-lived GitHub Actions OIDC token, Fulcio issues a throwaway certificate
  bound to the workflow identity and Rekor logs the signature — **no private key**
  exists. `id-token: write` is scoped to the **`publish` job only**.
- **`cosign attest`** attaches the same CycloneDX SBOM produced for the CVE gate
  as an in-toto attestation (`--type cyclonedx`) bound to the digest.
- **`cosign verify` / `cosign verify-attestation`** against the published digest,
  asserting the GitHub OIDC issuer and this repo's release-workflow identity, then
  the fail-closed gate (`scripts/assert-image-attestation.ps1`) over the
  verification output.

### The gate and how it fails closed

cosign performs the cryptography and its exit code is the first line of defence,
but the **decision** that turns its verification output into a published-or-blocked
verdict is pure logic (`scripts/LiveCoreImageAttestation.psm1`), the same
self-contained, fail-closed posture as the image-scan, coverage, CodeQL and
dependency-audit gates:

- a signature is verified only when at least one signature claim is present, and
  the SBOM attestation only when at least one in-toto statement of the requested
  predicate type (CycloneDX) carries a non-empty predicate;
- a **missing, empty, wrong-predicate or malformed** verification document
  **blocks** — a cosign run that somehow exited zero with no usable output never
  counts as verified.

### Proving the gate fails closed

Committing a real signing key to "prove the gate fails" would itself be the
problem, so the required "fails closed if the signature or SBOM attestation is
missing or invalid" proof is split two ways:

- the gate-logic test `scripts/test-image-attestation.ps1`, run **first** in the
  `publish-dry-run` job (before any cosign round-trip) exactly like
  `test-image-scan.ps1`, feeds the gate seeded `cosign verify` /
  `verify-attestation` fixtures and asserts a verified signature and CycloneDX
  attestation pass while a missing/empty/wrong-type/malformed one fails closed —
  deterministically, with no Docker, registry or cosign; and
- a **real cosign round-trip** in `publish-dry-run`, mirrored against a
  **locally-built digest** in a throwaway local registry with a throwaway key (no
  OIDC, **no push**): it signs, attests, verifies and verify-attestations each
  dry-run image and includes a negative check that an **unsigned** image fails
  verification.

Run the gate-logic test locally with
`pwsh -NoProfile -File scripts/test-image-attestation.ps1`. cosign is installed
from a pinned release binary, the same way Trivy and promtool are, so **no extra
GitHub Action enters the supply chain**.

### Wired into the required-checks set

The signing/attestation step lives in the release `publish` job, which already
`needs:` every gate, and the gate-logic test and the dry-run round-trip run in
`publish-dry-run` (which `publish` `needs:`), so a regression in the verification
fails the build and, transitively, blocks any release. See the README
"Continuous integration" and "Supply chain" sections and
`docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Signed images and SBOM attestations").
