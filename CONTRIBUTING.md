# Contributing to livecore-platform

Thank you for your interest in the LiveCore Core Platform. This document is the
**contributor IP policy** (CORE-LIC-004). It explains the license your
contributions are made under, how you certify your right to contribute (the
Developer Certificate of Origin sign-off), the source-header requirement, and the
checks that enforce all of this in CI. Reading it before you open a pull request
keeps the project's contribution provenance clean and its licensing options open.

This is not legal advice.

## Table of contents

- [The license your contribution is made under](#the-license-your-contribution-is-made-under)
- [Sign your work — the Developer Certificate of Origin (DCO)](#sign-your-work--the-developer-certificate-of-origin-dco)
- [The contribution license grant (and the relicensing option)](#the-contribution-license-grant-and-the-relicensing-option)
- [SPDX source headers](#spdx-source-headers)
- [How the policy is enforced in CI](#how-the-policy-is-enforced-in-ci)
- [Run the contributor-policy checks locally](#run-the-contributor-policy-checks-locally)
- [Product neutrality and the rest of the bar](#product-neutrality-and-the-rest-of-the-bar)

## The license your contribution is made under

The Core Platform is licensed **AGPL-3.0-or-later** (`LICENSE`) and is
**dual-licensed**: a commercial license is offered for the uses the AGPL grant
does not permit (see [`docs/16_LICENSING.md`](docs/16_LICENSING.md), the
_Commercial and dual-license decision (CORE-LIC-002)_ section).

Contributions are **inbound = outbound**: unless you state otherwise in writing,
the work you submit is contributed under the **same AGPL-3.0-or-later** license
that covers the project. You retain the copyright to your contribution.

## Sign your work — the Developer Certificate of Origin (DCO)

This project uses the **Developer Certificate of Origin (DCO) 1.1** — the full
text is in [`DEVELOPER_CERTIFICATE_OF_ORIGIN`](DEVELOPER_CERTIFICATE_OF_ORIGIN) —
rather than a separate signed CLA document. The DCO is a lightweight, per-commit
certification that you have the right to submit your contribution.

**Every commit must be signed off.** Add the sign-off trailer to the commit
message:

```text
Signed-off-by: Your Name <your.email@example.com>
```

The easiest way is the `-s` flag, which appends the trailer using your configured
`git` name and email:

```bash
git commit -s -m "CORE-XXX-001: short lowercase description"
```

The sign-off **email must match the commit author's email**. To sign off commits
you have already made, rebase with `--signoff`:

```bash
git rebase --signoff <base>
```

By signing off, you certify the DCO: that you wrote the contribution (or have the
right to submit it) and that it may be distributed under the project's license. A
record of the sign-off is kept in the git history indefinitely, which is what
makes the project's contribution provenance auditable.

## The contribution license grant (and the relicensing option)

The Core is dual-licensed, and a commercial (non-AGPL) license can only be granted
over code the project has the right to relicense (`docs/16_LICENSING.md`,
CORE-LIC-002). So, in addition to the inbound = outbound AGPL license above, **by
signing off on a contribution under this policy you also grant the LiveCore
copyright holder (the project maintainer) the perpetual, non-exclusive,
worldwide, royalty-free right to license your contribution under the project's
commercial license** as part of the dual-license model — including in combination
with first-party and other contributed code.

You keep your copyright; this grant simply preserves the project's ability to
offer the **same** code under both the AGPL and the commercial license. Without it,
contributed code could never be included under the commercial license and would
have to be reimplemented, so the grant is what keeps a single, coherent codebase
available under both licenses. If you cannot make this grant for a particular
contribution, say so explicitly in the pull request **before** it is merged.

## SPDX source headers

Every first-party, hand-authored **source file that ships in a distribution
artifact** must start with an SPDX license header, so the file keeps its license
context if it is ever copied out of the repository:

```csharp
// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors
```

This applies to:

- **C# (`.cs`)** — the source that builds the API and worker container images, and
- **TypeScript (`.ts` / `.tsx`)** — the source that builds the published packages.

The same `//` two-line header is used for both. Use the current year on a new file;
the lint does not require you to bump the year on existing files.

**Out of scope** (no header required, and the lint skips them):

- generated source — the EF Core migrations under
  `apps/api/Persistence/Migrations` (scaffolded by the EF tooling) and the
  generated, drift-gated OpenAPI contract types
  (`packages/contracts/src/openapi.ts`, CORE-OAS-002);
- build output (`bin/`, `obj/`, `dist/`, `node_modules/`) and TypeScript
  declaration files (`*.d.ts`);
- first-party tooling that is **not shipped** in an artifact — the PowerShell
  scripts under `scripts/` and the `.mjs` build/test helpers.

You do not have to add headers by hand. The lint can insert any missing header for
you, preserving each file's existing body and line ending:

```bash
pwsh -NoProfile -File scripts/lint-license-headers.ps1 -Fix
```

## How the policy is enforced in CI

The `contributor-policy` job in [`.github/workflows/ci.yml`](.github/workflows/ci.yml)
runs on every push and pull request and fails the build when the policy is not met:

1. **DCO sign-off** — `scripts/lint-dco-signoff.ps1` validates that every commit
   introduced by the push/PR carries a `Signed-off-by` trailer matching its author
   (merge commits are exempt). A commit without the required sign-off fails CI.
2. **SPDX source headers** — `scripts/lint-license-headers.ps1` validates that
   every in-scope shipped source file carries the header. A file missing it fails CI.
3. **The gate logic itself is tested** — `scripts/test-contributor-policy.ps1`
   proves, on every run, that an unsigned commit and a headerless source file are
   rejected and that a signed-off commit and a headered file pass.

## Run the contributor-policy checks locally

Before opening a pull request, run the same checks CI runs:

```bash
# Prove the gate logic, then verify the working tree.
pwsh -NoProfile -File scripts/test-contributor-policy.ps1
pwsh -NoProfile -File scripts/lint-license-headers.ps1
# Validate the sign-off on the commits you are about to push (range optional).
pwsh -NoProfile -File scripts/lint-dco-signoff.ps1 -BaseSha origin/main -HeadSha HEAD
```

## Product neutrality and the rest of the bar

The Core Platform must stay **product-neutral**: source code must not contain the
vertical or brand terms listed in [`AGENTS.md`](AGENTS.md) and
[`csv/forbidden_core_terms.csv`](csv/forbidden_core_terms.csv). A boundary scan
enforces this. Beyond licensing, contributions are also expected to meet the
[Definition of Done](docs/17_DEFINITION_OF_DONE.md) — tests for new behavior,
negative authorization tests where security-relevant, updated docs when contracts
change, and a green CI. See [`README.md`](README.md) for how to build, test, lint
and run the platform.
