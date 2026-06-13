# Package Versioning and Changelog Process

This document defines how the LiveCore Core Platform versions and changes its
published TypeScript packages so that vertical apps can consume **stable, typed
Core packages** with predictable upgrade semantics (the acceptance criterion of
the `SDK and UI Core Packages` epic; CORE-SDK-005).

It applies only to the published TypeScript packages:

```text
@livecore/contracts       packages/contracts
@livecore/sdk-ts          packages/sdk-ts
@livecore/design-tokens   packages/design-tokens
@livecore/ui-core         packages/ui-core
```

The .NET API and worker hosts are deployed applications, not published packages,
and are versioned by deployment — they are out of scope here.

## Semantic Versioning

The packages follow [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
Given a version `MAJOR.MINOR.PATCH`:

- **MAJOR** — a breaking change to a package's public, typed surface: a removed
  or renamed export, a narrowed accepted type, a widened required input, a
  changed enum/tuple value, or any change that can make a previously compiling
  consumer stop compiling or change runtime behavior.
- **MINOR** — a backward-compatible addition: a new export, a new optional field,
  a new enum member added to a tuple/union, a new resource client method.
- **PATCH** — a backward-compatible fix that changes neither the types nor the
  documented behavior (documentation, internal refactor, build fix).

While the packages are pre-1.0 (`0.y.z`), the public surface may still evolve;
a breaking change is released as a **minor** bump and is always called out in the
changelog under a `### Changed` or `### Removed` heading. Consumers should pin a
caret range (for example `^0.1.0`) and read the changelog before upgrading.

## Lockstep releases

The four packages are released **together** and always share a single version.
They are designed as one coherent surface (`@livecore/sdk-ts` is typed entirely
in terms of `@livecore/contracts`; `@livecore/ui-core` props are themed by
`@livecore/design-tokens`), so a single shared version removes any ambiguity
about which combination a vertical is running. A release bumps the `version`
field of all four `package.json` files and the `VERSION` runtime constant in each
package's `src/index.ts` to the same value, even for a package with no change in
that release.

## Runtime version introspection

Each package exports two stable runtime values from its entry point:

```ts
import { PACKAGE_NAME, VERSION } from "@livecore/contracts";
```

`VERSION` lets a vertical app log or assert exactly which Core package release it
is running against. The exported `VERSION` literal is the single source of truth
inside the bundle and is kept in lockstep with the package manifest and the
changelog (see below).

## Changelog

Every published package keeps a `CHANGELOG.md` in
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format, and the
repository root keeps a workspace-level `CHANGELOG.md` summarizing the release
across all four packages. Each release adds a `## [MAJOR.MINOR.PATCH] - YYYY-MM-DD`
section whose `Added` / `Changed` / `Deprecated` / `Removed` / `Fixed` subsections
describe the change in terms of the package's public surface only — never any
vertical domain language (`AGENTS.md`).

## How to cut a release

1. Pick the new version from the nature of the changes (the rules above).
2. Update the `version` field in all four `packages/*/package.json` files.
3. Update the `VERSION` constant in each `packages/*/src/index.ts` to match.
4. Add a `## [<version>] - <date>` entry to each affected package's
   `CHANGELOG.md` and to the root `CHANGELOG.md`.
5. Run the full local CI-equivalent (`README.md`). The package-build tests fail
   if `VERSION`, `package.json` and the changelog top entry disagree, so version
   drift cannot ship.

## Enforcement

The consistency of the process is enforced by tests, not convention:

- A **type test** in each package checks at compile time that `VERSION` is a
  well-formed, non-widened Semantic Versioning string literal.
- A **package-build test** in each package checks, against the compiled output,
  that `VERSION` is valid SemVer, that it equals the package's own
  `package.json` version, that `CHANGELOG.md` has a top entry for that version,
  and that the changelog ships with the package (`files` includes `CHANGELOG.md`).

## Publishing

The packages are currently marked `private` and consumed inside this workspace.
Wiring an actual publish pipeline (a registry, release automation and removing
`private`) is a follow-up; this document defines the versioning and changelog
discipline that any such pipeline builds on.
