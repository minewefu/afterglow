using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Afterglow.Core.Stress;

public sealed record VramProgress(
    StressState State,
    TimeSpan Elapsed,
    long PlannedBytes,
    long VerifiedBytes,
    int Rounds,
    long ErrorCount,
    double GiBPerSecond,
    string? Detail);

/// <summary>
/// Full-capacity VRAM test: allocates as much of the card's memory budget as
/// the OS will safely give out, fills it with a deterministic hash pattern, and
/// verifies every element on the GPU (a tiny error counter is the only
/// readback). Each round re-fills with a different pattern — alternate rounds
/// use the bit-inverted hash so every cell is exercised in both directions.
/// Allocation deliberately stays inside the DXGI budget: overcommitting would
/// make Windows page chunks to system RAM and silently fake the coverage.
/// </summary>
public sealed class VramTest : IDisposable
{
    private const int ThreadCount = 1 << 20;
    private const int ThreadsPerGroup = 256;
    private const long ChunkBytes = 1L << 30;          // 1 GiB per buffer
    private const long MinChunkBytes = 256L << 20;     // smallest tail chunk
    private const long SafetyReserveBytes = 1536L << 20;

    private const string FillShaderSource = """
        RWStructuredBuffer<uint4> buf : register(u0);
        cbuffer Params : register(b0) { uint count; uint seed; uint invert; uint pad; }

        uint hash(uint x)
        {
            x ^= x >> 16; x *= 0x7FEB352Du;
            x ^= x >> 15; x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            for (uint i = id.x; i < count; i += 1u << 20)
            {
                uint h0 = hash(i ^ seed);
                uint h1 = hash(h0 + 0x9E3779B9u);
                uint4 v = uint4(h0, h1, h0 ^ h1, hash(h1));
                buf[i] = invert != 0u ? ~v : v;
            }
        }
        """;

    private const string VerifyShaderSource = """
        RWStructuredBuffer<uint4> buf : register(u0);
        RWStructuredBuffer<uint> errs : register(u1);
        cbuffer Params : register(b0) { uint count; uint seed; uint invert; uint pad; }

        uint hash(uint x)
        {
            x ^= x >> 16; x *= 0x7FEB352Du;
            x ^= x >> 15; x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            for (uint i = id.x; i < count; i += 1u << 20)
            {
                uint h0 = hash(i ^ seed);
                uint h1 = hash(h0 + 0x9E3779B9u);
                uint4 expected = uint4(h0, h1, h0 ^ h1, hash(h1));
                if (invert != 0u) expected = ~expected;
                uint4 actual = buf[i];
                if (any(actual != expected))
                {
                    InterlockedAdd(errs[0], 1u);
                    InterlockedCompareStore(errs[1], 0xFFFFFFFFu, i);
                }
            }
        }
        """;

    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _stop;
    private VramProgress _progress = new(StressState.Idle, TimeSpan.Zero, 0, 0, 0, 0, 0, null);

    /// <summary>
    /// PCI bus of the card being tuned; binds the test to that exact adapter.
    /// Null keeps the largest-VRAM fallback for the target vendor.
    /// </summary>
    public uint? TargetPciBusId { get; set; }

    /// <summary>PCI vendor of the card being tuned (defaults to NVIDIA).</summary>
    public uint TargetVendorId { get; set; } = StressAdapter.NvidiaVendorId;

    public event Action<VramProgress>? ProgressChanged;

    public VramProgress Progress
    {
        get
        {
            lock (_lock)
            {
                return _progress;
            }
        }
    }

    public bool IsRunning => _thread is { IsAlive: true } && !_stop;

    /// <summary>
    /// Splits the safely-allocatable region into chunk sizes. Target is the
    /// DXGI budget minus what's already in use minus a safety reserve (so the
    /// desktop keeps working and nothing gets paged out), additionally capped
    /// at 95% of dedicated VRAM.
    /// </summary>
    public static long[] PlanChunks(long budgetBytes, long currentUsageBytes, long dedicatedBytes)
    {
        long target = Math.Min(
            budgetBytes - currentUsageBytes - SafetyReserveBytes,
            dedicatedBytes * 95 / 100);
        return ChunksFor(target);
    }

