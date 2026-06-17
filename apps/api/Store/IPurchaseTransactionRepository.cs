// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Persistence contract for the verified purchase transaction aggregate (CORE-STORE-002). The Store module owns
/// the <c>purchase_transactions</c> table; other modules access purchase transactions only through this contract
/// or the module's application services (docs/02_ARCHITECTURE.md: module boundaries).
///
/// A purchase is addressed by its idempotency key — the (<see cref="PurchaseProvider"/>, provider transaction
/// id) pair — so the lookup is by that pair, never a list-everything read of all purchases.
/// </summary>
public interface IPurchaseTransactionRepository
{
    /// <summary>
    /// Persists a new purchase transaction. A verified purchase is recorded at most once, so an insert that
    /// duplicates an existing (provider, provider transaction id) is reported as
    /// <see cref="PurchaseTransactionAddResult.DuplicateTransaction"/> (the idempotency guarantee); any other
    /// failure surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<PurchaseTransactionAddResult> AddAsync(PurchaseTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Persists changes to an already-tracked transaction (a status change).</summary>
    Task UpdateAsync(PurchaseTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the purchase transaction for exactly the given (provider, provider transaction id) pair, or
    /// <see langword="null"/> when none exists. This is the idempotency lookup: it recognizes a purchase that was
    /// already recorded.
    /// </summary>
    /// <exception cref="System.ArgumentException">The provider transaction id is blank.</exception>
    Task<PurchaseTransaction?> FindByProviderTransactionAsync(
        PurchaseProvider provider,
        string providerTransactionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the purchase transaction with exactly the given surrogate id, or <see langword="null"/> when none
    /// exists. Used by the Idempotency-Key replay path (CORE-DX-004): a verification route records the recorded
    /// transaction's id against the client key, so a retry under the same key re-loads the original transaction
    /// by that id and returns the original result WITHOUT re-running the external verifier. A purchase is named
    /// globally (no tenant), so the lookup is by id alone; per-subject isolation is enforced by the buyer-scoped
    /// idempotency scope, not by this read.
    /// </summary>
    /// <exception cref="System.ArgumentException">The id is empty.</exception>
    Task<PurchaseTransaction?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
}
