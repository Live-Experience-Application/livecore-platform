# LiveCore — example observability assets (alerting, SLOs, dashboard)

Starter Prometheus rules, a scrape configuration and a Grafana dashboard
(CORE-OBS-008) so a self-hoster gets actionable alerting and SLO tracking out of
the box, instead of the raw `livecore_*` metrics with no thresholds. They build
directly on the metrics the API and worker export on `/metrics` (CORE-OBS-001 /
CORE-OBS-007), documented in
[`docs/15_OBSERVABILITY.md`](../../docs/15_OBSERVABILITY.md).

These are **examples** to copy and tune — not a managed monitoring stack. The
SLO targets and `for:` windows below are sensible starting points; adjust them to
your traffic and reliability goals.

## What ships here

| File                                        | Purpose                                                                                                   |
| ------------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| `prometheus/prometheus.yml`                 | Example scrape config: scrapes the API (`:8080`) and worker (`:9464`) `/metrics`, loads the rules.        |
| `prometheus/rules/livecore.rules.yml`       | Recording rules (SLO burn series) and alert rules over the `livecore_*` metrics.                          |
| `grafana/dashboards/livecore-overview.json` | Starter Grafana dashboard: API SLOs, auth/rate-limit signals, dependency failures and worker-loop health. |

## Scrape guidance

The API and worker each expose a Prometheus scrape endpoint at `GET /metrics`
(the worker on its configurable metrics URL, default port `9464` — see
[`docs/13_SELF_HOSTING_REQUIREMENTS.md`](../../docs/13_SELF_HOSTING_REQUIREMENTS.md),
"Worker metrics and per-loop liveness"). Both are **unauthenticated by
convention** and meant to be scraped from inside the deployment network; restrict
them at the reverse-proxy/network edge. They carry only low-cardinality aggregate
series — no tenant, principal or resource detail (threat T7).

Point Prometheus at the example config:

```bash
prometheus --config.file=deploy/observability/prometheus/prometheus.yml
```

Or copy the two `scrape_configs` jobs and the `rule_files` entry into an existing
Prometheus. The example targets (`api:8080`, `worker:9464`) match the in-repo
Docker Compose service names and ports
([`deploy/compose/docker-compose.yml`](../compose/docker-compose.yml)); change
them to your own hosts.

### Worker loop label (`exported_job`)

The worker tags its `livecore_job_*` series with a low-cardinality `job`
attribute naming the loop (`asset-cleanup`, `recap-generation`,
`export-processing`, `store-notification-reconciliation`, `data-retention`). When
Prometheus scrapes, **its own target `job` label (`livecore-worker`) wins** and
the exposed loop label is renamed to **`exported_job`** (the default
`honor_labels: false`). The worker rules and dashboard panels group by
`exported_job` for that reason. If you prefer the loop name to stay on `job`, set
`honor_labels: true` on the `livecore-worker` scrape job (you then lose the
`job="livecore-worker"` distinction).

## SLO targets

The recording rules pre-compute the burn series the alerts and the dashboard
read, so each target is expressed once. The full table — objective, the
`livecore_*` series it is computed from, and the target — is the single source of
truth in
[`docs/15_OBSERVABILITY.md`](../../docs/15_OBSERVABILITY.md) ("Example alert
rules, SLO targets and a starter dashboard"). In summary:

| Objective                   | Series                                             | Starter target          |
| --------------------------- | -------------------------------------------------- | ----------------------- |
| API availability            | `job:livecore_api_request_error_ratio:rate5m`      | 5xx ratio < 1%          |
| API latency                 | `job:livecore_api_request_duration_seconds:p95_5m` | p95 < 1s                |
| Reveal latency              | `job:livecore_reveal_duration_seconds:p95_5m`      | p95 < 1s                |
| Auth-failure rate           | `job:livecore_api_auth_failures:rate5m`            | < 1/s sustained         |
| Rate-limit rejection rate   | `job:livecore_api_rate_limit_rejections:rate5m`    | < 1/s sustained         |
| Worker job failures         | `livecore_job_failures_total`                      | none sustained          |
| Worker loop success ratio   | `exported_job:livecore_job_success_ratio:rate15m`  | >= 90%                  |
| Worker loop backlog healthy | `livecore_job_backlog` (batch-capped)              | drains; peak not rising |

A stalled worker loop (one that hangs rather than fails) is detected by the
worker's per-loop `/health/live` heartbeat (CORE-DR-003), not by these metric
alerts; wire both.

> **Worker backlog is batch-capped.** `livecore_job_backlog` is the count of
> pending items a sweep examined, bounded by each loop's `BatchSize`, so it
> saturates at the batch size and under-reports a larger true backlog (CORE-OBS-015,
> [`docs/15`](../../docs/15_OBSERVABILITY.md)). Two complementary alerts cover it:
> `LiveCoreWorkerBacklogNotDraining` (the backlog never returns to zero) and
> `LiveCoreWorkerBacklogGrowing` (its hour-over-hour peak is rising — robust to the
> gauge touching zero between sweeps, which silenced the old floor-based test).

## Using the dashboard

Import `grafana/dashboards/livecore-overview.json` in Grafana
(**Dashboards → New → Import**) and select your Prometheus data source for the
`DS_PROMETHEUS` input. The dashboard reads the recording rules above, so load the
rules into Prometheus first (otherwise the SLO panels show no data until the
rules evaluate).

## Validation in CI

The assets are validated on every change (CORE-OBS-008):

- `promtool check config` validates the scrape config and `promtool check rules`
  validates the recording/alert rules;
- `promtool test rules prometheus/rules/livecore.rules.test.yml` unit-tests the
  alert expressions on synthetic series — notably that the worker backlog alert
  fires on a steadily-growing backlog that touches zero (where the old floor test
  false-passed) and not on a draining backlog (CORE-OBS-015);
- `scripts/test-observability-assets.ps1` lints the dashboard JSON and asserts
  every `livecore_*` series referenced by the rules and the dashboard is a series
  documented in `docs/15_OBSERVABILITY.md` or a recording rule defined here, so
  the rules, the dashboard and the docs cannot silently drift.
