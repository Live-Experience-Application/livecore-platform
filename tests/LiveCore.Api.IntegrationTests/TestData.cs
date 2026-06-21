// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Recaps;
using LiveCore.Api.Scenes;
using LiveCore.Api.Sessions;
using LiveCore.Api.Templates;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Seeding helpers for the workspace API integration tests. They write directly
/// through the public aggregate factories and the public DbContext, so the test
/// arrangement uses the same domain invariants the production code does.
/// </summary>
internal static class TestData
{
    public static readonly DateTimeOffset SeedTime = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates and persists a user profile for a given OIDC identity. Pass
    /// <paramref name="displayName"/>/<paramref name="email"/> to seed the optional provider-mirrored personal
    /// data (both default to null, the no-metadata case) — used by the data-subject erasure tests to arrange a
    /// subject whose email/display name must be erased everywhere they were stored (CORE-PRIV-001).
    /// </summary>
    public static async Task<UserProfile> AddUserAsync(
        this LiveCoreDbContext context,
        string issuer,
        string subject,
        string? displayName = null,
        string? email = null)
    {
        var principal = new OidcPrincipal(PrincipalType.User, issuer, subject, displayName, email);
        var profile = UserProfile.CreateFromPrincipal(principal, SeedTime);
        context.UserProfiles.Add(profile);
        await context.SaveChangesAsync();
        return profile;
    }

    /// <summary>
    /// Creates and persists a Web Push subscription for a principal (CORE-PUSH-001), driving the real
    /// <see cref="PushSubscription.Register"/> aggregate factory so the seeded row has exactly the invariants
    /// production would produce. Used to arrange a subject's per-principal push subscription for the erasure
    /// (cascade-removed) and user-data export (disclosed) tests. The endpoint and keys are generic, neutral data.
    /// </summary>
    public static async Task<PushSubscription> AddPushSubscriptionAsync(
        this LiveCoreDbContext context,
        Guid userProfileId,
        string endpoint = "https://push.example.test/sub/seed",
        string p256dh = "seed-p256dh-key",
        string auth = "seed-auth-secret")
    {
        var subscription = PushSubscription.Register(userProfileId, endpoint, p256dh, auth, SeedTime);
        context.PushSubscriptions.Add(subscription);
        await context.SaveChangesAsync();
        return subscription;
    }

    /// <summary>Creates and persists an organization.</summary>
    public static async Task<Organization> AddOrganizationAsync(
        this LiveCoreDbContext context,
        string slug)
    {
        var organization = Organization.Create(slug, slug, SeedTime);
        context.Organizations.Add(organization);
        await context.SaveChangesAsync();
        return organization;
    }

