using System.Text.Json;
using System.Text.Json.Serialization;

namespace Afterglow.Core.Profiles;

/// <summary>
/// JSON profile persistence under <see cref="AppPaths.ProfilesDir"/>. One file per
/// profile, atomic writes, tolerant loading (a corrupt file is skipped and reported,
/// never crashes the app).
/// <para>
/// A profile's identity is its exact name, as recorded inside its file. The file
/// name (<see cref="PathFor"/>) is a lossy image of it — characters Windows can't
/// use become '_' and NTFS ignores case — so the store never trusts the file name
/// alone: Load and Delete only act on a file that holds the requested name, and
/// Save refuses a name whose file already holds a different profile (R-25). That
/// keeps the mapping injective over the profiles actually stored without moving
/// any file an earlier build wrote.
/// </para>
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public ProfileStore(string? directory = null)
    {
        _directory = directory ?? AppPaths.ProfilesDir;

        // Best-effort: a store whose directory can't be created (ProgramData
        // locked down, a read-only path, an AV block) must still construct, so
        // a host that only reads — or that never touches profiles at all — is
        // not killed at startup. Save/LoadAll retry and report instead (F-36).
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Files that could not be loaded (unreadable, unparseable, corrupt or
    /// invalid) during the last <see cref="LoadAll"/>.
    /// </summary>
    public IReadOnlyList<(string File, string Error)> LastLoadErrors { get; private set; } = [];

    public IReadOnlyList<TuningProfile> LoadAll()
    {
        var profiles = new List<TuningProfile>();
        var errors = new List<(string, string)>();

        // Enumeration itself can throw (directory missing, unreadable, or not a
        // directory at all) — that used to escape LoadAll and, from the MCP
        // server's list_profiles tool, take the whole stdio process down (F-36).
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_directory, "*.json").ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadErrors = [(_directory, ex.Message)];
            return [];
        }

        foreach (string file in files)
        {
            try
            {
                // ProgramData hardening: when elevated, a profile whose owner is
                // neither an administrator/SYSTEM nor the current user may have
                // been planted by a standard user — skip it rather than trust it
                // for a GPU-write (no-op unelevated / --data-dir). Checked on the
                // handle the content is read from.
                if (Security.DataRootProtection.TryReadAllText(file) is not { } json)
                {
                    errors.Add((file, "Skipped: file owner is not trusted while running elevated."));
                    continue;
                }

                var profile = JsonSerializer.Deserialize<TuningProfile>(json, JsonOptions);
                if (profile is null)
                {
                    errors.Add((file, "Empty profile file."));
                }
                // The store is vendor-agnostic, so its corruption guard uses
                // the widest legitimate floors (Intel clamps reach 100 MHz;
                // low-power Arc parts report power minimums below 50 W). The
                // apply engine re-validates against the actual device.
                // Validate checks the shape first, so null collections from
                // the JSON come back here as an error, not a throw (R-24).
                else if (profile.Validate(lockFloorMHz: 100, powerFloorW: 15) is string error)
                {
                    errors.Add((file, error));
                }
                else
                {
                    profiles.Add(profile);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                errors.Add((file, ex.Message));
            }
            catch (Exception ex)
            {
                // Deliberately broad (R-24): LoadAll runs while the main window
                // is still being built (ProfilesViewModel) and behind the MCP
                // server's list_profiles. Whatever else one file's content can
                // make the parser or a check throw must cost that file, not
                // the app — it is reported like any other unreadable file.
                errors.Add((file, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        LastLoadErrors = errors;
        return profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The profile named exactly <paramref name="name"/>, or null when there is
    /// none — including when its file can't be read, is refused (untrusted
    /// owner while elevated), or holds a different profile.
    /// </summary>
    public TuningProfile? Load(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // Elevated: refuse a profile whose owner is untrusted (see LoadAll).
            var profile = Security.DataRootProtection.TryReadAllText(path) is { } json
                ? JsonSerializer.Deserialize<TuningProfile>(json, JsonOptions)
                : null;

            // R-25: the file may belong to another name that maps onto the
            // same file — left by an older build that let the second save
            // replace the first. The startup profile, game rules and
            // automation resolve by name, so they must get nothing here, not
            // that other profile's tuning. Ordinal: a reference made under one
            // spelling never silently resolves to another.
            if (profile is null || !string.Equals(profile.Name, name, StringComparison.Ordinal))
            {
                return null;
            }

            // R-24: null collections are corruption like unparseable JSON;
            // handing them out only moves the NullReferenceException into the
            // apply or the certifier. Range checks stay with the apply engine,
            // which reports what it refused and why.
            return profile.StructuralError() is null ? profile : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates then atomically writes the profile. Throws
    /// <see cref="InvalidOperationException"/> on validation failure, or when the
    /// profile's file already holds a different profile (R-25).
    /// </summary>
    public void Save(TuningProfile profile)
    {
        // Widest legitimate floors — see the load-side comment above.
        if (profile.Validate(lockFloorMHz: 100, powerFloorW: 15) is string error)
        {
            throw new InvalidOperationException($"Refusing to save invalid profile: {error}");
        }

        // Retry directory creation here — the constructor's attempt is
        // best-effort, so a store that couldn't make its folder then may be
        // able to now. A genuine failure surfaces as the IOException /
        // UnauthorizedAccessException that every Save caller already handles.
        Directory.CreateDirectory(_directory);

        // R-25: 'Game: Quiet' and 'Game_ Quiet' (or 'Quiet' and 'quiet') share
        // one file, and the second save used to replace the first silently —
        // after which every reference to the first name applied the second
        // profile's tuning. Re-saving the same name still updates in place; a
        // file that names no profile (unparseable) is overwritten as before.
        string path = PathFor(profile.Name);
        if (StoredNameAt(path) is { } occupant && !string.Equals(occupant, profile.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to save '{profile.Name}': its file ({Path.GetFileName(path)}) already holds the profile " +
                $"'{occupant}'. Windows file names ignore letter case and can't contain \\ / : * ? \" < > |, so the " +
                "two names would share one file and one profile would replace the other. Pick a different name, " +
                $"or save as '{occupant}' to update that profile.");
        }

        // Temp + rename: the result carries the folder's permissions, and a
        // refused (untrusted-owner) target is moved aside rather than replaced
        // in a way that would inherit its grants.
        Security.DataRootProtection.WriteAllTextAtomic(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    /// <summary>
    /// Deletes the profile named exactly <paramref name="name"/>. False when there
    /// is none — a file that holds a different profile, or that can't be
    /// identified, is left alone (R-25).
    /// </summary>
    public bool Delete(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path) || !string.Equals(StoredNameAt(path), name, StringComparison.Ordinal))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// The name recorded inside an existing profile file, read leniently: only
    /// the <c>name</c> property, so a profile this build can't fully parse (one
    /// saved by a newer version, or one LoadAll rejects as corrupt) still
    /// identifies itself and is not overwritten under another name. Null when
    /// the file is missing, refused (untrusted owner while elevated), or not a
    /// JSON object with a string name. I/O failures propagate to the caller.
    /// </summary>
    private static string? StoredNameAt(string path)
    {
        if (Security.DataRootProtection.TryReadAllText(path) is not { } json)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root &&
                   root.TryGetProperty("name", out var name) &&
                   name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file a profile name is stored in: the name with every character
    /// Windows can't use in a file name replaced by '_'. Lossy (and NTFS ignores
    /// case), so distinct names can map to one file; the store guards that by
    /// checking the name stored inside (see the class remarks). Kept as is so
    /// every file an earlier build wrote stays where it is.
    /// </summary>
    public string PathFor(string profileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. profileName.Select(c => invalid.Contains(c) ? '_' : c)]);
        if (safe.Length == 0)
        {
            safe = "profile";
        }

        return Path.Combine(_directory, safe + ".json");
    }
}
