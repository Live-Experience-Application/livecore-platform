namespace LiveCore.Api.Entitlements;

/// <summary>
/// Outcome of persisting a new <see cref="QuotaDefinition"/> through
/// <see cref="IQuotaDefinitionRepository.AddAsync"/> (CORE-ENTL-003). A quota definition measures at most one
/// entitlement, so a second quota for the same entitlement is reported rather than throwing, mirroring
/// <see cref="EntitlementDefinitionAddResult"/> and <see cref="SubjectEntitlementAddResult"/>.
/// </summary>
public enum QuotaDefinitionAddResult
{
    /// <summary>The quota definition was inserted.</summary>
    Added = 1,

    /// <summary>
    /// A quota definition for the same measured entitlement already exists (the unique
    /// <c>quota_definitions(entitlement_definition_id)</c> index rejected the insert), typically a lost create
    /// race. Nothing was inserted.
    /// </summary>
    DuplicateEntitlement = 2,
}
