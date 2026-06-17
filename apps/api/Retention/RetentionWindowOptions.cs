namespace LiveCore.Api.Retention;

/// <summary>
/// The deployment policy for ONE data-retention family (CORE-PRIV-003): whether the family's purge is
/// <see cref="Enabled"/> and, if so, the <see cref="RetentionWindow"/> after which a terminal/old record of that
/// family is eligible to be purged. The four families — completed/expired sessions (and their cascade-removed
/// session events, recaps and visibility rules), generated recaps, completed export artifacts and
/// closed/expired/revoked invitations — each carry their own instance of this policy, so a deployment tunes (or
/// disables) each independently.
///
/// <para>
/// DISABLED-BY-DEFAULT WHERE DELETION WOULD BE SURPRISING (the acceptance criterion). A purge that removes data
/// an operator would be surprised to lose — session history, recap host content, a completed export a user may
/// still re-download — defaults to <see cref="Enabled"/> = <see langword="false"/>, so it never runs until the
/// deployment opts in. A purge that is clearly expected privacy hygiene — a terminal invitation row whose only
/// payload is a now-dead plaintext invited email, with its lifecycle audit facts preserved separately —
/// defaults to enabled. The defaults live on <see cref="DataRetentionOptions"/>; this type only validates the
/// window.
/// </para>
/// </summary>
public sealed class RetentionWindowOptions
{
    /// <summary>
    /// Creates a retention-window policy, validating that the window is sane. A non-positive window is rejected
    /// so a misconfiguration can never collapse the grace period to zero and purge records the instant they
    /// become terminal. The window is always validated even when <paramref name="enabled"/> is
    /// <see langword="false"/>, so enabling a family later cannot surface a latent bad value.
    /// </summary>
    /// <param name="enabled">Whether this family's purge runs at all.</param>
    /// <param name="retentionWindow">How long a terminal/old record is retained before it is eligible to be purged.</param>
    /// <exception cref="ArgumentOutOfRangeException">The retention window is not positive.</exception>
    public RetentionWindowOptions(bool enabled, TimeSpan retentionWindow)
    {
        if (retentionWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionWindow), retentionWindow, "The retention window must be positive.");
        }

        Enabled = enabled;
        RetentionWindow = retentionWindow;
    }

    /// <summary>Whether this family's retention purge runs. When <see langword="false"/> the sweep skips the family entirely.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// How long a record of this family is retained, measured from its CREATION time (its age), before it is
    /// eligible to be purged. A record younger than this is always retained, so a recently-finished record is
    /// never reclaimed.
    /// </summary>
    public TimeSpan RetentionWindow { get; }
}
