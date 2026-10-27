using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace Afterglow.Core.Security;

/// <summary>
/// The per-user registration model for the elevated logon task (R-46), pure so
/// tests can pin it; the App's <c>StartupTaskService</c> runs schtasks.exe with
/// what this builds and feeds back what schtasks prints.
/// <para>
/// 1.5.0 and earlier registered one machine-global task named "Afterglow". A
/// second Windows user turning "Start with Windows" on silently replaced the
/// first user's task (<c>schtasks /Create /F</c>), turning it off deleted it,
/// and Settings showed "on" to every user — and for a task disabled in Task
/// Scheduler — because it only asked whether the name existed. Each account now
/// gets its own task, "Afterglow-&lt;account SID&gt;" (the convention OneDrive
/// uses for its per-user tasks), and a task only counts as a user's when its
/// principal is that user, it is enabled, and an enabled logon trigger fires
/// for that user. The legacy name is still found, and migrated or removed, when
/// its principal is the user asking; the uninstaller removes every user's task
/// through <see cref="AfterglowTasksIn"/>.
/// </para>
/// </summary>
public static class StartupTaskRegistration
{
    /// <summary>The machine-global name 1.5.0 and earlier registered (one task for whoever enabled it last).</summary>
    public const string LegacyTaskName = LogonTaskDefinition.TaskName;

    public const string PerUserTaskPrefix = LegacyTaskName + "-";

    /// <summary>
    /// Every task the caller can see, one CSV row each, no header. An elevated
    /// caller sees every user's tasks. Only the first column (the task path) is
    /// read: it is the one column schtasks does not localise.
    /// </summary>
    public const string ListArguments = "/Query /FO CSV /NH";

    internal static readonly XNamespace TaskXmlNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// The task name for one account. Throws for anything but a user-account
    /// SID, so a group, a service identity or a malformed string can never name
    /// (or, through the quoted /TN argument, reshape) a task.
    /// </summary>
    public static string TaskNameFor(string userSid)
    {
        if (!IsUserAccountSid(userSid))
        {
            throw new ArgumentException($"'{userSid}' is not a user-account SID.", nameof(userSid));
        }

        return PerUserTaskPrefix + CanonicalSid(userSid);
    }

    /// <summary>The names to look under for one account's task, preferred first.</summary>
    public static IReadOnlyList<string> CandidateTaskNames(string userSid) => [TaskNameFor(userSid), LegacyTaskName];

    /// <summary>
    /// True only for the SID shapes a person signs in to Windows with: a local
    /// or Active Directory account (S-1-5-21-&lt;domain&gt;-&lt;RID&gt;) or a
    /// Microsoft Entra ID account (S-1-12-1-&lt;4 parts&gt;). Rejects SYSTEM and
    /// the other service identities, built-in groups (S-1-5-32-…), Everyone,
    /// service SIDs (S-1-5-80-…) and the DWM/UMFD virtual accounts — none of
    /// which a logon task should be registered for.
    /// </summary>
    public static bool IsUserAccountSid(string? sid)
    {
        if (CanonicalSid(sid) is not { } canonical)
        {
            return false;
        }

        // S-<revision>-<authority>-<subauthority>…
        string[] parts = canonical.Split('-');
        int subAuthorities = parts.Length - 3;
        return subAuthorities == 5 && (parts[2], parts[3]) is ("5", "21") or ("12", "1");
    }

