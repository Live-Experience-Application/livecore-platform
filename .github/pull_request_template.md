<!--
Thanks for contributing to the LiveCore Core Platform.
Please read CONTRIBUTING.md before opening this pull request.
-->

## Summary

<!-- What does this change do, and why? Reference the story id (e.g. CORE-XXX-001). -->

## Contributor checklist

- [ ] **DCO sign-off**: every commit is signed off (`git commit -s`) with a
      `Signed-off-by` trailer whose email matches the author (see
      [CONTRIBUTING.md](../CONTRIBUTING.md) and
      [`DEVELOPER_CERTIFICATE_OF_ORIGIN`](../DEVELOPER_CERTIFICATE_OF_ORIGIN)).
- [ ] **SPDX headers**: new first-party `.cs` / `.ts` / `.tsx` source files carry
      the `// SPDX-License-Identifier: AGPL-3.0-or-later` + copyright header
      (`pwsh -NoProfile -File scripts/lint-license-headers.ps1 -Fix` adds any
      missing ones).
- [ ] **Product neutrality**: no forbidden vertical or brand terms in Core source
      (see [AGENTS.md](../AGENTS.md)); the boundary scan passes.
- [ ] **Tests**: new behavior is covered, including negative authorization tests
      where the change is security-relevant; the full CI is green.
- [ ] **Docs**: documentation and the SPEC csvs are updated when contracts,
      events, schema or routes change.
