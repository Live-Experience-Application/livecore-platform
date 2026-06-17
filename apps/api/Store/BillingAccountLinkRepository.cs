// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Store;

/// <summary>
/// EF Core implementation of <see cref="IBillingAccountLinkRepository"/> (CORE-MON-002), backed by the
/// <c>billing_account_links</c> table mapped in <see cref="BillingAccountLinkConfiguration"/>. The unique
/// <c>billing_account_links(purchase_transaction_id)</c> index is the one-subject-per-receipt guarantee; a
/// duplicate insert is surfaced as a result rather than an exception, mirroring
/// <see cref="PurchaseTransactionRepository"/>.
/// </summary>
internal sealed class BillingAccountLinkRepository : IBillingAccountLinkRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public BillingAccountLinkRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<BillingAccountLinkAddResult> AddAsync(
        BillingAccountLink link,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        _dbContext.BillingAccountLinks.Add(link);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return BillingAccountLinkAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the same
            // scope.
            _dbContext.Entry(link).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a link for the same purchase already exists, the unique
            // purchase_transaction_id index rejected the insert (a client retry, a replayed proof, or a concurrent
            // claim by another subject). Any other failure (for example a purchase_transaction_id foreign key that
            // does not exist) is rethrown unchanged.
            var duplicateExists = await _dbContext.BillingAccountLinks
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.PurchaseTransactionId == link.PurchaseTransactionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return BillingAccountLinkAddResult.DuplicatePurchase;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<BillingAccountLink?> FindByPurchaseTransactionAsync(
        Guid purchaseTransactionId,
        CancellationToken cancellationToken)
    {
        // An empty id can never address a stored link, so the lookup fails fast instead of matching an arbitrary
        // row.
        if (purchaseTransactionId == Guid.Empty)
        {
            throw new ArgumentException("Purchase transaction id must not be empty.", nameof(purchaseTransactionId));
        }

        return await _dbContext.BillingAccountLinks
            .FirstOrDefaultAsync(
                link => link.PurchaseTransactionId == purchaseTransactionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkedSubjectPurchase>> ListLinkedPurchasesBySubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        // An empty id can never address a stored subject, so the lookup fails fast instead of matching arbitrary
        // rows (mirrors FindByPurchaseTransactionAsync).
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        // Join each of the subject's links to the purchase it binds and project only the three facts the retention
        // decision needs. Filtered by the (subject_type, subject_id) pair — served by the
        // ix_billing_account_links_subject_type_subject_id lookup index — so one subject's purchases are never read
        // through another subject's id. The "is this purchase still active?" decision stays out of SQL (it belongs
        // to PurchaseTransactionStatusMachine.IsRevoked, the single source of truth) and is applied by the caller.
        var rows = await (
            from link in _dbContext.BillingAccountLinks.AsNoTracking()
            join transaction in _dbContext.PurchaseTransactions.AsNoTracking()
                on link.PurchaseTransactionId equals transaction.Id
            where link.SubjectType == subjectType && link.SubjectId == subjectId
            select new
            {
                transaction.Id,
                transaction.ProductReference,
                transaction.Status,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new LinkedSubjectPurchase(row.Id, row.ProductReference, row.Status))
            .ToList();
    }
}
