using System.Runtime.InteropServices;

namespace Afterglow.Core.Interop.Nvapi;

/// <summary>Per-game driver settings Afterglow manages through DRS.</summary>
public sealed record GameDriverSettings
{
    /// <summary>Driver frame-rate limiter in FPS; 0 = off.</summary>
    public int FrameCapFps { get; init; }

    /// <summary>"default" (application-controlled), "on", or "off".</summary>
    public string Vsync { get; init; } = "default";

    /// <summary>Caps maximum pre-rendered frames at 1 (the classic latency reduction).</summary>
    public bool LowLatency { get; init; }

    public bool AnythingSet => FrameCapFps > 0 || Vsync is "on" or "off" || LowLatency;
}

/// <summary>
/// NVIDIA DRS (driver settings store) — the same per-application settings the
/// NVIDIA Control Panel edits: frame-rate limiter, vsync, pre-rendered frames.
/// Settings persist in the driver, so they apply whenever the game runs, with
/// no injection and nothing resident. Interface IDs and setting IDs come from
/// the public NVAPI SDK (NvApiDriverSettings.h) and published open-source
/// implementations; every write is verified by reading the store back.
/// </summary>
public sealed unsafe class DrsApi
{
    // Function interface IDs (public NVAPI SDK).
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
    private const uint IdEnumProfiles = 0xBC371EE0;

    // Setting IDs (NvApiDriverSettings.h).
    public const uint FrameRateLimiterId = 0x10835002;   // FRL_FPS_ID: fps, 0 = off
    public const uint VsyncModeId = 0x00A879CF;          // VSYNCMODE_ID
    public const uint PreRenderedFramesId = 0x007BA09E;  // PRERENDERLIMIT_ID

    public const uint VsyncApplicationControlled = 0x60925292;
    public const uint VsyncForceOff = 0x08416747;
    public const uint VsyncForceOn = 0x47814940;

