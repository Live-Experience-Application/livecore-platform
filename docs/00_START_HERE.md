# Start Here - livecore-platform

This repository implements the generic Core Platform.

The Core is a reusable Live Experience Engine. It provides the underlying platform for multiple vertical products.

## Core purpose

A Host can prepare a Workspace, create Sessions, organize Scenes, manage Participants, define ContentBlocks and Entities, apply VisibilityRules, execute Reveals, stream SessionEvents and produce Recaps.

## Vertical products built on Core

```text
ArcanOS -> Pen-and-Paper / DnD / tabletop roleplaying
ScenarioOS -> Enterprise training / simulation / workshops
```

## Dependency rule

```text
Core depends on no vertical.
Verticals depend on Core.
```

## First implementation milestone

The first milestone is not product features. It is a production-quality foundation:

- repository skeleton
- format/lint/test setup
- health endpoint
- database connection
- migration system
- OIDC validation skeleton
- boundary scan
- Dockerfile
- CI

Do not implement ArcanOS or ScenarioOS features here.
