# Forbidden Core Terms

Core source code must not contain vertical terms, nor the brand/platform names
(ArcanOS, ScenarioOS, Enterprise, DnD, Pen-and-Paper).

See `csv/forbidden_core_terms.csv` for the full, enforced list.

## How to handle false positives

Some terms can appear in documentation to explain boundaries. Source code, API routes, DTOs, database tables and event names must avoid them.

## CI check

`scripts/boundary-scan.ps1` enforces this on every push and pull request
(the `boundary-scan` CI job). It:

- enumerates **tracked** files only (`git ls-files`), so gitignored local
  tooling is never scanned and a stray working-tree file cannot change the scan;
- scans every tracked text source, including Dockerfiles (`apps/*/Dockerfile`
  and `*.Dockerfile`);
- excludes only the documentation that legitimately lists the terms — the
  `docs/` and `csv/` trees and the root files `README.md`, `AGENTS.md`,
  `LICENSE` and `CHANGELOG.md`;
- is fail-closed: a forbidden term exits `1`, and an environment with no git
  work tree to enumerate exits `2`, never silently passing.

Its coverage rules are unit-tested by `scripts/test-boundary-scan.ps1`.