    /// <summary>
    /// The Afterglow logon tasks in <c>schtasks /Query /FO CSV /NH</c> output:
    /// the legacy root task and every root "Afterglow-&lt;user SID&gt;" task, once
    /// each (schtasks prints a row per trigger) and without the leading
    /// backslash. A same-prefixed task whose suffix is not a user SID, or one in
    /// a subfolder, is someone else's and is left out.
    /// </summary>
    public static IReadOnlyList<string> AfterglowTasksIn(string schtasksCsv)
    {
        ArgumentNullException.ThrowIfNull(schtasksCsv);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in schtasksCsv.Split('\n'))
        {
            // A task path can't contain a double quote, so the first quoted
            // field ends at the next one.
            string line = rawLine.Trim();
            int close = line.Length > 1 && line[0] == '"' ? line.IndexOf('"', 1) : -1;
            if (close < 0)
            {
                continue;
            }

            string path = line[1..close];
            if (!path.StartsWith('\\') || path.IndexOf('\\', 1) >= 0)
            {
                continue;
            }

            string name = path[1..];
            if (IsAfterglowTaskName(name) && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    public static string QueryXmlArguments(string taskName) => $"/Query /TN \"{CheckedTaskName(taskName)}\" /XML";

    public static string DeleteArguments(string taskName) => $"/Delete /TN \"{CheckedTaskName(taskName)}\" /F";

    /// <summary>/F replaces only this exact name — after R-46 that is never another user's task.</summary>
    public static string CreateArguments(string taskName, string xmlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlPath);
        if (xmlPath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("A task XML path cannot contain a double quote.", nameof(xmlPath));
        }

        return $"/Create /TN \"{CheckedTaskName(taskName)}\" /XML \"{xmlPath}\" /F";
    }

    /// <summary>
    /// Whether a task <c>UserId</c> — printed as a SID for the principal and as
    /// DOMAIN\name for a logon trigger (both seen in <c>schtasks /XML</c> output
    /// on Windows 11) — names <paramref name="userSid"/>. Names go through
    /// <paramref name="resolveAccountName"/>; an unresolvable one matches nothing.
    /// </summary>
    public static bool SameAccount(string userId, string userSid, Func<string, string?> resolveAccountName) =>
        UserIdToSid(userId, resolveAccountName) is { } other && CanonicalSid(userSid) is { } canonical &&
        string.Equals(other, canonical, StringComparison.OrdinalIgnoreCase);

    /// <summary>A task UserId as a canonical SID: parsed when it is one, resolved when it is a name, else null.</summary>
    public static string? UserIdToSid(string userId, Func<string, string?> resolveAccountName)
    {
        ArgumentNullException.ThrowIfNull(resolveAccountName);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        // A name can start with "S-" too (a domain called S-CORP), so only a
        // string that parses as a SID is one.
        string trimmed = userId.Trim();
        return CanonicalSid(trimmed) ?? CanonicalSid(resolveAccountName(trimmed));
    }

    /// <summary>
    /// R-41: the account to register for is the person signed in to this
    /// Windows session (<paramref name="sessionAccount"/>, DOMAIN\name), not the
    /// identity this process runs as. Setup runs elevated; when a standard user
    /// types an administrator's credentials into the UAC prompt it runs as that
    /// administrator ("Setup will then run under that account" — Inno's
    /// PrivilegesRequired docs), and so does the app when it self-elevates the
    /// same way. The session's user is the one who will sign in again.
    /// When this process already runs as that user (an administrator who just
    /// consented) its own token's SID (<paramref name="processSid"/>) is used
    /// with no name lookup at all; otherwise the name is looked up.
    /// Validated: someone must be signed in (a deployment tool running setup as
    /// SYSTEM in session 0 has nobody to start for), the name must resolve, and
    /// it must resolve to a user account.
    /// </summary>
    public static StartupTaskTarget ResolveTarget(
        string? sessionAccount, string? processAccount, string? processSid, Func<string, string?> resolveAccountName)
    {
        ArgumentNullException.ThrowIfNull(resolveAccountName);
        if (string.IsNullOrWhiteSpace(sessionAccount))
        {
            return StartupTaskTarget.Refused(
                "No one is signed in to this Windows session (a deployment tool may have run setup as SYSTEM), " +
                "so there is no account to start Afterglow for at logon. Sign in and turn on Start with Windows " +
                "from Settings.");
        }

        string account = sessionAccount.Trim();
        string? sid = string.Equals(account, processAccount?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? CanonicalSid(processSid)
            : null;
        sid ??= CanonicalSid(resolveAccountName(account));
        if (sid is null)
        {
            return StartupTaskTarget.Refused(
                $"Windows couldn't look up the signed-in account {account}, so Afterglow can't tell whose logon " +
                "should start it.");
        }

        if (!IsUserAccountSid(sid))
        {
            return StartupTaskTarget.Refused(
                $"The signed-in account {account} ({sid}) isn't a user account a logon task can start Afterglow for.");
        }

        return new StartupTaskTarget(sid, account, null);
    }

    /// <summary>DOMAIN\name → SID through the local security authority; null when it doesn't resolve.</summary>
    public static string? ResolveAccountName(string accountName)
    {
        try
        {
            return new NTAccount(accountName).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (SystemException ex) when (ex.GetType() == typeof(SystemException))
        {
            // NTAccount.Translate reports any other LSA lookup failure (a domain
            // controller out of reach, say) as a bare SystemException.
            return null;
        }
    }

    // "S-" first: SecurityIdentifier also accepts SDDL aliases ("SY", "BA"), and
    // a bare alias in a task's UserId is a name to resolve, not a SID. No
    // trimming here (callers trim what they parse), so nothing but SID
    // characters can follow the prefix of a name IsAfterglowTaskName accepts.
    private static string? CanonicalSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-", StringComparison.OrdinalIgnoreCase) ||
            sid.Any(char.IsWhiteSpace))
        {
            return null;
        }

        try
        {
            return new SecurityIdentifier(sid).Value;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // The legacy name, or the prefix followed by a user-account SID — which also
    // means no quote, space or slash can reach a quoted /TN argument.
    private static bool IsAfterglowTaskName(string name) =>
        string.Equals(name, LegacyTaskName, StringComparison.OrdinalIgnoreCase) ||
        (name.StartsWith(PerUserTaskPrefix, StringComparison.OrdinalIgnoreCase) &&
         IsUserAccountSid(name[PerUserTaskPrefix.Length..]));

    private static string CheckedTaskName(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        if (!IsAfterglowTaskName(taskName))
        {
            throw new ArgumentException($"'{taskName}' is not an Afterglow logon task name.", nameof(taskName));
        }

        return taskName;
    }
}

/// <summary>The account a logon task is for, or why there isn't one (<see cref="Error"/>).</summary>
public sealed record StartupTaskTarget(string? Sid, string? AccountName, string? Error)
{
    internal static StartupTaskTarget Refused(string error) => new(null, null, error);

    /// <summary>
    /// Name → SID for a task's UserId: the target's own DOMAIN\name answers with
    /// its SID directly (schtasks prints a logon trigger's user by name, and a
    /// lookup can fail where the session already told us who this is); any
    /// other name goes to <paramref name="fallback"/>.
    /// </summary>
    public string? ResolveAccount(string accountName, Func<string, string?> fallback)
    {
        ArgumentNullException.ThrowIfNull(accountName);
        ArgumentNullException.ThrowIfNull(fallback);
        return Sid is not null && string.Equals(accountName.Trim(), AccountName, StringComparison.OrdinalIgnoreCase)
            ? Sid
            : fallback(accountName);
    }
}

/// <summary>One &lt;LogonTrigger&gt;: whether it is enabled and whose logon it fires on (null: anyone's).</summary>
public readonly record struct LogonTriggerInfo(bool Enabled, string? UserId);

/// <summary>
/// What Task Scheduler actually holds for a task (R-46): parsed from
/// <c>schtasks /Query /TN … /XML</c>, which omits &lt;Enabled&gt; when it is
/// true (the schema default) and prints <c>&lt;Enabled&gt;false&lt;/Enabled&gt;</c>
/// in &lt;Settings&gt; once the task is disabled in Task Scheduler.
/// </summary>
public sealed record RegisteredLogonTask(
    bool Enabled,
    string? PrincipalUserId,
    IReadOnlyList<LogonTriggerInfo> LogonTriggers,
    string? Command)
{
    /// <summary>Null when the text is not a task definition.</summary>
    public static RegisteredLogonTask? Parse(string taskXml)
    {
        XDocument doc;
        try
        {
            // As in ParseCommand: parsing from a string ignores the UTF-16 declaration.
            doc = XDocument.Parse(taskXml);
        }
        catch (XmlException)
        {
            return null;
        }

        XNamespace ns = StartupTaskRegistration.TaskXmlNamespace;
        XElement? root = doc.Root;
        if (root is null || root.Name != ns + "Task")
        {
            return null;
        }

        bool enabled = !IsFalse(root.Element(ns + "Settings")?.Element(ns + "Enabled"));

        // The principal the actions run as: the one the Actions' Context names.
        string? context = root.Element(ns + "Actions")?.Attribute("Context")?.Value;
        var principals = root.Element(ns + "Principals")?.Elements(ns + "Principal").ToList() ?? [];
        XElement? principal = context is null
            ? principals.FirstOrDefault()
            : principals.FirstOrDefault(p => string.Equals((string?)p.Attribute("id"), context, StringComparison.Ordinal));
        string? principalUser = NullIfBlank(principal?.Element(ns + "UserId")?.Value);

        var triggers = root.Element(ns + "Triggers")?.Elements(ns + "LogonTrigger")
            .Select(t => new LogonTriggerInfo(!IsFalse(t.Element(ns + "Enabled")), NullIfBlank(t.Element(ns + "UserId")?.Value)))
            .ToList() ?? [];

        return new RegisteredLogonTask(enabled, principalUser, triggers, LogonTaskDefinition.ParseCommand(taskXml));
    }

    /// <summary>The task runs as <paramref name="userSid"/>, whatever its state — so it is theirs to migrate or remove.</summary>
    public bool BelongsTo(string userSid, Func<string, string?> resolveAccountName) =>
        PrincipalUserId is { } principal && StartupTaskRegistration.SameAccount(principal, userSid, resolveAccountName);

    /// <summary>
    /// What the "Start with Windows" toggle means: the task is enabled, runs as
    /// this user, and has an enabled logon trigger that fires for this user. A
    /// task disabled in Task Scheduler, one whose logon triggers are all
    /// disabled or all for another account, or one belonging to someone else is
    /// "off" for this user.
    /// </summary>
    public bool StartsAtLogonOf(string userSid, Func<string, string?> resolveAccountName) =>
        Enabled &&
        BelongsTo(userSid, resolveAccountName) &&
        LogonTriggers.Any(t => t.Enabled && TriggerFiresFor(t.UserId, userSid, resolveAccountName));

    private static bool TriggerFiresFor(string? triggerUser, string userSid, Func<string, string?> resolveAccountName)
    {
        if (triggerUser is null)
        {
            return true; // no UserId: any user's logon
        }

        // schtasks prints a trigger's user by name, and that name can fail to
        // resolve while the trigger still works: it can be stale (on the dev
        // machine a per-user logon trigger still printed the computer's former
        // name, which no longer resolves) or mangled by the console code page
        // the output is decoded through. The principal — printed as a SID — has
        // already shown whose task this is, so an unresolvable trigger name must
        // not turn a working task "off". Only a name that resolves to someone
        // else does.
        return StartupTaskRegistration.UserIdToSid(triggerUser, resolveAccountName) is not { } sid ||
               StartupTaskRegistration.SameAccount(sid, userSid, resolveAccountName);
    }

    // xs:boolean: "false" or "0"; anything else (or absence) leaves the default true.
    private static bool IsFalse(XElement? element) =>
        element is not null && element.Value.Trim() is var v &&
        (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || v == "0");

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
