using Afterglow.Core.Hardware;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Profiles;

/// <summary>
/// The card-side half of "save what this card runs now as a profile", shared by
/// every surface that does it: the Profiles page and MCP's save_profile.
/// <para>
/// It lived inline in the Profiles page, and MCP's twin read only ReadCurrent's
/// scalars (R-35): a curve an agent had shaped was saved with no points and with
/// <see cref="TuningProfile.CapturedVfPoints"/> false, so after a reset the saved
/// profile could not put it back. One function means the two saves cannot drift
/// apart again.
/// </para>
/// </summary>
public static class ProfileCapture
{
    /// <summary>
    /// Returns <paramref name="profile"/> stamped with <paramref name="gpu"/>'s
    /// identity and carrying its per-point V/F table. Both come from the same
    /// card, so the table can never be filed under another card's UUID.
    /// <list type="bullet">
    /// <item>Identity: the card's UUID (the apply engine's cross-card guard) and
    /// display name. A null card — none selected — leaves the profile unstamped.</item>
    /// <item>Table: on a card whose driver answered the live probe, the whole live
    /// table as <see cref="VfPointPlanner.CaptureForProfile"/> records it — explicit
    /// zeros included, keyed by raw table slot (F-02/F-07).
    /// <see cref="TuningProfile.CapturedVfPoints"/> is set only when the table was
    /// actually read: then "no points" is a fact about this card and applying the
    /// profile may remove a curve. A card without per-point control, or a read that
    /// failed, leaves it false, and the profile has no opinion on the curve.</item>
    /// </list>
    /// </summary>
    public static TuningProfile CapturedFrom(this TuningProfile profile, GpuContext? gpu)
    {
        ArgumentNullException.ThrowIfNull(profile);

        IReadOnlyDictionary<int, int>? vfPoints = null;
        bool capturedVfPoints = false;
        if (gpu is { Tuner.Capabilities.SupportsVfPoints: true } &&
            gpu.Tuner.TryReadVfPoints(out var table) == NvapiStatus.Ok)
        {
            // Compared against the core offset the driver reports now, not the
            // profile's: a table holding only that offset needs no points, since
            // the core-offset write reproduces it.
            vfPoints = VfPointPlanner.CaptureForProfile(table, gpu.Tuner.ReadCurrent().CoreOffsetMHz);
            capturedVfPoints = true;
        }

        return profile with
        {
            GpuUuid = gpu?.Uuid,
            GpuName = gpu?.Name,
            VfPointOffsetsMHz = vfPoints,
            CapturedVfPoints = capturedVfPoints,
        };
    }
}
