using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Afterglow.Core.Interop.Nvapi;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// An in-memory NVIDIA driver settings (DRS) store behind the real NVAPI
/// interface IDs, bound through <see cref="DrsApi.CreateForTesting"/> so the
/// DrsApi workflows run end to end on a machine without an NVIDIA GPU. The
/// semantics it models are NVIDIA's own (NVIDIA Driver Settings Programming
/// Guide PG-12072-001_v01, and the nvapi.h / nvapi_lite_common.h comments in
/// github.com/NVIDIA/nvapi):
/// <list type="bullet">
/// <item>a session works on a copy loaded by LoadSettings; nothing reaches the
/// store until SaveSettings, and a session destroyed unsaved changes nothing;</item>
/// <item>application-profile settings override the Current Global / Base
/// profile, and GetSetting reports an inherited value with its settingLocation
/// (guide p.2 "Profile Hierarchy", p.8–10 on settingLocation);</item>
/// <item>DeleteProfileSetting "deletes a setting or sets it back to predefined
/// value"; a setting the profile does not hold is SETTING_NOT_FOUND;</item>
/// <item>CreateProfile of a name in use is PROFILE_NAME_IN_USE, a missing exe is
/// EXECUTABLE_NOT_FOUND, a missing profile is PROFILE_NOT_FOUND.</item>
/// </list>
/// The Fail*/Ignore* knobs inject what a real driver can do and a GPU-less
/// machine cannot: refuse a call, or report success without doing it.
/// The state is static because the entry points must be static
/// [UnmanagedCallersOnly] functions; every test class that uses it joins
/// [Collection("FakeDrsDriver")] so they never run in parallel, and calls
/// <see cref="Reset"/> first.
/// </summary>
internal static unsafe class FakeDrsDriver
{
    // Interface IDs: nvapi_interface.h, github.com/NVIDIA/nvapi (the same table DrsApi cites).
    private const uint IdCreateSession = 0x0694D52E;
    private const uint IdDestroySession = 0xDAD9CFF8;
    private const uint IdLoadSettings = 0x375DBD6B;
    private const uint IdSaveSettings = 0xFCBC7E14;
    private const uint IdFindProfileByName = 0x7E4A9A0B;
    private const uint IdCreateProfile = 0xCC176068;
    private const uint IdDeleteProfile = 0x17093206;
    private const uint IdCreateApplication = 0x4347A9DE;
    private const uint IdFindApplicationByName = 0xEEE566B2;
    private const uint IdSetSetting = 0x577DD202;
    private const uint IdGetSetting = 0x73BF8338;
    private const uint IdDeleteProfileSetting = 0xE4A26362;
    private const uint IdGetProfileInfo = 0x61CD6FD6;

    /// <summary>NVDRS_SETTING_LOCATION (nvapi.h).</summary>
    public const uint CurrentProfileLocation = 0;
    public const uint GlobalProfileLocation = 1;

    /// <summary>NVAPI_EXECUTABLE_ALREADY_IN_USE (nvapi_lite_common.h): the exe belongs to another profile.</summary>
    public const NvapiStatus ExecutableAlreadyInUse = (NvapiStatus)(-167);

    /// <summary>NVAPI_INVALID_USER_PRIVILEGE (nvapi_lite_common.h): the call needs administrator rights.</summary>
    public const NvapiStatus InvalidUserPrivilege = (NvapiStatus)(-137);

    private const nint SessionHandle = 0x5E55;

    /// <summary>One stored setting: its value, and whether it is the driver-shipped (predefined) one.</summary>
    public sealed record Setting(uint Value, bool IsPredefined);

    /// <summary>One profile. Handles are stable ids, so a handle means the same profile in every session.</summary>
    public sealed class Profile
    {
        public required string Name { get; init; }

        public bool IsPredefined { get; init; }

        public List<string> Apps { get; init; } = [];

        public Dictionary<uint, Setting> Settings { get; init; } = [];

        /// <summary>Driver-shipped values that DeleteProfileSetting restores instead of removing.</summary>
        public Dictionary<uint, uint> PredefinedValues { get; init; } = [];

        public Profile Clone() => new()
        {
            Name = Name,
            IsPredefined = IsPredefined,
            Apps = [.. Apps],
            Settings = new Dictionary<uint, Setting>(Settings),
            PredefinedValues = new Dictionary<uint, uint>(PredefinedValues),
        };
    }

    private static Dictionary<nint, Profile> _store = [];
    private static Dictionary<nint, Profile>? _session;
    private static nint _nextHandle = 1;

    /// <summary>The Current Global Profile's settings: what an application profile inherits.</summary>
    public static Dictionary<uint, uint> Global { get; } = [];

    public static NvapiStatus? FailFindApplication { get; set; }

    public static NvapiStatus? FailGetProfileInfo { get; set; }

    public static NvapiStatus? FailDeleteProfile { get; set; }

    public static NvapiStatus? FailDeleteProfileSetting { get; set; }

    public static NvapiStatus? FailSave { get; set; }

