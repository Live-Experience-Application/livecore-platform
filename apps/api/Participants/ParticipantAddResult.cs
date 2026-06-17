// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Participants;

/// <summary>
/// Outcome of persisting a new participant (CORE-SES-001).
/// </summary>
public enum ParticipantAddResult
{
    /// <summary>The participant was persisted.</summary>
    Added = 1,

    /// <summary>
    /// A participant linked to the same user already exists in the same
    /// workspace. The existing participant was not modified; the
    /// (workspace_id, user_id) pair is the per-workspace unique natural key for
    /// user-linked participants, so a second writer can never create a duplicate
    /// participant for one user in one workspace (enforced by the filtered unique
    /// database index). This result applies only to user-linked participants;
    /// anonymous participants (no user link) are not subject to this uniqueness,
    /// so a workspace may hold many of them (threat T5).
    /// </summary>
    DuplicateUserParticipant = 2,
}
