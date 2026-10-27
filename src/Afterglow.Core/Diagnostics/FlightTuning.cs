using System.Globalization;
using System.Text;

namespace Afterglow.Core.Diagnostics;

/// <summary>
/// The tuning one card was running, as its flight recording states it. Written
/// as the existing <c>offsets</c> marker with extra fields, so every reader keeps
/// working: an older Afterglow reads only <c>core=</c> and <c>mem=</c> and skips
/// the rest, and this one reads a recording written before the extra fields
/// existed as "offsets known, lock and V/F points not recorded".
/// <code>
/// offsets core=100 mem=500 lock=none vf=0 gpu=GPU-…            (no lock, no per-point edits)
/// offsets core=0 mem=0 lock=1500 vf=3 vfmax=150 gpu=GPU-…      (a lock and three edited points)
/// offsets core=100 mem=500                                      (pre-R-43 recording)
/// </code>
/// </summary>
public sealed record FlightTuning
{
    public int CoreOffsetMHz { get; init; }

    public int MemOffsetMHz { get; init; }

    /// <summary>
    /// True when the record says whether a clock lock was applied. False for
    /// recordings written before locks were recorded — then a null
    /// <see cref="LockedCoreClockMHz"/> means "unknown", not "none".
    /// </summary>
    public bool LockRecorded { get; init; }

    /// <summary>The clock lock Afterglow had applied; null = none (when <see cref="LockRecorded"/>).</summary>
    public uint? LockedCoreClockMHz { get; init; }

    /// <summary>
    /// How many V/F points carried an offset different from the global core
    /// offset (the rule a saved profile captures by); 0 = none. Null = not
    /// recorded: the table could not be read, or the recording predates it.
    /// </summary>
    public int? VfPointsEdited { get; init; }

    /// <summary>The highest offset among the edited points; null when none were edited.</summary>
    public int? VfPointMaxOffsetMHz { get; init; }

    /// <summary>
    /// The card the state was read from (its UUID or stable key). The marker sits
    /// in that card's own file already; the identity is what lets a report say
    /// which card it describes even after cards are added, removed or
    /// re-enumerated between sessions.
    /// </summary>
    public string? GpuId { get; init; }

    /// <summary>True when the record states the lock and V/F-point state, not just the offsets.</summary>
    public bool FullyRecorded => LockRecorded && VfPointsEdited is not null;

    /// <summary>True when any recorded knob differs from stock.</summary>
    public bool AnyApplied =>
        CoreOffsetMHz != 0 || MemOffsetMHz != 0 || LockedCoreClockMHz is not null || VfPointsEdited > 0;

    /// <summary>The marker text (without the timestamp prefix).</summary>
    public string ToMarkerText()
    {
        var sb = new StringBuilder("offsets ");
        sb.Append(CultureInfo.InvariantCulture, $"core={CoreOffsetMHz} mem={MemOffsetMHz}");
        if (LockRecorded)
        {
            sb.Append(" lock=").Append(LockedCoreClockMHz is uint mhz
                ? mhz.ToString(CultureInfo.InvariantCulture)
                : "none");
        }

        if (VfPointsEdited is int edited)
        {
            sb.Append(CultureInfo.InvariantCulture, $" vf={edited}");
            if (edited > 0 && VfPointMaxOffsetMHz is int max)
            {
                sb.Append(CultureInfo.InvariantCulture, $" vfmax={max}");
            }
        }

        if (GpuId is { Length: > 0 } id)
        {
            sb.Append(" gpu=").Append(Token(id));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads an <c>offsets</c> marker's text. Core and memory fall back to
    /// <paramref name="previous"/> when the marker omits them (the reader has
    /// always kept the last value it saw); every other field describes this
    /// marker alone, so a later marker without a lock is not given the lock of
    /// an earlier one.
    /// </summary>
    public static FlightTuning Parse(string markerText, FlightTuning? previous = null)
    {
        int core = previous?.CoreOffsetMHz ?? 0;
        int mem = previous?.MemOffsetMHz ?? 0;
        bool lockRecorded = false;
        uint? lockMHz = null;
        int? vfEdited = null;
        int? vfMax = null;
        string? gpu = null;

        foreach (string part in markerText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryField(part, "core=", out var v) && TryInt(v, out int c))
            {
                core = c;
            }
            else if (TryField(part, "mem=", out v) && TryInt(v, out int m))
            {
                mem = m;
            }
            else if (TryField(part, "lock=", out v))
            {
                if (v.SequenceEqual("none"))
                {
                    lockRecorded = true;
                    lockMHz = null;
                }
                else if (uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out uint l) && l > 0)
                {
                    lockRecorded = true;
                    lockMHz = l;
                }
            }
            else if (TryField(part, "vf=", out v) && TryInt(v, out int n) && n >= 0)
            {
                vfEdited = n;
            }
            else if (TryField(part, "vfmax=", out v) && TryInt(v, out int mx))
            {
                vfMax = mx;
            }
            else if (TryField(part, "gpu=", out v) && v.Length > 0)
            {
                gpu = v.ToString();
            }
        }

        return new FlightTuning
        {
            CoreOffsetMHz = core,
            MemOffsetMHz = mem,
            LockRecorded = lockRecorded,
            LockedCoreClockMHz = lockMHz,
            VfPointsEdited = vfEdited,
            VfPointMaxOffsetMHz = vfEdited > 0 ? vfMax : null,
            GpuId = gpu,
        };
    }

    private static bool TryField(string part, string prefix, out ReadOnlySpan<char> value)
    {
        value = part.StartsWith(prefix, StringComparison.Ordinal) ? part.AsSpan(prefix.Length) : default;
        return part.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool TryInt(ReadOnlySpan<char> s, out int value) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    /// <summary>One space-free token: the marker is split on spaces and must stay one line.</summary>
    private static string Token(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            sb.Append(char.IsWhiteSpace(ch) || char.IsControl(ch) ? '_' : ch);
        }

        return sb.ToString();
    }
}
