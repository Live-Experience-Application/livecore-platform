// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Result of handling one normalized store notification through <see cref="StoreNotificationService.HandleAsync"/>
/// (CORE-STORE-005). It carries the <see cref="Outcome"/> (what handling the notification did, including the
/// idempotent <see cref="StoreNotificationProcessingOutcome.AlreadyProcessed"/> dedup path) so a caller — the
/// store notification endpoint — can acknowledge the provider appropriately without a second lookup.
/// </summary>
/// <param name="Outcome">What handling the notification did to the affected purchase.</param>
public sealed record StoreNotificationProcessingResult(StoreNotificationProcessingOutcome Outcome);