    /// <summary>
    /// Planner for unified-memory (UMA) GPUs, where "VRAM" is a budget carved
    /// from system RAM: the dedicated-VRAM cap would be meaningless, and the
    /// safety reserve must be much larger because every byte tested is a byte
    /// taken from the OS and applications. A quarter of the budget (at least
    /// the normal reserve) stays free, and the plan is additionally capped by
    /// what is actually physically free right now — the DXGI budget alone can
    /// exceed free RAM on a loaded low-memory machine, and UMA chunks are
    /// pageable, so overshooting would page the system AND let tested bytes
    /// silently round-trip through the pagefile instead of RAM.
    /// </summary>
    public static long[] PlanChunksShared(long budgetBytes, long currentUsageBytes, long availablePhysicalBytes)
    {
        long reserve = Math.Max(SafetyReserveBytes, budgetBytes / 4);
        long target = Math.Min(
            budgetBytes - currentUsageBytes - reserve,
            availablePhysicalBytes - SafetyReserveBytes);
        return ChunksFor(target);
    }

    /// <summary>
    /// Free physical RAM right now, via the documented GlobalMemoryStatusEx.
    /// long.MaxValue on failure so the budget-based caps decide alone.
    /// </summary>
    internal static long AvailablePhysicalBytes()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : long.MaxValue;
        }
        catch (DllNotFoundException)
        {
            return long.MaxValue;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private static long[] ChunksFor(long target)
    {
        if (target < MinChunkBytes)
        {
            return [];
        }

        var chunks = new List<long>();
        long remaining = target;
        while (remaining >= ChunkBytes)
        {
            chunks.Add(ChunkBytes);
            remaining -= ChunkBytes;
        }

        if (remaining >= MinChunkBytes)
        {
            chunks.Add(remaining & ~15L);   // 16-byte element granularity
        }

        return [.. chunks];
    }

    public void Start()
    {
        // Liveness guard (see GpuStressTest.Start): never resurrect a stopping
        // worker while spawning a second one.
        if (_thread is { IsAlive: true })
        {
            return;
        }

        _stop = false;
        _thread = new Thread(Run)
        {
            Name = "Afterglow vram test",
            IsBackground = true,
        };
        _thread.Start();
    }

    public void Stop() => _stop = true;

    public void StopAndWait(TimeSpan timeout)
    {
        _stop = true;
        _thread?.Join(timeout);
    }

    private void Report(
        StressState state, TimeSpan elapsed, long planned, long verified, int rounds,
        long errors, double gibps, string? detail = null)
    {
        var progress = new VramProgress(state, elapsed, planned, verified, rounds, errors, gibps, detail);
        lock (_lock)
        {
            _progress = progress;
        }

        ProgressChanged?.Invoke(progress);
    }

    private unsafe void Run()
    {
        var stopwatch = Stopwatch.StartNew();
        long planned = 0;
        long verified = 0;
        int rounds = 0;
        long errors = 0;

        try
        {
            using var targetAdapter = StressAdapter.Select(TargetVendorId, TargetPciBusId, out string adapterName);
            if (targetAdapter is null)
            {
                Report(StressState.Failed, stopwatch.Elapsed, 0, 0, 0, 0, 0,
                    adapterName.Length > 0
                        ? $"Adapter binding failed: {adapterName}"
                        : $"No {StressAdapter.VendorName(TargetVendorId)} adapter found — refusing to run the VRAM test on a different GPU.");
                return;
            }

            Diagnostics.Log.Info($"VRAM-test adapter: {adapterName}");

            var result = D3D11.D3D11CreateDevice(
                targetAdapter, DriverType.Unknown, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0],
                out ID3D11Device? device, out ID3D11DeviceContext? context);
            if (result.Failure || device is null || context is null)
            {
                Report(StressState.Failed, stopwatch.Elapsed, 0, 0, 0, 0, 0,
                    $"D3D11 device creation failed on {adapterName}: {result}");
                return;
            }

            using (device)
            using (context)
            {
                long dedicated;
                long budget;
                long usage;
                using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
                using (var adapter = dxgiDevice.GetAdapter())
                {
                    dedicated = (long)(ulong)adapter.Description.DedicatedVideoMemory;
                    using var adapter3 = adapter.QueryInterface<IDXGIAdapter3>();
                    var info = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                    budget = (long)info.Budget;
                    usage = (long)info.CurrentUsage;
                }

                // The device itself says whether its memory is unified with
                // system RAM. On UMA the dedicated-VRAM planner would either
                // produce a sliver (fake coverage) or misread the budget, so
                // a shared-budget planner runs instead — and the result says
                // plainly what was tested.
                bool unifiedMemory = device.CheckFeatureSupport<Vortice.Direct3D11.FeatureDataD3D11Options2>(
                    Vortice.Direct3D11.Feature.D3D11Options2).UnifiedMemoryArchitecture;

                long[] plan = unifiedMemory
                    ? PlanChunksShared(budget, usage, AvailablePhysicalBytes())
                    : PlanChunks(budget, usage, dedicated);
                string? memoryNote = unifiedMemory
                    ? "tested the GPU's shared system-memory budget (UMA) — this device has no dedicated VRAM"
                    : null;
                if (plan.Length == 0)
                {
                    Report(StressState.Failed, stopwatch.Elapsed, 0, 0, 0, 0, 0,
                        unifiedMemory
                            ? "Not enough free shared-memory budget to test (close memory-heavy applications and retry)."
                            : "Not enough free VRAM to test (close GPU-heavy applications and retry).");
                    return;
                }

                using var fillShader = device.CreateComputeShader(
                    Compiler.Compile(FillShaderSource, "main", "vram-fill", "cs_5_0").Span);
                using var verifyShader = device.CreateComputeShader(
                    Compiler.Compile(VerifyShaderSource, "main", "vram-verify", "cs_5_0").Span);

                var errDesc = new BufferDescription(
                    8, BindFlags.UnorderedAccess, ResourceUsage.Default,
                    CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4);
                using var errBuffer = device.CreateBuffer(errDesc);
                using var errUav = device.CreateUnorderedAccessView(errBuffer);
                var errStagingDesc = new BufferDescription(
                    8, BindFlags.None, ResourceUsage.Staging,
                    CpuAccessFlags.Read | CpuAccessFlags.Write, ResourceOptionFlags.BufferStructured, 4);
                using var errStaging = device.CreateBuffer(errStagingDesc);

                // Allocate the plan; if the OS refuses a chunk, test what we got.
                var buffers = new List<(ID3D11Buffer Buffer, ID3D11UnorderedAccessView Uav, uint Elements)>();
                try
                {
                    foreach (long size in plan)
                    {
                        if (_stop)
                        {
                            break;
                        }

                        try
                        {
                            var desc = new BufferDescription(
                                (uint)size, BindFlags.UnorderedAccess, ResourceUsage.Default,
                                CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16);
                            var buffer = device.CreateBuffer(desc);
                            var uav = device.CreateUnorderedAccessView(buffer);
                            buffers.Add((buffer, uav, (uint)(size / 16)));
                            planned += size;
                            Report(StressState.Running, stopwatch.Elapsed, planned, 0, 0, 0, 0, "allocating");
                        }
                        catch (SharpGenException)
                        {
                            break;   // out of allocatable memory — proceed with what we have
                        }
                    }

                    if (buffers.Count == 0)
                    {
                        Report(StressState.Failed, stopwatch.Elapsed, 0, 0, 0, 0, 0,
                            "VRAM allocation failed before any chunk could be created.");
                        return;
                    }

                    uint[] zeroErrs = [0, 0xFFFFFFFF];

                    void WriteParams(ID3D11Buffer constants, uint count, uint seed, uint invert)
                    {
                        uint[] values = [count, seed, invert, 0];
                        context.UpdateSubresource(values, constants);
                    }

                    using var constants = device.CreateBuffer<uint>([0, 0, 0, 0],
                        BindFlags.ConstantBuffer, ResourceUsage.Default);

                    while (!_stop)
                    {
                        uint seed = (uint)(rounds * 0x1000193 + 0x811C9DC5);
                        uint invert = (uint)(rounds & 1);

                        // Fill every chunk, then verify every chunk: the data
                        // rests in VRAM for the whole fill phase before its
                        // readback, which also exercises retention.
                        for (int i = 0; i < buffers.Count && !_stop; i++)
                        {
                            WriteParams(constants, buffers[i].Elements, seed, invert);
                            context.CSSetShader(fillShader);
                            context.CSSetConstantBuffer(0, constants);
                            context.CSSetUnorderedAccessView(0, buffers[i].Uav);
                            context.Dispatch(ThreadCount / ThreadsPerGroup, 1, 1);
                            context.CSSetUnorderedAccessView(0, null);
                            context.Flush();
                        }

                        context.UpdateSubresource(zeroErrs, errBuffer);

                        for (int i = 0; i < buffers.Count && !_stop; i++)
                        {
                            WriteParams(constants, buffers[i].Elements, seed, invert);
                            context.CSSetShader(verifyShader);
                            context.CSSetConstantBuffer(0, constants);
                            context.CSSetUnorderedAccessView(0, buffers[i].Uav);
                            context.CSSetUnorderedAccessView(1, errUav);
                            context.Dispatch(ThreadCount / ThreadsPerGroup, 1, 1);
                            context.CSSetUnorderedAccessView(0, null);
                            context.CSSetUnorderedAccessView(1, null);
                            context.Flush();

                            verified += (long)buffers[i].Elements * 16;
                            double gibps = verified / Math.Max(0.2, stopwatch.Elapsed.TotalSeconds) / (1L << 30);
                            Report(StressState.Running, stopwatch.Elapsed, planned, verified, rounds, errors, gibps,
                                $"round {rounds + 1}, chunk {i + 1}/{buffers.Count}");
                        }

                        if (_stop)
                        {
                            break;
                        }

                        // One tiny readback per round: the error counter.
                        context.CopyResource(errStaging, errBuffer);
                        var mapped = context.Map(errStaging, 0, MapMode.Read);
                        uint roundErrors;
                        uint firstIndex;
                        try
                        {
                            uint* data = (uint*)mapped.DataPointer;
                            roundErrors = data[0];
                            firstIndex = data[1];
                        }
                        finally
                        {
                            context.Unmap(errStaging, 0);
                        }

                        var removed = device.DeviceRemovedReason;
                        if (removed.Failure)
                        {
                            Report(StressState.DeviceLost, stopwatch.Elapsed, planned, verified, rounds, errors, 0,
                                $"The GPU device was removed/reset during the VRAM test (0x{removed.Code:X8}) — " +
                                "driver TDR. The current memory clocks are unstable.");
                            return;
                        }

                        if (roundErrors > 0)
                        {
                            errors += roundErrors;
                            // On UMA the tested memory IS system RAM: pointing at a GPU
                            // memory offset (which this device may not even expose)
                            // would misdirect the user away from the real suspect.
                            Report(StressState.ArtifactDetected, stopwatch.Elapsed, planned, verified, rounds, errors, 0,
                                unifiedMemory
                                    ? $"{errors} element(s) of the GPU's shared system-memory budget read back " +
                                      $"different data than was written (first at element {firstIndex} of the failing " +
                                      "chunk) — on this unified-memory device that memory is system RAM, so check " +
                                      "system-memory stability (XMP/EXPO profile, DRAM timings) before blaming the GPU."
                                    : $"{errors} VRAM element(s) read back different data than was written " +
                                      $"(first at element {firstIndex} of the failing chunk) — memory errors at the " +
                                      "current memory clock/offset. Lower the memory offset.");
                            return;
                        }

                        rounds++;
                    }

                    Report(StressState.Stopped, stopwatch.Elapsed, planned, verified, rounds, errors, 0, memoryNote);
                }
                finally
                {
                    foreach (var (buffer, uav, _) in buffers)
                    {
                        uav.Dispose();
                        buffer.Dispose();
                    }
                }
            }
        }
        catch (SharpGenException ex)
        {
            Report(
                ex.ResultCode.Code == unchecked((int)0x887A0005)
                    ? StressState.DeviceLost
                    : StressState.Failed,
                stopwatch.Elapsed, planned, verified, rounds, errors, 0, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
        {
            Report(StressState.Failed, stopwatch.Elapsed, planned, verified, rounds, errors, 0, ex.Message);
        }
        catch (Exception ex)
        {
            // Last resort — see GpuStressTest: never let a worker escape kill
            // the process.
            Report(StressState.Failed, stopwatch.Elapsed, planned, verified, rounds, errors, 0,
                $"Unexpected failure: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopAndWait(TimeSpan.FromSeconds(10));
    }
}
