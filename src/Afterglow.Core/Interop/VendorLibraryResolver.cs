using System.Runtime.InteropServices;

namespace Afterglow.Core.Interop;

/// <summary>
/// One DllImport resolver for every vendor driver library Afterglow loads.
///
/// <para>
/// Afterglow runs elevated to write GPU state, so it must never let the default
/// probe order decide where a driver library comes from: that order searches the
/// application directory (and other caller-writable locations) before System32,
/// which lets anyone who can drop a file next to the executable — or into a
/// user-writable directory on PATH — get their code loaded into an elevated
/// process that talks to the GPU driver. Every one of these libraries ships with
/// its vendor's driver and belongs in System32, so each is pinned there by full
/// path and simply fails to load if it is absent.
/// </para>
/// <para>
/// This lives in one place because .NET allows only a single
/// <see cref="NativeLibrary.SetDllImportResolver"/> per assembly — a second call
/// throws. NVML previously owned that one slot, which is why the Intel libraries
/// added in the Arc release fell back to default probing and were the only
/// unpinned native dependencies in the process.
/// </para>
/// </summary>
internal static class VendorLibraryResolver
{
    internal const string NvmlLib = "nvml.dll";
    internal const string IgclLib = "ControlLib.dll";
    internal const string ZeLoaderLib = "ze_loader.dll";

    private static int _installed;

    /// <summary>
    /// Installs the resolver for this assembly exactly once. Must be called
    /// before the first P/Invoke into any vendor library; every vendor entry
    /// point (NVML, IGCL, Level Zero Sysman) calls it during initialization.
    /// </summary>
    internal static void EnsureInstalled()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(VendorLibraryResolver).Assembly, static (name, _, _) =>
        {
            if (name.Equals(NvmlLib, StringComparison.OrdinalIgnoreCase))
            {
                // MUST throw rather than return IntPtr.Zero. Zero means "I did
                // not resolve this" and the runtime then falls back to DEFAULT
                // probing — which searches the application directory first. In a
                // process that self-elevates, that is the exact DLL-planting
                // hole this resolver exists to close: on a machine with no
                // NVIDIA driver, a file named nvml.dll dropped beside the
                // executable would be loaded and run its DllMain as
                // Administrator. Throwing stops resolution dead; every vendor
                // entry point already treats DllNotFoundException as
                // "this vendor is unavailable".
                return ResolveNvml()
                    ?? throw new DllNotFoundException(
                        $"{NvmlLib} was not found in System32 or the legacy NVSMI folder. " +
                        "Afterglow loads vendor driver libraries only from their installed locations.");
            }

            // IGCL (ControlLib.dll) and the Level Zero loader (ze_loader.dll)
            // are installed into System32 by the Intel graphics driver. There is
            // no documented secondary location, so there is no fallback: an
            // absent library reports "not found" and Afterglow degrades to the
            // vendors it can reach, exactly as it does without an NVIDIA driver.
            if (name.Equals(IgclLib, StringComparison.OrdinalIgnoreCase)
                || name.Equals(ZeLoaderLib, StringComparison.OrdinalIgnoreCase))
            {
                return NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, name), out nint intelHandle)
                    ? intelHandle
                    : throw new DllNotFoundException(
                        $"{name} was not found in System32. Afterglow loads vendor driver libraries " +
                        "only from their installed locations.");
            }

            // Not one of ours: let the runtime resolve it normally.
            return IntPtr.Zero;
        });
    }

    /// <summary>
    /// NVML resolution, unchanged from when it lived in NvmlNative: System32
    /// since driver R445+, falling back to the legacy NVSMI folder used by very
    /// old drivers.
    /// </summary>
    private static nint? ResolveNvml()
    {
        if (NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, NvmlLib), out nint handle))
        {
            return handle;
        }

        string legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", NvmlLib);
        return NativeLibrary.TryLoad(legacy, out handle) ? handle : null;
    }
}
