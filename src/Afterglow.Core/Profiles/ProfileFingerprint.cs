using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Afterglow.Core.Fans;

namespace Afterglow.Core.Profiles;

/// <summary>
/// A content fingerprint of the configuration a profile puts on the card (R-03).
/// <para>
/// Certification and the stable mark are claims about one configuration, but
/// the certifier used to find its subject again by NAME: a profile saved under
/// the same name while the run burned — new sliders, never applied — was the
/// one it stamped and marked stable, and startup auto-applies a stable profile.
/// A certification also counted for any profile with the same two offsets, so
/// removing the clock lock, moving the power limit or re-stamping the profile
/// for another card kept an approval nothing had tested.
/// </para>
/// <para>
/// Covered: everything an apply writes — the card identity, offsets, power and
/// temperature limits, clock lock, voltage boost, the per-point V/F table and
/// whether the profile claims to have read it — and the fan policy, since
/// certification runs under it (R-11). Not covered: the name, notes,
/// timestamps, the stable mark and the certifications themselves, and any fan
/// value the selected fan mode does not use. Changing one of those is not a
/// different configuration.
/// </para>
/// </summary>
public static class ProfileFingerprint
{
    /// <summary>Bumped if the canonical form ever changes, so old and new fingerprints never compare equal by accident.</summary>
    private const string Version = "afterglow-tuning-v1";

    /// <summary>The fingerprint: 128 bits of SHA-256 over the canonical form, as lowercase hex.</summary>
    public static string TuningFingerprint(this TuningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(profile)));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>The fingerprinted text, one knob per line, invariant culture. Internal so tests can read it.</summary>
    internal static string Canonical(TuningProfile profile)
    {
        var text = new StringBuilder();
        void Line(string key, string value) => text.Append(key).Append('=').Append(value).Append('\n');

        Line("v", Version);
        Line("gpu", profile.GpuUuid?.ToUpperInvariant() ?? "-");
        Line("core", Num(profile.CoreOffsetMHz));
        Line("mem", Num(profile.MemOffsetMHz));
        Line("power", profile.PowerLimitW is double w ? Num(w) : "-");
        Line("temp", profile.TempLimitC is uint t ? Num(t) : "-");
        Line("lock", profile.LockedCoreClockMHz is uint l ? Num(l) : "-");
        Line("boost", profile.VoltageBoostPct is uint b ? Num(b) : "-");
        Line("vfcaptured", profile.CapturedVfPoints ? "1" : "0");
        Line("vf", profile.VfPointOffsetsMHz is { Count: > 0 } points
            ? string.Join(';', points.OrderBy(p => p.Key).Select(p => $"{Num(p.Key)}:{Num(p.Value)}"))
            : "-");
        Line("fan", profile.FanMode switch
        {
            FanMode.Fixed => $"fixed:{Num(profile.FixedFanPct)}",
            FanMode.Curve => profile.FanCurve is { } curve ? $"curve:{Curve(curve)}" : "curve:none",
            _ => "auto",
        });
        return text.ToString();
    }

    private static string Curve(FanCurveConfig curve) =>
        string.Join('|',
            curve.TempSource.ToString(),
            Num(curve.HysteresisC),
            Num(curve.ZeroRpmBelowC),
            Num(curve.ZeroRpmHysteresisC),
            Num(curve.MaxStepPerTick),
            Num(curve.MinSpinDutyPct),
            string.Join(';', curve.Points.Select(p => $"{Num(p.TempC)}:{Num(p.DutyPct)}")));

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Num(uint value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Round-trip form, so two values that serialize differently never collide.</summary>
    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
