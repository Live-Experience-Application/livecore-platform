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
