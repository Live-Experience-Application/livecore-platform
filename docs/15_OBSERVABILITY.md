# Observability

## Logging

Use structured logs.

Log IDs and event types, not sensitive content bodies.

Required log context:

```text
request_id
organization_id
workspace_id
session_id when applicable
user_id when applicable
event_id when applicable
```

## Metrics

Track:

- API request duration
- API error rate
- realtime connections
- reveal command latency
- event delivery failures
- asset upload/download failures
- database query failures
- background job failures

## Tracing

Add trace propagation later when multiple services are deployed.

## Health checks

Required:

```text
/health/live
/health/ready
```

Readiness checks database and critical dependencies.