    /// <summary>DeleteProfile returns OK without deleting anything.</summary>
    public static bool IgnoreDeleteProfile { get; set; }

    /// <summary>DeleteProfileSetting returns OK without deleting anything.</summary>
    public static bool IgnoreDeleteProfileSetting { get; set; }

    /// <summary>
    /// Runs once, right after the next successful save, on the persisted store:
    /// another process editing the store between two of Afterglow's sessions.
    /// It runs on the native-call path, so it must not throw.
    /// </summary>
    public static Action? AfterNextSave { get; set; }

    public static int SaveCount { get; private set; }

    public static int DeleteProfileCalls { get; private set; }

    /// <summary>Profiles currently persisted in the store (what a fresh session loads).</summary>
    public static IReadOnlyCollection<Profile> Stored => _store.Values;

    public static void Reset()
    {
        _store = [];
        _session = null;
        _nextHandle = 1;
        Global.Clear();
        FailFindApplication = null;
        FailGetProfileInfo = null;
        FailDeleteProfile = null;
        FailDeleteProfileSetting = null;
        FailSave = null;
        IgnoreDeleteProfile = false;
        IgnoreDeleteProfileSetting = false;
        AfterNextSave = null;
        SaveCount = 0;
        DeleteProfileCalls = 0;
    }

    /// <summary>Puts a profile straight into the persisted store, as if the user or the driver had made it.</summary>
    public static void Seed(Profile profile) => _store[_nextHandle++] = profile;

    /// <summary>The persisted profile with exactly this name (case-insensitive, as DRS names are), or null.</summary>
    public static Profile? StoredProfile(string name) =>
        _store.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static DrsApi Create()
    {
        var api = DrsApi.CreateForTesting(Resolve, out var status);
        Assert.Equal(NvapiStatus.Ok, status);
        return api!;
    }

    private static nint Resolve(uint id) => id switch
    {
        IdCreateSession => (nint)(delegate* unmanaged[Cdecl]<nint*, NvapiStatus>)&CreateSession,
        IdDestroySession => (nint)(delegate* unmanaged[Cdecl]<nint, NvapiStatus>)&DestroySession,
        IdLoadSettings => (nint)(delegate* unmanaged[Cdecl]<nint, NvapiStatus>)&LoadSettings,
        IdSaveSettings => (nint)(delegate* unmanaged[Cdecl]<nint, NvapiStatus>)&SaveSettings,
        IdFindProfileByName => (nint)(delegate* unmanaged[Cdecl]<nint, char*, nint*, NvapiStatus>)&FindProfileByName,
        IdCreateProfile => (nint)(delegate* unmanaged[Cdecl]<nint, DrsApi.NvdrsProfile*, nint*, NvapiStatus>)&CreateProfile,
        IdDeleteProfile => (nint)(delegate* unmanaged[Cdecl]<nint, nint, NvapiStatus>)&DeleteProfile,
        IdCreateApplication => (nint)(delegate* unmanaged[Cdecl]<nint, nint, DrsApi.NvdrsApplication*, NvapiStatus>)&CreateApplication,
        IdFindApplicationByName => (nint)(delegate* unmanaged[Cdecl]<nint, char*, nint*, DrsApi.NvdrsApplication*, NvapiStatus>)&FindApplicationByName,
        IdSetSetting => (nint)(delegate* unmanaged[Cdecl]<nint, nint, DrsApi.NvdrsSetting*, NvapiStatus>)&SetSetting,
        IdGetSetting => (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, DrsApi.NvdrsSetting*, NvapiStatus>)&GetSetting,
        IdDeleteProfileSetting => (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, NvapiStatus>)&DeleteProfileSetting,
        IdGetProfileInfo => (nint)(delegate* unmanaged[Cdecl]<nint, nint, DrsApi.NvdrsProfile*, NvapiStatus>)&GetProfileInfo,
        _ => 0,   // enumeration interfaces: not modelled (DrsApi treats them as optional)
    };