    private const string ProfilePrefix = "Afterglow - ";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus SessionOutDelegate(out nint session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus SessionDelegate(nint session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus FindProfileByNameDelegate(nint session, char* name, out nint profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus CreateProfileDelegate(nint session, ref NvdrsProfile profile, out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus DeleteProfileDelegate(nint session, nint profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus CreateApplicationDelegate(nint session, nint profile, ref NvdrsApplication app);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus FindApplicationByNameDelegate(
        nint session, char* appName, out nint profile, ref NvdrsApplication app);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus SetSettingDelegate(nint session, nint profile, ref NvdrsSetting setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus GetSettingDelegate(nint session, nint profile, uint settingId, ref NvdrsSetting setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus DeleteProfileSettingDelegate(nint session, nint profile, uint settingId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus GetProfileInfoDelegate(nint session, nint profile, ref NvdrsProfile info);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NvdrsProfile
    {
        public uint Version;
        public fixed char ProfileName[2048];
        public uint GpuSupport;
        public uint IsPredefined;
        public uint NumOfApps;
        public uint NumOfSettings;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NvdrsApplication   // NVDRS_APPLICATION_V4 — what current drivers expect for find/create
    {
        public uint Version;
        public uint IsPredefined;
        public fixed char AppName[2048];
        public fixed char UserFriendlyName[2048];
        public fixed char Launcher[2048];
        public fixed char FileInFolder[2048];
        public uint Flags;                 // bit 0 isMetro, bit 1 isCommandLine
        public fixed char CommandLine[2048];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NvdrsSetting
    {
        public uint Version;
        public fixed char SettingName[2048];
        public uint SettingId;
        public uint SettingType;             // 0 = DWORD
        public uint SettingLocation;
        public uint IsCurrentPredefined;
        public uint IsPredefinedValid;
        public uint PredefinedValue;
        public fixed byte PredefinedPad[4096];
        public uint CurrentValue;
        public fixed byte CurrentPad[4096];
    }

    private readonly SessionOutDelegate _createSession;
    private readonly SessionDelegate _destroySession;
    private readonly SessionDelegate _loadSettings;
    private readonly SessionDelegate _saveSettings;
    private readonly FindProfileByNameDelegate _findProfileByName;
    private readonly CreateProfileDelegate _createProfile;
    private readonly DeleteProfileDelegate _deleteProfile;
    private readonly CreateApplicationDelegate _createApplication;
    private readonly FindApplicationByNameDelegate _findApplicationByName;
    private readonly SetSettingDelegate _setSetting;
    private readonly GetSettingDelegate _getSetting;
    private readonly DeleteProfileSettingDelegate _deleteProfileSetting;
    private readonly GetProfileInfoDelegate _getProfileInfo;

    private DrsApi(
        SessionOutDelegate createSession, SessionDelegate destroySession, SessionDelegate loadSettings,
        SessionDelegate saveSettings, FindProfileByNameDelegate findProfileByName,
        CreateProfileDelegate createProfile, DeleteProfileDelegate deleteProfile,
        CreateApplicationDelegate createApplication, FindApplicationByNameDelegate findApplicationByName,
        SetSettingDelegate setSetting, GetSettingDelegate getSetting,
        DeleteProfileSettingDelegate deleteProfileSetting, GetProfileInfoDelegate getProfileInfo)
    {
        _createSession = createSession;
        _destroySession = destroySession;
        _loadSettings = loadSettings;
        _saveSettings = saveSettings;
        _findProfileByName = findProfileByName;
        _createProfile = createProfile;
        _deleteProfile = deleteProfile;
        _createApplication = createApplication;
        _findApplicationByName = findApplicationByName;
        _setSetting = setSetting;
        _getSetting = getSetting;
        _deleteProfileSetting = deleteProfileSetting;
        _getProfileInfo = getProfileInfo;
    }

    public static DrsApi? TryCreate(out NvapiStatus status)
    {
        // Layout self-checks: a marshaling regression must fail loudly here,
        // never as silent driver-store corruption.
        if (sizeof(NvdrsProfile) != 4116 || sizeof(NvdrsApplication) != 20492 || sizeof(NvdrsSetting) != 12320)
        {
            status = NvapiStatus.IncompatibleStructVersion;
            return null;
        }

        var initialize = NvapiNative.GetDelegate<NvapiNative.InitializeDelegate>(NvapiIds.Initialize);
        if (initialize is null)
        {
            status = NvapiStatus.LibraryNotFound;
            return null;
        }

        status = initialize();
        if (status != NvapiStatus.Ok)
        {
            return null;
        }

        var createSession = NvapiNative.GetDelegate<SessionOutDelegate>(IdCreateSession);
        var destroySession = NvapiNative.GetDelegate<SessionDelegate>(IdDestroySession);
        var loadSettings = NvapiNative.GetDelegate<SessionDelegate>(IdLoadSettings);
        var saveSettings = NvapiNative.GetDelegate<SessionDelegate>(IdSaveSettings);
        var findProfileByName = NvapiNative.GetDelegate<FindProfileByNameDelegate>(IdFindProfileByName);
        var createProfile = NvapiNative.GetDelegate<CreateProfileDelegate>(IdCreateProfile);
        var deleteProfile = NvapiNative.GetDelegate<DeleteProfileDelegate>(IdDeleteProfile);
        var createApplication = NvapiNative.GetDelegate<CreateApplicationDelegate>(IdCreateApplication);
        var findApplicationByName = NvapiNative.GetDelegate<FindApplicationByNameDelegate>(IdFindApplicationByName);
        var setSetting = NvapiNative.GetDelegate<SetSettingDelegate>(IdSetSetting);
        var getSetting = NvapiNative.GetDelegate<GetSettingDelegate>(IdGetSetting);
        var deleteProfileSetting = NvapiNative.GetDelegate<DeleteProfileSettingDelegate>(IdDeleteProfileSetting);
        var getProfileInfo = NvapiNative.GetDelegate<GetProfileInfoDelegate>(IdGetProfileInfo);

        if (createSession is null || destroySession is null || loadSettings is null || saveSettings is null ||
            findProfileByName is null || createProfile is null || deleteProfile is null ||
            createApplication is null || findApplicationByName is null || setSetting is null ||
            getSetting is null || deleteProfileSetting is null || getProfileInfo is null)
        {
            status = NvapiStatus.FunctionNotFound;
            return null;
        }

        return new DrsApi(
            createSession, destroySession, loadSettings, saveSettings, findProfileByName, createProfile,
            deleteProfile, createApplication, findApplicationByName, setSetting, getSetting,
            deleteProfileSetting, getProfileInfo);
    }

    /// <summary>
    /// Writes the given settings onto the DRS application profile for
    /// <paramref name="exeName"/> (creating an "Afterglow - exe" profile when
    /// the driver has none), then re-reads every value from a fresh session to
    /// verify the store took it.
    /// </summary>
    public NvapiStatus ApplySettings(string exeName, GameDriverSettings settings, out string note)
    {
        note = string.Empty;
        var status = WithSession((session) =>
        {
            var rc = FindOrCreateProfile(session, exeName, out nint profile, out bool created);
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            rc = ApplyOne(session, profile, FrameRateLimiterId, settings.FrameCapFps > 0,
                (uint)Math.Clamp(settings.FrameCapFps, 0, 1000));
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            uint vsync = settings.Vsync switch
            {
                "on" => VsyncForceOn,
                "off" => VsyncForceOff,
                _ => VsyncApplicationControlled,
            };
            rc = ApplyOne(session, profile, VsyncModeId, settings.Vsync is "on" or "off", vsync);
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            rc = ApplyOne(session, profile, PreRenderedFramesId, settings.LowLatency, 1);
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            return _saveSettings(session);
        });

        if (status != NvapiStatus.Ok)
        {
            return status;
        }

        // Verification round-trip from a fresh session.
        var readBack = ReadSettings(exeName, out var actual);
        if (readBack != NvapiStatus.Ok)
        {
            note = "write succeeded but readback failed";
            return readBack;
        }

        bool ok = actual.FrameCapFps == (settings.FrameCapFps > 0 ? settings.FrameCapFps : 0) &&
                  actual.Vsync == (settings.Vsync is "on" or "off" ? settings.Vsync : "default") &&
                  actual.LowLatency == settings.LowLatency;
        note = ok ? "verified" : $"readback mismatch (got cap={actual.FrameCapFps}, vsync={actual.Vsync}, lowlat={actual.LowLatency})";
        return ok ? NvapiStatus.Ok : NvapiStatus.Error;
    }

    /// <summary>Reads the three Afterglow-managed settings for an executable.</summary>
    public NvapiStatus ReadSettings(string exeName, out GameDriverSettings settings)
    {
        int cap = 0;
        string vsync = "default";
        bool lowLatency = false;

        var status = WithSession((session) =>
        {
            var app = default(NvdrsApplication);
            app.Version = MakeVersion(sizeof(NvdrsApplication), 4);
            nint profile;
            NvapiStatus rc;
            fixed (char* name = exeName.ToLowerInvariant())
            {
                rc = _findApplicationByName(session, name, out profile, ref app);
            }

            if (rc != NvapiStatus.Ok)
            {
                return rc;   // ExecutableNotFound and friends -> no profile
            }

            if (TryGetDword(session, profile, FrameRateLimiterId, out uint frl))
            {
                cap = (int)frl;
            }

            if (TryGetDword(session, profile, VsyncModeId, out uint v))
            {
                vsync = v switch
                {
                    VsyncForceOn => "on",
                    VsyncForceOff => "off",
                    _ => "default",
                };
            }

            if (TryGetDword(session, profile, PreRenderedFramesId, out uint prerender))
            {
                lowLatency = prerender == 1;
            }

            return NvapiStatus.Ok;
        });

        settings = new GameDriverSettings { FrameCapFps = cap, Vsync = vsync, LowLatency = lowLatency };
        return status;
    }

    /// <summary>
    /// Removes Afterglow's settings for an executable: a profile Afterglow
    /// created is deleted outright; on a pre-existing (driver-predefined or
    /// user) profile only the three managed settings are removed.
    /// </summary>
    public NvapiStatus ClearSettings(string exeName)
    {
        return WithSession((session) =>
        {
            var app = default(NvdrsApplication);
            app.Version = MakeVersion(sizeof(NvdrsApplication), 4);
            nint profile;
            NvapiStatus rc;
            fixed (char* name = exeName.ToLowerInvariant())
            {
                rc = _findApplicationByName(session, name, out profile, ref app);
            }

            if (rc != NvapiStatus.Ok)
            {
                return NvapiStatus.Ok;   // nothing to clear
            }

            string profileName = GetProfileNameFromApp(session, profile);
            if (profileName.StartsWith(ProfilePrefix, StringComparison.Ordinal))
            {
                _ = _deleteProfile(session, profile);
            }
            else
            {
                _ = _deleteProfileSetting(session, profile, FrameRateLimiterId);
                _ = _deleteProfileSetting(session, profile, VsyncModeId);
                _ = _deleteProfileSetting(session, profile, PreRenderedFramesId);
            }

            return _saveSettings(session);
        });
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvapiStatus EnumProfilesDelegate(nint session, uint index, out nint profile);

    /// <summary>Diagnostic: every profile name in the store that contains the filter.</summary>
    public IReadOnlyList<string> ProbeListProfiles(string contains)
    {
        var names = new List<string>();
        var enumProfiles = NvapiNative.GetDelegate<EnumProfilesDelegate>(IdEnumProfiles);
        if (enumProfiles is null)
        {
            return names;
        }

        int total = 0;
        int infoOk = 0;
        var lastInfoStatus = NvapiStatus.Ok;
        _ = WithSession((session) =>
        {
            for (uint i = 0; i < 20000; i++)
            {
                if (enumProfiles(session, i, out nint profile) != NvapiStatus.Ok)
                {
                    break;
                }

                total++;
                var info = default(NvdrsProfile);
                info.Version = MakeVersion(sizeof(NvdrsProfile), 1);
                lastInfoStatus = _getProfileInfo(session, profile, ref info);
                if (lastInfoStatus != NvapiStatus.Ok)
                {
                    continue;
                }

                infoOk++;
                string name = ReadFixedString(info.ProfileName, 2048);
                if (contains.Length == 0 || name.Contains(contains, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return NvapiStatus.Ok;
        });

        names.Insert(0, $"[probe: {total} profiles enumerated, {infoOk} GetProfileInfo ok, last status {lastInfoStatus}]");
        return names;
    }

    /// <summary>Diagnostic: create a bare profile, save, re-find from a fresh session, delete.</summary>
    public string ProbeCreate(string profileName)
    {
        var createRc = NvapiStatus.Ok;
        var saveRc = NvapiStatus.Ok;
        string writtenBack = string.Empty;
        _ = WithSession((session) =>
        {
            var newProfile = default(NvdrsProfile);
            newProfile.Version = MakeVersion(sizeof(NvdrsProfile), 1);
            newProfile.GpuSupport = 0x1;   // Geforce
            WriteFixedString(newProfile.ProfileName, profileName);
            writtenBack = ReadFixedString(newProfile.ProfileName, 2048);
            createRc = _createProfile(session, ref newProfile, out _);
            saveRc = createRc == NvapiStatus.Ok ? _saveSettings(session) : NvapiStatus.Ok;
            return NvapiStatus.Ok;
        });

        var findRc = ProbeFindProfile(profileName);

        var deleteRc = WithSession((session) =>
        {
            fixed (char* name = profileName)
            {
                if (_findProfileByName(session, name, out nint handle) != NvapiStatus.Ok)
                {
                    return NvapiStatus.ProfileNotFound;
                }

                var rc = _deleteProfile(session, handle);
                return rc != NvapiStatus.Ok ? rc : _saveSettings(session);
            }
        });

        return $"nameInStruct='{writtenBack}' create={createRc} save={saveRc} refind={findRc} delete+save={deleteRc}";
    }

    /// <summary>Diagnostic: raw FindProfileByName status for an exact profile name.</summary>
    public NvapiStatus ProbeFindProfile(string profileName)
    {
        return WithSession((session) =>
        {
            fixed (char* name = profileName)
            {
                return _findProfileByName(session, name, out _);
            }
        });
    }

    private NvapiStatus WithSession(Func<nint, NvapiStatus> body)
    {
        var rc = _createSession(out nint session);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        try
        {
            rc = _loadSettings(session);
            return rc != NvapiStatus.Ok ? rc : body(session);
        }
        finally
        {
            _ = _destroySession(session);
        }
    }

    private NvapiStatus FindOrCreateProfile(nint session, string exeName, out nint profile, out bool created)
    {
        created = false;
        var app = default(NvdrsApplication);
        app.Version = MakeVersion(sizeof(NvdrsApplication), 4);
        NvapiStatus rc;
        fixed (char* name = exeName.ToLowerInvariant())
        {
            rc = _findApplicationByName(session, name, out profile, ref app);
        }

        if (rc == NvapiStatus.Ok)
        {
            return NvapiStatus.Ok;
        }

        // No driver profile knows this exe: create our own and attach the app.
        string profileName = ProfilePrefix + exeName.ToLowerInvariant();
        var newProfile = default(NvdrsProfile);
        newProfile.Version = MakeVersion(sizeof(NvdrsProfile), 1);
        WriteFixedString(newProfile.ProfileName, profileName);
        rc = _createProfile(session, ref newProfile, out profile);
        if (rc != NvapiStatus.Ok)
        {
            // Name collision from a previous run: reuse it.
            fixed (char* name = profileName)
            {
                if (_findProfileByName(session, name, out profile) != NvapiStatus.Ok)
                {
                    return rc;
                }
            }
        }

        var newApp = default(NvdrsApplication);
        newApp.Version = MakeVersion(sizeof(NvdrsApplication), 4);
        WriteFixedString(newApp.AppName, exeName.ToLowerInvariant());
        rc = _createApplication(session, profile, ref newApp);
        created = true;
        return rc;
    }

    private NvapiStatus ApplyOne(nint session, nint profile, uint settingId, bool enabled, uint value)
    {
        if (!enabled)
        {
            // Not-found is fine — the knob simply wasn't set before.
            _ = _deleteProfileSetting(session, profile, settingId);
            return NvapiStatus.Ok;
        }

        var setting = default(NvdrsSetting);
        setting.Version = MakeVersion(sizeof(NvdrsSetting), 1);
        setting.SettingId = settingId;
        setting.SettingType = 0;   // DWORD
        setting.CurrentValue = value;
        return _setSetting(session, profile, ref setting);
    }

    private bool TryGetDword(nint session, nint profile, uint settingId, out uint value)
    {
        var setting = default(NvdrsSetting);
        setting.Version = MakeVersion(sizeof(NvdrsSetting), 1);
        if (_getSetting(session, profile, settingId, ref setting) == NvapiStatus.Ok)
        {
            value = setting.CurrentValue;
            return true;
        }

        value = 0;
        return false;
    }

    private string GetProfileNameFromApp(nint session, nint profile)
    {
        var info = default(NvdrsProfile);
        info.Version = MakeVersion(sizeof(NvdrsProfile), 1);
        if (_getProfileInfo(session, profile, ref info) != NvapiStatus.Ok)
        {
            return string.Empty;
        }

        return ReadFixedString(info.ProfileName, 2048);
    }

    private static string ReadFixedString(char* source, int capacity)
    {
        int length = 0;
        while (length < capacity && source[length] != '\0')
        {
            length++;
        }

        return new string(source, 0, length);
    }

    private static uint MakeVersion(int size, int version) => (uint)size | ((uint)version << 16);

    private static void WriteFixedString(char* destination, string value)
    {
        int count = Math.Min(value.Length, 2047);
        for (int i = 0; i < count; i++)
        {
            destination[i] = value[i];
        }

        destination[count] = '\0';
    }
}
