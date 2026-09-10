using System.Globalization;

namespace Afterglow.Cli;

/// <summary>
/// Shared option checking. A CLI that silently ignores what it does not
/// understand is a poor one anywhere; here it can manufacture a false stability
/// verdict. `stress --pattern transtions` fell through to the SUSTAINED burn and
/// reported a clean pass — so a single typo replaced the regime the user chose
/// for catching marginal memory offsets with one that cannot catch them, and
/// still said "stable". Likewise `--seconds 6O` (letter O) ran the 30-second
/// default and called it the run that was asked for.
/// <para>
/// Every option-taking command routes through here: an option this command does
/// not know, or a value it cannot parse, stops the run and says so.
/// </para>
/// </summary>
internal static class CliArgs
{
    /// <summary>
    /// Every command's option surface, in ONE place.
    /// <para>
    /// It used to live at each call site, and the lists drifted from what the
    /// commands actually read — `drs`'s three hidden probe flags were left out
    /// and stopped working the moment validation was added. A single table is
    /// also what makes the contract testable: <c>CliContractTests</c> drives
    /// these exact lists, so a command that gains an option without declaring it
    /// here fails a test instead of silently rejecting a documented flag.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string[] Flags, string[] ValueOptions)> Options =
        new Dictionary<string, (string[], string[])>(StringComparer.Ordinal)
        {
            ["stress"] = (["--probe-adapter"], ["--seconds", "--intensity", "--pattern", "--gpu"]),
            ["vram"] = ([], ["--seconds", "--gpu"]),
            ["fps"] = ([], ["--seconds"]),
            ["certify"] = ([], ["--profile", "--seconds", "--gpu"]),
            ["vfcurve"] = (["--load", "--probe", "--json", "--fresh"], ["--seconds", "--gpu"]),
            ["vfpoints"] = (["--clear"], ["--set", "--flatten", "--gpu"]),
            ["drs"] = (
                ["--clear", "--probe-create", "--probe-list", "--probe-profile"],
                ["--exe", "--cap", "--vsync", "--low-latency"]),
            ["monitor"] = (["--once", "--json"], ["--interval", "--csv", "--gpu"]),

            // The read-only twins were the last two commands outside this
            // table, so `get --jsn` printed the human-readable state and exited
            // 0 as if the flag were absent. (They share TuneCommands.cs with
            // `set`, so the contract test covers that file under "set".)
            ["caps"] = (["--json"], ["--gpu"]),
            ["get"] = (["--json"], ["--gpu"]),

            // `set` shares TuneCommands.cs with `caps`/`get`, whose --json the
            // contract test therefore sees; over-declaring it is harmless (the
            // command's own switch still rejects it).
            ["set"] = (
                ["--json"],
                ["--core-offset", "--mem-offset", "--power-limit", "--lock-clock",
                 "--voltage-boost", "--temp-limit", "--fan", "--gpu"]),
        };

    /// <summary>Validates <paramref name="args"/> against the named command's declared options.</summary>
    public static string? Validate(string[] args, string command)
    {
        var (flags, valueOptions) = Options[command];
        return Validate(args, flags, valueOptions);
    }

    /// <summary>
    /// Returns an error message if any token after the command name is not a
    /// recognised flag, a recognised option with its value, or that value.
    /// No command here takes positional arguments, so a bare token is a mistake
    /// too — usually a value whose option was misspelled.
    /// </summary>
    public static string? Validate(string[] args, string[] flags, string[] valueOptions)
    {
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (Array.IndexOf(flags, token) >= 0)
            {
                continue;
            }

            if (Array.IndexOf(valueOptions, token) >= 0)
            {
                if (i + 1 >= args.Length)
                {
                    return $"'{token}' needs a value.";
                }

                // --gpu is checked HERE rather than in each command, because it
                // is the one option every command shares and the one whose
                // silent fallback is worst: an unusable index used to mean "no
                // particular card", which turned a named-card burn into an
                // unbound guess and a named-card certification into a stamp on
                // GPU 0. Putting the rule in the shared checker is what stops it
                // being added to one command and forgotten in its twins.
                if (token == "--gpu"
                    && !uint.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    // Checked at THIS token, not by re-scanning the list for the
                    // first --gpu: a duplicated flag whose second value was bad
                    // used to pass because the scan found the good first one.
                    return $"'--gpu' needs a GPU index (a whole number), not '{args[i + 1]}'.";
                }

                i++;
                continue;
            }

            return token.StartsWith("--", StringComparison.Ordinal)
                ? $"Unknown option '{token}'. Run `afterglow-cli help` for the options this command accepts."
                : $"Unexpected argument '{token}' — this command takes options only.";
        }

        return null;
    }

    /// <summary>
    /// Reads an integer option. Absent leaves <paramref name="value"/> alone and
    /// returns null; present-but-unparseable OR out-of-range returns an error,
    /// rather than falling back to the default or silently clamping. That is the
    /// whole point: a run must never be quietly different from the one asked for.
    /// </summary>
    public static string? TryInt(string[] args, string name, int min, int max, ref int value)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] != name)
            {
                continue;
            }

            if (!int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return $"'{name}' needs a whole number, not '{args[i + 1]}'.";
            }

            if (parsed < min || parsed > max)
            {
                return $"'{name}' must be between {min} and {max} (got {parsed}).";
            }

            value = parsed;
            return null;
        }

        return null;
    }

    /// <summary>Unsigned twin of <see cref="TryInt"/>.</summary>
    public static string? TryUInt(string[] args, string name, uint min, uint max, ref uint value)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] != name)
            {
                continue;
            }

            if (!uint.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint parsed))
            {
                return $"'{name}' needs a whole number, not '{args[i + 1]}'.";
            }

            if (parsed < min || parsed > max)
            {
                return $"'{name}' must be between {min} and {max} (got {parsed}).";
            }

            value = parsed;
            return null;
        }

        return null;
    }
}