    // Every entry point below runs on a native-to-managed transition, where an
    // exception would take the test host down: lookups use TryGetValue and
    // nothing here throws.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus CreateSession(nint* session)
    {
        *session = SessionHandle;
        return NvapiStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus DestroySession(nint session)
    {
        _session = null;   // unsaved edits are discarded
        return NvapiStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus LoadSettings(nint session)
    {
        _session = Copy(_store);
        return NvapiStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus SaveSettings(nint session)
    {
        if (_session is null)
        {
            return NvapiStatus.InvalidHandle;
        }

        if (FailSave is { } fail)
        {
            return fail;
        }

        _store = Copy(_session);
        SaveCount++;
        var hook = AfterNextSave;
        AfterNextSave = null;
        hook?.Invoke();
        return NvapiStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus FindProfileByName(nint session, char* name, nint* profile)
    {
        if (_session is null)
        {
            return NvapiStatus.InvalidHandle;
        }

        string wanted = new(name);
        foreach (var (handle, candidate) in _session)
        {
            if (string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                *profile = handle;
                return NvapiStatus.Ok;
            }
        }

        return NvapiStatus.ProfileNotFound;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus CreateProfile(nint session, DrsApi.NvdrsProfile* info, nint* profile)
    {
        if (_session is null)
        {
            return NvapiStatus.InvalidHandle;
        }

        string name = new(info->ProfileName);
        if (_session.Values.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return NvapiStatus.ProfileNameInUse;
        }

        nint handle = _nextHandle++;
        _session[handle] = new Profile { Name = name };
        *profile = handle;
        return NvapiStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus DeleteProfile(nint session, nint profile)
    {
        DeleteProfileCalls++;
        if (_session is null || !_session.ContainsKey(profile))
        {
            return NvapiStatus.InvalidHandle;
        }

        if (FailDeleteProfile is { } fail)
        {
            return fail;
        }

        if (!IgnoreDeleteProfile)
        {
            _session.Remove(profile);
        }

        return NvapiStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus CreateApplication(nint session, nint profile, DrsApi.NvdrsApplication* app)
    {
        if (_session is null || !_session.TryGetValue(profile, out var target))
        {
            return NvapiStatus.InvalidHandle;
        }

        string name = new(app->AppName);
        if (_session.Values.Any(p => p.Apps.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            return ExecutableAlreadyInUse;
        }

        target.Apps.Add(name);
        return NvapiStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus FindApplicationByName(nint session, char* name, nint* profile, DrsApi.NvdrsApplication* app)
    {
        if (_session is null)
        {
            return NvapiStatus.InvalidHandle;
        }

        if (FailFindApplication is { } fail)
        {
            return fail;
        }

        string wanted = new(name);
        foreach (var (handle, candidate) in _session)
        {
            string? stored = candidate.Apps.FirstOrDefault(a => string.Equals(a, wanted, StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
            {
                *profile = handle;
                Write(app->AppName, stored);
                return NvapiStatus.Ok;
            }
        }

        return NvapiStatus.ExecutableNotFound;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus SetSetting(nint session, nint profile, DrsApi.NvdrsSetting* setting)
    {
        if (_session is null || !_session.TryGetValue(profile, out var target))
        {
            return NvapiStatus.InvalidHandle;
        }

        target.Settings[setting->SettingId] = new Setting(setting->CurrentValue, IsPredefined: false);
        return NvapiStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus GetSetting(nint session, nint profile, uint settingId, DrsApi.NvdrsSetting* setting)
    {
        if (_session is null || !_session.TryGetValue(profile, out var target))
        {
            return NvapiStatus.InvalidHandle;
        }

        setting->SettingId = settingId;
        setting->SettingType = 0;
        if (target.Settings.TryGetValue(settingId, out var local))
        {
            setting->CurrentValue = local.Value;
            setting->SettingLocation = CurrentProfileLocation;
            setting->IsCurrentPredefined = local.IsPredefined ? 1u : 0u;
            return NvapiStatus.Ok;
        }

        if (Global.TryGetValue(settingId, out uint inherited))
        {
            setting->CurrentValue = inherited;
            setting->SettingLocation = GlobalProfileLocation;
            setting->IsCurrentPredefined = 0;
            return NvapiStatus.Ok;
        }

        return NvapiStatus.SettingNotFound;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus DeleteProfileSetting(nint session, nint profile, uint settingId)
    {
        if (_session is null || !_session.TryGetValue(profile, out var target))
        {
            return NvapiStatus.InvalidHandle;
        }

        if (FailDeleteProfileSetting is { } fail)
        {
            return fail;
        }

        if (!target.Settings.TryGetValue(settingId, out var current) || current.IsPredefined)
        {
            return NvapiStatus.SettingNotFound;   // no user value to delete
        }

        if (!IgnoreDeleteProfileSetting)
        {
            if (target.PredefinedValues.TryGetValue(settingId, out uint shipped))
            {
                target.Settings[settingId] = new Setting(shipped, IsPredefined: true);
            }
            else
            {
                target.Settings.Remove(settingId);
            }
        }

        return NvapiStatus.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NvapiStatus GetProfileInfo(nint session, nint profile, DrsApi.NvdrsProfile* info)
    {
        if (_session is null || !_session.TryGetValue(profile, out var target))
        {
            return NvapiStatus.InvalidHandle;
        }

        if (FailGetProfileInfo is { } fail)
        {
            return fail;
        }

        Write(info->ProfileName, target.Name);
        info->IsPredefined = target.IsPredefined ? 1u : 0u;
        info->NumOfApps = (uint)target.Apps.Count;
        info->NumOfSettings = (uint)target.Settings.Count;
        return NvapiStatus.Ok;
    }

    private static Dictionary<nint, Profile> Copy(Dictionary<nint, Profile> source) =>
        source.ToDictionary(e => e.Key, e => e.Value.Clone());

    private static void Write(char* destination, string value)
    {
        int count = Math.Min(value.Length, 2047);
        for (int i = 0; i < count; i++)
        {
            destination[i] = value[i];
        }

        destination[count] = '\0';
    }
}
