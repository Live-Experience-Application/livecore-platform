# Forbidden Core Terms

Core source code must not contain vertical terms.

See `csv/forbidden_core_terms.csv`.

## How to handle false positives

Some terms can appear in documentation to explain boundaries. Source code, API routes, DTOs, database tables and event names must avoid them.

## Recommended CI check

Add a script that scans source folders only:

```text
apps/
packages/
tests/
```

Exclude:

```text
docs/19_FORBIDDEN_CORE_TERMS.md
csv/forbidden_core_terms.csv
```

Fail CI on forbidden terms.