    /// <summary>Creates and persists an organization membership.</summary>
    public static async Task<OrganizationMember> AddOrganizationMemberAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid userProfileId,
        MembershipRole role)
    {
        var member = OrganizationMember.Create(organizationId, userProfileId, role, SeedTime);
        context.OrganizationMembers.Add(member);
        await context.SaveChangesAsync();
        return member;
    }

    /// <summary>
    /// Creates and persists a workspace. The workspace is created Active by the real
    /// aggregate factory; when <paramref name="archived"/> is <see langword="true"/>
    /// it is then soft-archived through <see cref="Workspace.Archive"/>, so the seeded
    /// row has exactly the status the production archive transition would produce
    /// (CORE-LIFE-009).
    /// </summary>
    public static async Task<Workspace> AddWorkspaceAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        string slug,
        string name,
        bool archived = false)
    {
        var workspace = Workspace.Create(organizationId, slug, name, SeedTime);

        if (archived)
        {
            workspace.Archive(SeedTime);
        }

        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();
        return workspace;
    }

    /// <summary>Creates and persists a workspace membership.</summary>
    public static async Task<WorkspaceMember> AddWorkspaceMemberAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        MembershipRole role)
    {
        var member = WorkspaceMember.Create(organizationId, workspaceId, userProfileId, role, SeedTime);
        context.WorkspaceMembers.Add(member);
        await context.SaveChangesAsync();
        return member;
    }

    /// <summary>
    /// Creates and persists a workspace invitation through the real <see cref="WorkspaceInvitation.Create"/>
    /// aggregate factory and returns it together with its ONE-TIME plaintext token — the only place the
    /// plaintext exists, captured here so a redeem test can present the real token (CORE-WS-006). By default a
    /// Pending invitation with a far-future expiry (a decade), so it is redeemable at the real wall-clock the
    /// endpoint stamps; pass <paramref name="validity"/>/<paramref name="createdAt"/> to arrange a
    /// deterministically EXPIRED invitation (a past <paramref name="createdAt"/> with a short validity), or
    /// <paramref name="revoked"/> to arrange a revoked one. The invited email is generic data, never a
    /// credential (AGENTS.md; docs/adr/0005).
    /// </summary>
    public static async Task<(WorkspaceInvitation Invitation, string Token)> AddWorkspaceInvitationAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        MembershipRole role,
        string invitedEmail = "invitee@example.test",
        bool revoked = false,
        DateTimeOffset? createdAt = null,
        TimeSpan? validity = null)
    {
        var now = createdAt ?? SeedTime;
        var invitation = WorkspaceInvitation.Create(
            organizationId,
            workspaceId,
            invitedEmail,
            role,
            now,
            out var token,
            validity ?? TimeSpan.FromDays(3650));

        if (revoked)
        {
            invitation.Revoke(now);
        }

        context.WorkspaceInvitations.Add(invitation);
        await context.SaveChangesAsync();
        return (invitation, token);
    }

    /// <summary>
    /// Creates and persists a participant in the given workspace, optionally linked
    /// to a user (pass <paramref name="userProfileId"/> as <see langword="null"/> for
    /// an anonymous participant). The participant is created Active by the real
    /// aggregate factory; when <paramref name="removed"/> is <see langword="true"/>
    /// it is then soft-removed through <see cref="Participant.Remove"/>, so the seeded
    /// row has exactly the status the production transition would produce.
    /// </summary>
    public static async Task<Participant> AddParticipantAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid? userProfileId,
        string displayName = "Participant",
        bool removed = false)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId, displayName, SeedTime);

        if (removed)
        {
            participant.Remove(SeedTime);
        }

        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    /// <summary>
    /// Creates and persists a session in the given lifecycle status by driving the
    /// real aggregate state machine (Create, then Start/End/Cancel as required), so the
    /// seeded session has exactly the timestamps and status the production
    /// transitions would produce. A <see cref="SessionStatus.Prepared"/> session is
    /// just created; a <see cref="SessionStatus.Live"/> session is created and
    /// started; an <see cref="SessionStatus.Ended"/> session is created, started and
    /// ended; a <see cref="SessionStatus.Cancelled"/> session is created and then
    /// cancelled (from Prepared, the only legal source), so it never opened a live
    /// timeline (CORE-LIFE-010).
    /// </summary>
    public static async Task<Session> AddSessionAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        string title,
        SessionStatus status)
    {
        var session = Session.Create(organizationId, workspaceId, title, SeedTime);

        if (status is SessionStatus.Live or SessionStatus.Ended)
        {
            session.Start(SeedTime);
        }

        if (status is SessionStatus.Ended)
        {
            session.End(SeedTime);
        }

        if (status is SessionStatus.Cancelled)
        {
            session.Cancel(SeedTime);
        }

        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// Creates and persists a recap for the given session, driving the real <see cref="Recap"/> aggregate
    /// factory so the seeded row has exactly the invariants production would produce. By default a
    /// SYSTEM-produced recap (no user), exactly the kind the background recap-generation job writes for an
    /// ended session (CORE-RCP-001/002); pass <paramref name="generatedByUserProfileId"/> to seed a
    /// HOST-produced recap instead. The <paramref name="summary"/> is the generic, product-neutral recap body
    /// (host content; AGENTS.md). Used to arrange a session's generated recap for the recap read tests
    /// (CORE-RCP-003). The surrogate id is a UUIDv7 derived from <paramref name="generatedAt"/>, so seeding
    /// recaps with increasing times yields a deterministic produced order.
    /// </summary>
    public static async Task<Recap> AddRecapAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        string summary = "The session opened, three scenes were activated and two content blocks were revealed.",
        Guid? generatedByUserProfileId = null,
        DateTimeOffset? generatedAt = null)
    {
        var producedAt = generatedAt ?? SeedTime;
        var recap = generatedByUserProfileId is { } host
            ? Recap.GenerateByHost(organizationId, workspaceId, sessionId, host, summary, producedAt)
            : Recap.GenerateBySystem(organizationId, workspaceId, sessionId, summary, producedAt);
        context.Recaps.Add(recap);
        await context.SaveChangesAsync();
        return recap;
    }

    /// <summary>
    /// Creates and persists an export job in the given workspace, driving the real <see cref="ExportJob"/>
    /// aggregate factory and its guarded lifecycle transitions so the seeded row has exactly the invariants
    /// production would produce. By default a <see cref="ExportScope.Workspace"/> job driven all the way to
    /// <see cref="ExportJobStatus.Completed"/> (the kind the export read/download route serves, CORE-EXP-001);
    /// pass a different <paramref name="status"/> to seed a pending/running/failed job, or a different
    /// <paramref name="scope"/>. Used to arrange a workspace's export for the export read tests.
    /// </summary>
    public static async Task<ExportJob> AddExportJobAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid requestedByUserProfileId,
        ExportScope scope = ExportScope.Workspace,
        ExportJobStatus status = ExportJobStatus.Completed)
    {
        var job = ExportJob.Create(organizationId, workspaceId, requestedByUserProfileId, scope, SeedTime);

        if (status is ExportJobStatus.Running or ExportJobStatus.Completed)
        {
            job.Start(SeedTime);
        }

        if (status is ExportJobStatus.Completed)
        {
            job.Complete(SeedTime);
        }

        if (status is ExportJobStatus.Failed)
        {
            job.Fail("export processing failed", SeedTime);
        }

        context.ExportJobs.Add(job);
        await context.SaveChangesAsync();
        return job;
    }

    /// <summary>
    /// Produces and persists the workspace export manifest for a COMPLETED, workspace-scoped export job,
    /// driving the real <see cref="ExportManifest.ForWorkspaceExport"/> factory so the seeded artifact has
    /// exactly the invariants the worker (CORE-JOB-002) would produce. The optional <paramref name="inventory"/>
    /// is the per-kind resource count the export covered (a small generic default when omitted); it carries
    /// counts only, never any exported content (threats T7/T8). Used to arrange the artifact the export
    /// read/download route returns (CORE-EXP-001).
    /// </summary>
    public static async Task<ExportManifest> AddExportManifestAsync(
        this LiveCoreDbContext context,
        ExportJob completedJob,
        IReadOnlyDictionary<ExportResourceKind, int>? inventory = null)
    {
        var manifest = ExportManifest.ForWorkspaceExport(
            completedJob,
            inventory ?? new Dictionary<ExportResourceKind, int>
            {
                [ExportResourceKind.Session] = 2,
                [ExportResourceKind.Scene] = 1,
                [ExportResourceKind.ContentBlock] = 3,
            },
            SeedTime);

        context.ExportManifests.Add(manifest);
        await context.SaveChangesAsync();
        return manifest;
    }

    /// <summary>
    /// Creates and persists a scene in the given workspace at an explicit order, driving
    /// the real <see cref="Scene.Create"/> aggregate factory so the seeded row has exactly
    /// the invariants production would produce. Used to arrange a workspace's existing
    /// scenes (and their ordering) for the list and append-to-end tests.
    /// </summary>
    public static async Task<Scene> AddSceneAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        string title,
        int order)
    {
        var scene = Scene.Create(organizationId, workspaceId, title, order, SeedTime);
        context.Scenes.Add(scene);
        await context.SaveChangesAsync();
        return scene;
    }

    /// <summary>
    /// Creates and persists a content block in the given scene, driving the real
    /// <see cref="ContentBlock.Create"/> aggregate factory so the seeded row starts at the
    /// initial revision exactly as production would. Used to assert content-block creates
    /// did or did not happen.
    /// </summary>
    public static async Task<ContentBlock> AddContentBlockAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        ContentBlockType type,
        string body)
    {
        var contentBlock = ContentBlock.Create(organizationId, workspaceId, sceneId, type, body, SeedTime);
        context.ContentBlocks.Add(contentBlock);
        await context.SaveChangesAsync();
        return contentBlock;
    }

    /// <summary>
    /// Creates and persists an asset in the given workspace, driving the real <see cref="Asset.Create"/>
    /// aggregate factory so the seeded row has exactly the invariants production would produce. The asset
    /// starts <see cref="AssetStatus.Pending"/>; when <paramref name="available"/> is
    /// <see langword="true"/> it is then confirmed through <see cref="Asset.MarkAvailable"/> (recording a
    /// size and checksum), so the seeded row has exactly the status the production confirm transition would
    /// produce. Used to arrange a downloadable (or not-yet-downloadable) asset for the signed download
    /// tests. The storage coordinates are generic, tenant- and workspace-scoped naming (never client input).
    /// </summary>
    public static async Task<Asset> AddAssetAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid createdByUserProfileId,
        string contentType = "image/png",
        bool available = true)
    {
        var asset = Asset.Create(
            organizationId,
            workspaceId,
            createdByUserProfileId,
            "s3",
            "livecore-private-assets",
            $"assets/{organizationId}/{workspaceId}/{Guid.CreateVersion7()}",
            contentType,
            SeedTime);

        if (available)
        {
            asset.MarkAvailable(
                4096,
                "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
                SeedTime);
        }

        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        return asset;
    }

    /// <summary>
    /// Creates and persists a SESSION-SCOPED visibility rule for the given resource in the given session,
    /// driving the real <see cref="VisibilityRule.Create"/> aggregate factory (CORE-SVIS-001). Used to
    /// arrange a resource's existing visibility within a session (for example a Hidden rule the reveal
    /// command flips to Visible, or an audience-wide reveal made in one session that must not leak into a
    /// concurrent session). The <paramref name="sessionId"/> must reference a real session in the
    /// workspace (the session foreign key).
    /// </summary>
    public static async Task<VisibilityRule> AddVisibilityRuleAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        VisibilityState visibility)
    {
        var rule = VisibilityRule.Create(
            organizationId, workspaceId, sessionId, resourceType, resourceId, visibility, SeedTime);
        context.VisibilityRules.Add(rule);
        await context.SaveChangesAsync();
        return rule;
    }

    /// <summary>
    /// Creates and persists a SELECTED-participant, SESSION-SCOPED visibility rule for the given resource
    /// in the given session, driving the real <see cref="VisibilityRule.CreateForParticipant"/> aggregate
    /// factory (CORE-VIS-005 + CORE-SVIS-001). The rule's visibility applies ONLY to
    /// <paramref name="targetParticipantId"/> within <paramref name="sessionId"/> — a private reveal — so
    /// a non-selected participant must not see it. Used to arrange the selected-participant guarantee
    /// (the crown-jewel: one participant does not see another participant's private reveal).
    /// </summary>
    public static async Task<VisibilityRule> AddParticipantVisibilityRuleAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid targetParticipantId,
        VisibilityState visibility)
    {
        var rule = VisibilityRule.CreateForParticipant(
            organizationId,
            workspaceId,
            sessionId,
            resourceType,
            resourceId,
            targetParticipantId,
            visibility,
            SeedTime);
        context.VisibilityRules.Add(rule);
        await context.SaveChangesAsync();
        return rule;
    }

    /// <summary>
    /// Creates and persists an entity type in the given workspace, driving the real
    /// <see cref="EntityType.Create"/> aggregate factory. Used to arrange a type that an entity instance
    /// (and, through it, an asset link) can reference. The schema is a generic, well-formed JSON object.
    /// </summary>
    public static async Task<EntityType> AddEntityTypeAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        string typeKey = "generic-type")
    {
        var entityType = EntityType.Create(organizationId, workspaceId, typeKey, "Generic Type", "{}", SeedTime);
        context.EntityTypes.Add(entityType);
        await context.SaveChangesAsync();
        return entityType;
    }

    /// <summary>
    /// Creates and persists an entity instance in the given workspace, driving the real
    /// <see cref="Entity.Create"/> aggregate factory. Used to arrange a generic entity an asset can be
    /// linked to. The attribute values are a generic, well-formed JSON object.
    /// </summary>
    public static async Task<Entity> AddEntityAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid entityTypeId,
        string name = "Generic Entity")
    {
        var entity = Entity.Create(organizationId, workspaceId, entityTypeId, name, "{}", SeedTime);
        context.Entities.Add(entity);
        await context.SaveChangesAsync();
        return entity;
    }

    /// <summary>
    /// Creates and persists a directed entity relationship edge between two entities in the given
    /// workspace, driving the real <see cref="EntityRelationship.Create"/> aggregate factory. Used to
    /// arrange an edge the removal endpoint can delete. The kind is a generic, neutral slug (AGENTS.md).
    /// </summary>
    public static async Task<EntityRelationship> AddEntityRelationshipAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid sourceEntityId,
        Guid targetEntityId,
        string kind = "links-to")
    {
        var relationship = EntityRelationship.Create(
            organizationId, workspaceId, sourceEntityId, targetEntityId, kind, SeedTime);
        context.EntityRelationships.Add(relationship);
        await context.SaveChangesAsync();
        return relationship;
    }

    /// <summary>
    /// Creates and persists an asset link attaching the given asset to the given target (a content block
    /// or entity) in the given workspace, driving the real <see cref="AssetLink.Create"/> aggregate
    /// factory. Used to arrange an asset's audience-visibility linkage for the download-authorization
    /// tests.
    /// </summary>
    public static async Task<AssetLink> AddAssetLinkAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid assetId,
        AssetLinkTargetType targetType,
        Guid targetId,
        Guid createdByUserProfileId)
    {
        var link = AssetLink.Create(
            organizationId, workspaceId, assetId, targetType, targetId, createdByUserProfileId, SeedTime);
        context.AssetLinks.Add(link);
        await context.SaveChangesAsync();
        return link;
    }

    /// <summary>
    /// A generic, well-formed, NEUTRAL template definition document (the template boundary, docs/04): a
    /// top-level <c>templateKey</c> plus a non-empty <c>entityTypes</c> array of valid entries, exactly
    /// the minimal structure <c>TemplateDefinitionValidator</c> requires. Used to seed templates for the
    /// deletion tests.
    /// </summary>
    private const string _templateDefinition = """
        {
          "templateKey": "sample.template",
          "entityTypes": [
            { "key": "type-alpha", "displayName": "Type Alpha", "attributeSchema": { "fields": [] } }
          ]
        }
        """;

    /// <summary>
    /// Creates and persists an ORGANIZATION-scoped template owned by the given organization, driving the
    /// real <see cref="Template.CreateForOrganization"/> aggregate factory so the seeded row has exactly
    /// the invariants production would produce. Used to arrange a deletable org template. The key and
    /// definition are generic, neutral data (AGENTS.md; the template boundary, docs/04).
    /// </summary>
    public static async Task<Template> AddOrganizationTemplateAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        string templateKey = "sample.template",
        int version = 1)
    {
        var template = Template.CreateForOrganization(organizationId, templateKey, version, _templateDefinition, SeedTime);
        context.Templates.Add(template);
        await context.SaveChangesAsync();
        return template;
    }

    /// <summary>
    /// Creates and persists a GLOBAL template (available to every organization, <c>organization_id IS
    /// NULL</c>), driving the real <see cref="Template.CreateGlobal"/> aggregate factory. Used to arrange
    /// the "global templates cannot be deleted by an org" negative: a global template addressed through an
    /// org path is a hidden 404 and is left intact.
    /// </summary>
    public static async Task<Template> AddGlobalTemplateAsync(
        this LiveCoreDbContext context,
        string templateKey = "sample.template",
        int version = 1)
    {
        var template = Template.CreateGlobal(templateKey, version, _templateDefinition, SeedTime);
        context.Templates.Add(template);
        await context.SaveChangesAsync();
        return template;
    }

    /// <summary>
    /// Creates and persists a generic append-only audit log entry for the given tenant, driving the real
    /// <see cref="AuditLogEntry.Create"/> factory AND the real <see cref="AuditLogRepository.AppendAsync"/>
    /// append path so the seeded row has exactly the invariants production would produce — including being
    /// sealed into the tenant's tamper-evident hash chain (CORE-SEC-003: a per-tenant append sequence plus the
    /// previous-hash/entry-hash link, allocated from <c>audit_log_sequences</c>). Seeding through the repository
    /// (rather than a bare <c>context.AuditLogs.Add</c>) is what keeps each tenant's seeded entries on a single,
    /// gap-free chain instead of colliding on the unique <c>audit_logs(organization_id, sequence)</c> index.
    /// Used to arrange a tenant's audit trail for the view-audit-log read tests (CORE-SEC-002). The surrogate id
    /// is a UUIDv7 derived from <paramref name="createdAt"/>, so seeding entries with increasing times yields a
    /// deterministic chronological read order. Every value is a generic identifier/enum/state name — never PII or
    /// content (threat T7; AGENTS.md).
    /// </summary>
    public static async Task<AuditLogEntry> AddAuditLogEntryAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        AuditAction action = AuditAction.SessionStarted,
        Guid? workspaceId = null,
        Guid? actorUserProfileId = null,
        DateTimeOffset? createdAt = null)
    {
        var entry = AuditLogEntry.Create(
            organizationId,
            workspaceId ?? Guid.CreateVersion7(),
            action,
            actorUserProfileId ?? Guid.CreateVersion7(),
            resourceType: null,
            resourceId: null,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt ?? SeedTime);
        await new AuditLogRepository(context).AppendAsync(entry, CancellationToken.None);
        return entry;
    }

    /// <summary>
    /// Creates and persists a generic numeric quota entitlement definition, driving the real
    /// <see cref="EntitlementDefinition.Define"/> factory. Used to arrange the catalog a quota definition
    /// measures and a subject entitlement grants a limit for. The key is generic Core vocabulary (AGENTS.md).
    /// </summary>
    public static async Task<EntitlementDefinition> AddQuotaEntitlementDefinitionAsync(
        this LiveCoreDbContext context,
        string key)
    {
        var definition = EntitlementDefinition.Define(
            key, EntitlementValueKind.Quota, "Quota entitlement", null, SeedTime);
        context.EntitlementDefinitions.Add(definition);
        await context.SaveChangesAsync();
        return definition;
    }

    /// <summary>
    /// Creates and persists a quota definition measuring the given quota entitlement for the given subject kind
    /// and unit, driving the real <see cref="QuotaDefinition.Define"/> factory.
    /// </summary>
    public static async Task<QuotaDefinition> AddQuotaDefinitionAsync(
        this LiveCoreDbContext context,
        EntitlementDefinition entitlement,
        EntitlementSubjectType subjectType,
        QuotaUnit unit = QuotaUnit.Count)
    {
        var definition = QuotaDefinition.Define(entitlement, subjectType, unit, SeedTime);
        context.QuotaDefinitions.Add(definition);
        await context.SaveChangesAsync();
        return definition;
    }

    /// <summary>
    /// Creates and persists a quota entitlement assignment granting a subject a numeric limit (or null for an
    /// unlimited/fair-use grant), driving the real <see cref="SubjectEntitlement.GrantQuota"/> factory.
    /// </summary>
    public static async Task<SubjectEntitlement> AddSubjectQuotaEntitlementAsync(
        this LiveCoreDbContext context,
        EntitlementSubjectType subjectType,
        Guid subjectId,
        EntitlementDefinition entitlement,
        long? limit)
    {
        var assignment = SubjectEntitlement.GrantQuota(
            subjectType, subjectId, entitlement, limit, sourcePlanDefinitionId: null, SeedTime);
        context.SubjectEntitlements.Add(assignment);
        await context.SaveChangesAsync();
        return assignment;
    }

    /// <summary>
    /// Creates and persists a generic boolean flag entitlement definition, driving the real
    /// <see cref="EntitlementDefinition.Define"/> factory. Used to arrange the catalog a subject flag entitlement
    /// grants (e.g. <c>ads.disabled</c>). The key is generic Core vocabulary (AGENTS.md).
    /// </summary>
    public static async Task<EntitlementDefinition> AddFlagEntitlementDefinitionAsync(
        this LiveCoreDbContext context,
        string key)
    {
        var definition = EntitlementDefinition.Define(
            key, EntitlementValueKind.Flag, "Flag entitlement", null, SeedTime);
        context.EntitlementDefinitions.Add(definition);
        await context.SaveChangesAsync();
        return definition;
    }

    /// <summary>
    /// Creates and persists a flag entitlement assignment granting a subject a boolean capability at the given
    /// value, driving the real <see cref="SubjectEntitlement.GrantFlag"/> factory.
    /// </summary>
    public static async Task<SubjectEntitlement> AddSubjectFlagEntitlementAsync(
        this LiveCoreDbContext context,
        EntitlementSubjectType subjectType,
        Guid subjectId,
        EntitlementDefinition entitlement,
        bool value)
    {
        var assignment = SubjectEntitlement.GrantFlag(
            subjectType, subjectId, entitlement, value, sourcePlanDefinitionId: null, SeedTime);
        context.SubjectEntitlements.Add(assignment);
        await context.SaveChangesAsync();
        return assignment;
    }

    /// <summary>
    /// Creates and persists a subject's recorded usage of a quota at the given amount, driving the real
    /// <see cref="QuotaUsage.Start"/> factory and <see cref="QuotaUsage.Record"/> transition so the seeded row
    /// has exactly the amount production would record.
    /// </summary>
    public static async Task<QuotaUsage> AddQuotaUsageAsync(
        this LiveCoreDbContext context,
        EntitlementSubjectType subjectType,
        Guid subjectId,
        QuotaDefinition definition,
        long usedAmount)
    {
        var usage = QuotaUsage.Start(subjectType, subjectId, definition, SeedTime);
        if (usedAmount != 0)
        {
            usage.Record(usedAmount, SeedTime);
        }

        context.QuotaUsage.Add(usage);
        await context.SaveChangesAsync();
        return usage;
    }
}
