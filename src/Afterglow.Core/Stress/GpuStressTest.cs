using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Afterglow.Core.Stress;

public enum StressState
{
    Idle,
    Running,
    Stopped,
    ArtifactDetected,
    DeviceLost,
    Failed,
}

public enum StressPattern
{
    /// <summary>Continuous full load — the classic burn for sustained-clock validation.</summary>
    Sustained,

    /// <summary>
    /// Load/idle cycling that forces P-state and memory-clock transitions — the
    /// regime where marginal memory offsets fail even though they pass any
    /// sustained burn. VRAM retention is re-verified across every transition.
    /// </summary>
    Transitions,

    /// <summary>
    /// Short saturating bursts with idle gaps: each burst rides the boost
    /// overshoot up through the top clock bins before power management clamps,
    /// then falls back to idle — sweeping the whole clock range dozens of
    /// times a minute. This is the bursty desktop regime behind "passed the
    /// stress test, crashed on the desktop".
    /// </summary>
    BoostExcursions,
}

public sealed record StressProgress(
    StressState State,
    TimeSpan Elapsed,
    double DispatchesPerSecond,
    long TotalDispatches,
    long ErrorCount,
    string? Detail,
    string? Phase = null,
    long Transitions = 0,

    /// <summary>
    /// Dispatches from the LOAD loop only. <see cref="TotalDispatches"/> counts
    /// the one-off reference pass too, so it is 1 even for a run whose load loop
    /// never executed — which makes it useless as a "did any work happen?" test.
    /// A run with no burn dispatches verified nothing: its closing check would
    /// compare the reference buffer against itself and match by construction.
    /// </summary>
    long BurnDispatches = 0,

    /// <summary>The pattern the engine ran; it decides what "completed" means.</summary>
    StressPattern Pattern = StressPattern.Sustained)
{
    /// <summary>
    /// The single definition of "this run passed": it ended normally AND
    /// <see cref="VerdictGap"/> finds nothing missing from the evidence.
    /// <para>
    /// Every pass/fail decision goes through this rather than testing
    /// <see cref="State"/> alone. Each verdict site having its own copy of the
    /// rule is how the gap arose in the first place — the work check reached the
    /// certifier, the MCP tool and the Stability page while the CLI exit code
    /// and the stepper kept passing a run that had burned nothing, because
    /// <see cref="TotalDispatches"/> counts a one-off reference pass and so is
    /// never zero. Note that a caller must still confirm the worker actually
    /// stopped (see <c>StopAndWait</c>); this property speaks only for the
    /// progress record it is on.
    /// </para>
    /// </summary>
    public bool IsCleanPass => State == StressState.Stopped && VerdictGap is null;

    /// <summary>True for the patterns that prove nothing until they have completed cycles.</summary>
    public bool IsCyclic => Pattern is StressPattern.Transitions or StressPattern.BoostExcursions;

    /// <summary>
    /// Why a run that ended normally still earned no verdict, or null when it
    /// did. One rule for every consumer: the certifier used to stamp a
    /// transition-cycling mode that completed zero cycles as a pass while the
    /// Stability page refused the same run, and the "ran no load dispatches"
    /// test lived in six hand-written copies.
    /// </summary>
    public string? VerdictGap =>
        BurnDispatches <= 0
            ? "the burn ran no load dispatches, so nothing was actually tested"
            : IsCyclic && Transitions <= 0
                ? Pattern == StressPattern.Transitions
                    ? "the burn completed no clock excursions — the first transitions cycle ends only at about " +
                      $"{GpuStressTest.TransitionsFirstCycleSeconds} s, so run at least 60 s — and this pattern's " +
                      "regime was never entered"
                    : "the burn completed no clock excursions, so this pattern's regime was never entered"
                : null;

    /// <summary>
    /// True when the engine produced a HARDWARE result: the load ran and the GPU
    /// misbehaved. This is the answer the tool exists to give.
    /// <para>
    /// Deliberately narrower than "a terminal state". The engine also publishes
    /// <see cref="StressState.Failed"/> when it could not run at all — no
    /// matching adapter, a refused guessed adapter, D3D device or shader
    /// creation failure, an unexpected exception — and when it gave up waiting
    /// for queued work. Those tested nothing, so they are the absence of a
    /// verdict, not a verdict. Treating the whole Failed class as a result told
    /// an agent "not stable, and not inconclusive" about a run that never
    /// created a device — and on the give-up path contradicted the engine's own
    /// detail text, which says the run is inconclusive.
    /// </para>
    /// </summary>
    public bool IsHardwareVerdict => State is StressState.ArtifactDetected or StressState.DeviceLost;
}

/// <summary>
/// Compute burn test with bit-exact error detection, designed to load both the
/// shader cores and the memory subsystem:
///  - a large source buffer (hundreds of MiB) is filled once, deterministically,
///    by an integer-hash init shader;
///  - each burn dispatch streams it with LCG-scrambled gathers (defeating cache
///    and prefetch, keeping the VRAM controller busy) and runs an FMA storm
///    between fetches (keeping the FP32 pipes busy);
///  - inputs never change, so every dispatch computes an identical output —
///    a slice is read back periodically and compared byte-for-byte against the
///    first dispatch. Any difference means the GPU is miscalculating.
/// A device-removed error (driver reset/TDR) is likewise caught and reported.
/// </summary>
public sealed class GpuStressTest : IProbeLoad
{
    private const int ThreadCount = 1 << 20;          // 1M threads → 16 MiB output
    private const int CheckElements = 16384;          // 256 KiB verify slice
    private const int ThreadsPerGroup = 256;

    /// <summary>
    /// How long a queued batch may go unretired before the run is called
    /// inconclusive. Generous: a heavy dispatch on a slow iGPU legitimately
    /// takes seconds. This is the backstop for a device that stops making
    /// progress without reporting a reset.
    /// </summary>
    private static readonly TimeSpan FenceWaitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Seconds before the transitions pattern completes its FIRST load/idle
    /// cycle (its first load phase plus its first idle phase, see the pattern
    /// loop). A shorter run cannot count an excursion, so it can never earn a
    /// verdict — the CLI refuses such a run up front and the MCP schema says so.
    /// </summary>
    public const int TransitionsFirstCycleSeconds = 24;

    /// <summary>
    /// The refusal for a run too short for its pattern to earn a verdict, or
    /// null when the length is fine. One rule for the CLI, the MCP tool and
    /// the engine's own no-verdict message, so the floor cannot be enforced
    /// on one surface and merely described on another.
    /// </summary>
    public static string? SecondsShortfall(StressPattern pattern, int seconds) =>
        pattern == StressPattern.Transitions && seconds < TransitionsFirstCycleSeconds
            ? $"the transitions pattern needs at least {TransitionsFirstCycleSeconds} s to complete one load/idle " +
              $"cycle (60 s or more is recommended); {seconds} s cannot earn a verdict"
            : null;

    private const string InitShaderSource = """
        RWStructuredBuffer<float4> src : register(u0);
        cbuffer Params : register(b0) { uint elementCount; uint pad0; uint pad1; uint pad2; };

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
            for (uint i = id.x; i < elementCount; i += 1u << 20)
            {
                uint h = hash(i);
                src[i] = float4(
                    (float)(h & 0xFFFFu) / 65536.0f,
                    (float)((h >> 16) & 0xFFFFu) / 65536.0f,
                    (float)(hash(h) & 0xFFFFu) / 65536.0f,
                    1.0f);
            }
        }
        """;

    private const string BurnShaderSource = """
        StructuredBuffer<float4> src : register(t0);
        RWStructuredBuffer<float4> dst : register(u0);
        cbuffer Params : register(b0)
        {
            uint fetches;      // gathers per thread (memory-load knob)
            uint aluRounds;    // FMA rounds per gather (compute-load knob)
            uint srcCount;
            float seed;
        };

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            // Four independent accumulator chains keep the FMA pipes issue-saturated
            // (a single chain is latency-bound and leaves execution width idle).
            float4 a0 = float4(seed, seed * 1.7f, seed * 2.3f, seed * 3.1f) + (float)id.x * 0.001f;
            float4 a1 = a0.wzyx + 0.25f;
            float4 a2 = a0.yxwz + 0.50f;
            float4 a3 = a0.zwxy + 0.75f;
            const float4 m0 = float4(1.0001f, 0.9999f, 1.0002f, 0.9998f);
            const float4 m1 = float4(0.9997f, 1.0003f, 0.9996f, 1.0004f);
            uint idx = id.x;
            // Integer chain rides the INT pipe concurrently with the FP32 FMA chains.
            uint4 h = uint4(id.x, id.x * 747796405u, id.x ^ 0x9E3779B9u, id.x + 0x85EBCA6Bu);
            [loop]
            for (uint j = 0; j < fetches; j++)
            {
                // LCG scramble → pseudo-random stride across the whole buffer.
                idx = idx * 1664525u + 1013904223u;
                float4 v = src[idx % srcCount];
                [loop]
                for (uint k = 0; k < aluRounds; k++)
                {
                    a0 = mad(a0, m0, v * 0.000001f);
                    a1 = mad(a1, m1, v.wzyx * 0.000001f);
                    a2 = mad(a2, m0.wzyx, v.yxwz * 0.000001f);
                    a3 = mad(a3, m1.wzyx, v.zwxy * 0.000001f);
                    h = h * 1664525u + 1013904223u;
                    h ^= h >> 13;
                    v = mad(v, a0.wzyx, float4(0.0001f, -0.0001f, 0.0002f, -0.0002f));
                }
                a0 += v * 0.001f;
            }
            a0 += (float4)(h & 1u) * 0.0000001f;
            dst[id.x] = a0 + a1 * 0.5f + a2 * 0.25f + a3 * 0.125f;
        }
        """;

    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _stop;
    private StressProgress _progress = new(StressState.Idle, TimeSpan.Zero, 0, 0, 0, null);

    /// <summary>
    /// Load knob (512 light … 8192 heavy). Maps to gathers-per-thread and FMA
    /// rounds so both the memory controller and the shader pipes scale with it.
    /// </summary>
    public uint IterationsPerDispatch { get; set; } = 4096;

    /// <summary>Source-buffer size in MiB (streamed working set; clamped to VRAM/6).</summary>
    public uint WorkingSetMiB { get; set; } = 512;

    /// <summary>Load shape — sustained burn, transition cycling, or boost excursions.</summary>
    public StressPattern Pattern { get; set; } = StressPattern.Sustained;

    /// <summary>
    /// PCI bus of the card being tuned; binds the burn to that exact adapter.
    /// Null keeps the largest-VRAM fallback for the target vendor.
    /// </summary>
    public uint? TargetPciBusId { get; set; }

    /// <summary>PCI vendor of the card being tuned (defaults to NVIDIA).</summary>
    public uint TargetVendorId { get; set; } = StressAdapter.NvidiaVendorId;

    /// <summary>
    /// Permit running on an adapter that could only be GUESSED (no PCI bus to
    /// bind to and more than one candidate of the vendor).
    /// <para>
    /// Defaults to false — refusing — because almost every caller attributes the
    /// result to a specific card, and burning the wrong GPU would stamp a
    /// verdict the tested card never earned. The safe behaviour has to be the
    /// default: as an opt-IN it was set on the certifier, stepper and V/F probe
    /// and forgotten on both MCP engines and the Stability page, which is how an
    /// agent could still be handed stable:true for a card the run never bound to.
    /// Only a deliberately unbound exploratory run — CLI `stress`/`vram` with no
    /// --gpu, where the historical largest-VRAM fallback is the documented
    /// behaviour — sets this true.
    /// </para>
    /// </summary>
    public bool AllowUnboundGuess { get; set; }

    public event Action<StressProgress>? ProgressChanged;

    public StressProgress Progress
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

    public void Start()
    {
        // Guard on thread liveness, not IsRunning: after Stop() the worker can
        // still be winding down, and un-stopping it while spawning a second
        // thread would run two burns against one device.
        if (_thread is { IsAlive: true })
        {
            return;
        }

        _stop = false;
        _thread = new Thread(Run)
        {
            Name = "Afterglow stress",
            IsBackground = true,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
    }

    /// <summary>
    /// Blocks until the burn thread has finished (used by the stepper).
    /// Returns false when the worker was STILL RUNNING at the timeout: the burn
    /// was abandoned, so <see cref="Progress"/> is a stale mid-run snapshot and
    /// the run must never be read as a clean pass. Callers that decide
    /// pass/fail must treat false as "no verdict", not as success.
    /// </summary>
    public bool StopAndWait(TimeSpan timeout)
    {
        _stop = true;
        return _thread?.Join(timeout) ?? true;
    }

    private void Report(
        StressState state, TimeSpan elapsed, double dps, long dispatches, long errors,
        long burnDispatches, string? detail = null, string? phase = null, long transitions = 0)
    {
        var progress = new StressProgress(
            state, elapsed, dps, dispatches, errors, detail, phase, transitions, burnDispatches, Pattern);
        lock (_lock)
        {
            _progress = progress;
        }

        ProgressChanged?.Invoke(progress);
    }

    private unsafe void Run()
    {
        var stopwatch = Stopwatch.StartNew();
        long dispatches = 0;
        long errors = 0;

        // Load-loop dispatches only (see StressProgress.BurnDispatches). Declared
        // at method scope so the catch handlers can report it too: a terminal
        // record that under-reports the work done makes consumers describe a real
        // hardware failure as "nothing was tested".
        long burnDispatches = 0;

        try
        {
            using var targetAdapter = StressAdapter.Select(TargetVendorId, TargetPciBusId, out string adapterName);
            if (targetAdapter is null)
            {
                Report(StressState.Failed, stopwatch.Elapsed, 0, 0, 0, burnDispatches,
                    adapterName.Length > 0
                        ? $"Adapter binding failed: {adapterName}"
                        : $"No {StressAdapter.VendorName(TargetVendorId)} adapter found — refusing to run the burn on a different GPU.");
                return;
            }

            if (!AllowUnboundGuess && StressAdapter.IsUnboundGuess(adapterName))
            {
                Report(StressState.Failed, stopwatch.Elapsed, 0, 0, 0, burnDispatches,
                    $"Cannot tell which card this would burn ({adapterName}) — refusing, because the " +
                    "result would be attributed to a specific GPU.");
                return;
            }

            Diagnostics.Log.Info($"Burn adapter: {adapterName}");

            var result = D3D11.D3D11CreateDevice(
                targetAdapter, DriverType.Unknown, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0],
                out ID3D11Device? device, out ID3D11DeviceContext? context);
            if (result.Failure || device is null || context is null)
            {
                Report(StressState.Failed, stopwatch.Elapsed, 0, 0, 0, burnDispatches,
                    $"D3D11 device creation failed on {adapterName}: {result}");
                return;
            }

            using (device)
            using (context)
            {
                // Size the streamed working set against available VRAM. On a
                // UMA device DedicatedVideoMemory is a token carve-out (128 MiB
                // on an Arc B390 whose real budget is 13.4 GiB), so keying the
                // cap off it collapsed the working set to the 64 MiB floor and
                // the burn quietly stopped exercising the memory path at all.
                // The device itself says whether its memory is unified; when it
                // is, plan against the same shared budget VramTest uses.
                // Dedicated-VRAM cards keep the historical sizing exactly.
                bool unifiedMemory = device.CheckFeatureSupport<Vortice.Direct3D11.FeatureDataD3D11Options2>(
                    Vortice.Direct3D11.Feature.D3D11Options2).UnifiedMemoryArchitecture;
                ulong vramBytes = 8UL * 1024 * 1024 * 1024;
                using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
                using (var adapter = dxgiDevice.GetAdapter())
                {
                    vramBytes = (ulong)adapter.Description.DedicatedVideoMemory;
                    if (unifiedMemory)
                    {
                        using var adapter3 = adapter.QueryInterface<IDXGIAdapter3>();
                        vramBytes = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).Budget;
                    }
                }

                ulong requested = (ulong)Math.Max(64, WorkingSetMiB) * 1024 * 1024;
                ulong cap = Math.Max(64UL * 1024 * 1024, vramBytes / 6);

                // A structured buffer's ByteWidth must be an exact multiple of
                // its 16-byte stride or CreateBuffer fails with E_INVALIDARG.
                // Powers-of-two sizes hid this: before the UMA change the cap
                // always collapsed to the aligned 64 MiB floor on iGPUs, but a
                // dynamic memory budget is any number at all, so a UMA device
                // whose budget/6 is not a multiple of 16 could not start the
                // burn at all. Round down — never up, which could exceed the cap.
                uint srcBytes = (uint)Math.Min(requested, cap);
                srcBytes -= srcBytes % 16;
                uint srcCount = srcBytes / 16;

                // Load knob → gathers (memory) + FMA rounds (compute).
                uint intensity = Math.Clamp(IterationsPerDispatch, 512, 16384);
                uint fetches = Math.Clamp(intensity / 64, 8, 256);
                const uint aluRounds = 24;

                using var initShader = device.CreateComputeShader(
                    Compiler.Compile(InitShaderSource, "main", "stress-init", "cs_5_0").Span);
                using var burnShader = device.CreateComputeShader(
                    Compiler.Compile(BurnShaderSource, "main", "stress-burn", "cs_5_0").Span);

                var srcDesc = new BufferDescription(
                    srcBytes, BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                    ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16);
                using var srcBuffer = device.CreateBuffer(srcDesc);
                using var srcUav = device.CreateUnorderedAccessView(srcBuffer);
                using var srcSrv = device.CreateShaderResourceView(srcBuffer);

                var dstDesc = new BufferDescription(
                    ThreadCount * 16, BindFlags.UnorderedAccess, ResourceUsage.Default,
                    CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16);
                using var dstBuffer = device.CreateBuffer(dstDesc);
                using var dstUav = device.CreateUnorderedAccessView(dstBuffer);

                var stagingDesc = new BufferDescription(
                    CheckElements * 16, BindFlags.None, ResourceUsage.Staging,
                    CpuAccessFlags.Read, ResourceOptionFlags.BufferStructured, 16);
                using var staging = device.CreateBuffer(stagingDesc);

                // Submission is cheap; execution is not. With no back-pressure
                // the loop queued thousands of dispatches in the first second
                // and the only sync point — Verify's Map — then blocked until
                // the whole backlog drained (24 s on an Arc B390). That made
                // --seconds meaningless, pushed Stop() far past its timeout,
                // and made the dispatch counter report the submission burst
                // instead of real throughput. A small ring of event queries
                // keeps enough work in flight to stay saturated while capping
                // how far ahead submission can run.
                var fenceDesc = new QueryDescription(QueryType.Event, QueryFlags.None);
                using var fence0 = device.CreateQuery(fenceDesc);
                using var fence1 = device.CreateQuery(fenceDesc);
                using var fence2 = device.CreateQuery(fenceDesc);
                using var fence3 = device.CreateQuery(fenceDesc);
                ID3D11Query[] fences = [fence0, fence1, fence2, fence3];
                long batchesSubmitted = 0;

                // One-time deterministic source fill.
                using (var initConstants = device.CreateBuffer<uint>([srcCount, 0, 0, 0], BindFlags.ConstantBuffer))
                {
                    context.CSSetShader(initShader);
                    context.CSSetUnorderedAccessView(0, srcUav);
                    context.CSSetConstantBuffer(0, initConstants);
                    context.Dispatch(ThreadCount / ThreadsPerGroup, 1, 1);
                    context.CSSetUnorderedAccessView(0, null);
                }

                var burnParams = new uint[4];
                burnParams[0] = fetches;
                burnParams[1] = aluRounds;
                burnParams[2] = srcCount;
                burnParams[3] = BitConverter.SingleToUInt32Bits(0.5f);
                using var burnConstants = device.CreateBuffer<uint>(burnParams, BindFlags.ConstantBuffer);

                context.CSSetShader(burnShader);
                context.CSSetShaderResource(0, srcSrv);
                context.CSSetUnorderedAccessView(0, dstUav);
                context.CSSetConstantBuffer(0, burnConstants);

                const int SliceBytes = CheckElements * 16;
                const int SliceCount = ThreadCount / CheckElements;
                byte[] current = new byte[SliceBytes];
                int verifySlice = 0;

                void ReadSlice(int sliceIndex, byte[] into, int intoOffset)
                {
                    int offset = sliceIndex * SliceBytes;
                    context.CopySubresourceRegion(staging, 0, 0, 0, 0, dstBuffer, 0,
                        new Vortice.Mathematics.Box(offset, 0, 0, offset + SliceBytes, 1, 1));
                    var mapped = context.Map(staging, 0, MapMode.Read);
                    try
                    {
                        fixed (byte* dest = into)
                        {
                            System.Buffer.MemoryCopy(
                                (void*)mapped.DataPointer, dest + intoOffset, into.Length - intoOffset, SliceBytes);
                        }
                    }
                    finally
                    {
                        context.Unmap(staging, 0);
                    }
                }

                // Reference pass: capture the ENTIRE 16 MiB output once, then
                // verification walks a rotating 256 KiB window — every element
                // is re-checked bit-for-bit across each 64-check cycle, not
                // just a fixed slice at offset 0.
                context.Dispatch(ThreadCount / ThreadsPerGroup, 1, 1);
                context.Flush();
                byte[] reference = new byte[ThreadCount * 16];
                for (int slice = 0; slice < SliceCount; slice++)
                {
                    ReadSlice(slice, reference, slice * SliceBytes);
                }

                dispatches++;

                var lastReport = TimeSpan.Zero;
                var lastCheck = TimeSpan.Zero;
                long dispatchesAtLastReport = 0;

                long transitions = 0;
                bool healthy = true;

                const string MismatchUnderLoad =
                    "Computation mismatch — the GPU returned different results for identical work. " +
                    "The current clocks are unstable.";

                // Shared verification: bit-exact slice compare + device-removed check.
                // Reports and flips `healthy` on failure so pattern loops can exit.
                bool Verify(string failureDetail)
                {
                    var elapsed = stopwatch.Elapsed;
                    int slice = verifySlice++ % SliceCount;
                    ReadSlice(slice, current, 0);
                    if (!current.AsSpan().SequenceEqual(reference.AsSpan(slice * SliceBytes, SliceBytes)))
                    {
                        errors++;
                        Report(StressState.ArtifactDetected, elapsed,
                            Rate(dispatches, dispatchesAtLastReport, elapsed, lastReport), dispatches, errors,
                            burnDispatches, failureDetail, transitions: transitions);
                        healthy = false;
                        return false;
                    }

                    var reason = device.DeviceRemovedReason;
                    if (reason.Failure)
                    {
                        Report(StressState.DeviceLost, elapsed, 0, dispatches, errors, burnDispatches,
                            $"The GPU device was removed/reset (0x{reason.Code:X8}) — driver TDR. " +
                            "The current clocks are unstable.", transitions: transitions);
                        healthy = false;
                        return false;
                    }

                    return true;
                }

                void Tick(string? phase)
                {
                    // Never publish Running over a terminal state. DispatchBatch
                    // and Verify are local functions: reporting DeviceLost or
                    // ArtifactDetected there only returns to the CALL SITE, and
                    // the rest of the loop body still ran — so a Tick after a
                    // detected TDR overwrote the failure with "verified clean so
                    // far", leaving the last published progress saying Running on
                    // a dead worker.
                    if (!healthy)
                    {
                        return;
                    }

                    var elapsed = stopwatch.Elapsed;
                    if ((elapsed - lastReport).TotalSeconds >= 1)
                    {
                        Report(StressState.Running, elapsed,
                            Rate(dispatches, dispatchesAtLastReport, elapsed, lastReport), dispatches, errors,
                            phase: phase, transitions: transitions, burnDispatches: burnDispatches);
                        dispatchesAtLastReport = dispatches;
                        lastReport = elapsed;
                    }
                }

                // Bounded wait for one batch's event query. Returns false after
                // publishing a terminal state: the device reported a reset, or the
                // batch did not retire within the deadline.
                //
                // The poll MUST be able to give up. After a device reset the query
                // simply never signals — IsDataAvailable keeps returning false
                // with no error — so an unguarded wait here spins forever inside
                // the worker: no terminal state is ever published, the stepper
                // and the certifier wait on progress that has stopped advancing,
                // and the GPU is left holding the overclock that just killed it.
                // Losing the device is exactly what this test exists to catch, so
                // it is checked here and a bounded deadline backs it up.
                //
                // A stop request does NOT short-circuit this wait. It used to,
                // and the closing Verify then issued a Map — which blocks until
                // every queued batch completes — on a GPU that had stalled
                // without reporting a reset: the exact unbounded hang the
                // deadline exists to escape, reachable by any stop inside the
                // deadline window. Waiting keeps every path bounded.
                bool WaitForBatch(ID3D11Query fence)
                {
                    var deadline = stopwatch.Elapsed + FenceWaitTimeout;
                    int waits = 0;
                    while (!context.IsDataAvailable(fence))
                    {
                        var removed = device.DeviceRemovedReason;
                        if (removed.Failure)
                        {
                            Report(StressState.DeviceLost, stopwatch.Elapsed, 0, dispatches, errors, burnDispatches,
                                $"The GPU device was removed/reset (0x{removed.Code:X8}) — driver TDR. " +
                                "The current clocks are unstable.", transitions: transitions);
                            healthy = false;
                            return false;
                        }

                        if (stopwatch.Elapsed > deadline)
                        {
                            Report(StressState.Failed, stopwatch.Elapsed, 0, dispatches, errors, burnDispatches,
                                $"The GPU did not retire queued work within {FenceWaitTimeout.TotalSeconds:F0} s " +
                                "and the device did not report a reset — the run is inconclusive.",
                                transitions: transitions);
                            healthy = false;
                            return false;
                        }

                        // Spin, then yield, then sleep — never sleep first.
                        //
                        // Thread.Sleep(1) rounds up to the OS timer quantum
                        // (10–15 ms by default), so with a 4-deep ring it caps
                        // submission at a few hundred batches per second
                        // REGARDLESS of GPU speed. On a slow iGPU the GPU is the
                        // bottleneck and that never binds, but on a fast discrete
                        // card at low intensity a batch retires in well under a
                        // millisecond — the cap then duty-cycles the burn, which
                        // the boost-excursion pattern in particular is documented
                        // to be defeated by, while the run still reports a clean
                        // pass. beta.1 submitted unbounded and stayed saturated.
                        //
                        // A short spin covers the fast case with no syscall; the
                        // sleep only takes over once waiting is clearly long,
                        // keeping a slow device CPU-polite.
                        if (waits < 16)
                        {
                            Thread.SpinWait(128 << Math.Min(waits, 5));
                        }
                        else if (waits < 24)
                        {
                            Thread.Yield();
                        }
                        else
                        {
                            Thread.Sleep(1);
                        }

                        waits++;
                    }

                    return true;
                }

                void DispatchBatch(int count)
                {
                    for (int i = 0; i < count && !_stop; i++)
                    {
                        context.Dispatch(ThreadCount / ThreadsPerGroup, 1, 1);
                        dispatches++;
                        burnDispatches++;
                    }

                    context.End(fences[(int)(batchesSubmitted % fences.Length)]);
                    context.Flush();
                    batchesSubmitted++;

                    // Ring full: the oldest batch must retire before more work is
                    // queued, capping how far ahead submission can run.
                    if (batchesSubmitted >= fences.Length)
                    {
                        _ = WaitForBatch(fences[(int)(batchesSubmitted % fences.Length)]);
                    }
                }

                bool VerifyDue(string failureDetail)
                {
                    // Once a terminal state has been published, do not touch the
                    // device again: Map on a removed device throws and the outer
                    // handler would republish a raw HRESULT over the real
                    // diagnosis, and Map on a hung one blocks — reintroducing the
                    // very worker hang the fence deadline exists to escape. Tick
                    // got this guard; its twin here did not.
                    if (!healthy)
                    {
                        return false;
                    }

                    var elapsed = stopwatch.Elapsed;
                    if ((elapsed - lastCheck).TotalSeconds < 2)
                    {
                        return true;
                    }

                    lastCheck = elapsed;
                    return Verify(failureDetail);
                }

                switch (Pattern)
                {
                    case StressPattern.Transitions:
                        // Deterministic, slightly irregular cycle lengths exercise
                        // different retraining timings. Idle phases are long enough
                        // for the driver to drop P-states and memory clocks.
                        int[] loadSeconds = [10, 8, 14, 9, 12];
                        int[] idleSeconds = [14, 18, 12, 20, 16];
                        int phaseIndex = 0;
                        while (!_stop && healthy)
                        {
                            var loadEnd = stopwatch.Elapsed +
                                TimeSpan.FromSeconds(loadSeconds[phaseIndex % loadSeconds.Length]);
                            while (!_stop && healthy && stopwatch.Elapsed < loadEnd)
                            {
                                DispatchBatch(4);
                                if (!VerifyDue(MismatchUnderLoad))
                                {
                                    return;
                                }

                                Tick("load");
                            }

                            if (_stop || !healthy || !Verify(MismatchUnderLoad))
                            {
                                break;
                            }

                            var idleEnd = stopwatch.Elapsed +
                                TimeSpan.FromSeconds(idleSeconds[phaseIndex % idleSeconds.Length]);
                            while (!_stop && stopwatch.Elapsed < idleEnd)
                            {
                                Thread.Sleep(200);
                                Tick("idle");
                            }

                            if (_stop)
                            {
                                break;
                            }

                            transitions++;

                            // No new work has run since before the idle phase: a
                            // mismatch here means VRAM contents changed while the
                            // memory clock switched down and back.
                            if (!Verify(
                                "Results changed across an idle transition with no new GPU work — VRAM contents " +
                                "were corrupted while the memory clock switched. The memory offset is unstable " +
                                "at clock transitions."))
                            {
                                break;
                            }

                            phaseIndex++;
                        }

                        break;

                    case StressPattern.BoostExcursions:
                        // Steady light load never reaches max boost — the driver
                        // parks it in efficient mid bins (measured on Blackwell).
                        // What does reach the top bins is the boost OVERSHOOT: the
                        // first few hundred ms of a saturating burst run at maximum
                        // clocks before power management clamps down. Short bursts
                        // with idle gaps ride that overshoot over and over — the
                        // exact excursion behind "passed the burn, crashed on the
                        // desktop" — and sweep every clock bin in between.
                        while (!_stop && healthy)
                        {
                            var burstEnd = stopwatch.Elapsed + TimeSpan.FromMilliseconds(250);
                            while (!_stop && healthy && stopwatch.Elapsed < burstEnd)
                            {
                                DispatchBatch(2);
                            }

                            if (!VerifyDue(
                                "Computation mismatch during a boost excursion — the GPU miscalculates at " +
                                "its top boost clocks. This core offset is unsafe even if heavy loads pass."))
                            {
                                return;
                            }

                            if (!healthy)
                            {
                                break;
                            }

                            transitions++;
                            Tick("burst");
                            Thread.Sleep(1250);
                        }

                        break;

                    default:
                        while (!_stop && healthy)
                        {
                            DispatchBatch(4);
                            if (!VerifyDue(MismatchUnderLoad))
                            {
                                return;
                            }

                            Tick(null);
                        }

                        break;
                }

                if (!healthy)
                {
                    return;
                }

                // Drain before the closing check, bounded by the fence deadline.
                // Verify's Map waits for EVERY queued batch, and the ring wait
                // only ever proved the oldest one retired — on a device that has
                // stalled without a reset the Map would never return and no
                // terminal state would ever be published.
                if (batchesSubmitted > 0
                    && !WaitForBatch(fences[(int)((batchesSubmitted - 1) % fences.Length)]))
                {
                    return;
                }

                // Verification is periodic (every ~2 s), so everything dispatched
                // since the last check is still unverified when the loop ends.
                // Reporting Stopped without this final bit-exact pass let the
                // closing seconds of every run go unchecked — including the
                // moment a marginal clock finally slipped — and the run was
                // still scored as clean. A step is only good once the last work
                // it did has been compared.
                if (!Verify(MismatchUnderLoad))
                {
                    return;
                }

                Report(StressState.Stopped, stopwatch.Elapsed, 0, dispatches, errors,
                    transitions: transitions, burnDispatches: burnDispatches);
            }
        }
        catch (SharpGenException ex)
        {
            Report(
                ex.ResultCode.Code == unchecked((int)0x887A0005) // DXGI_ERROR_DEVICE_REMOVED
                    ? StressState.DeviceLost
                    : StressState.Failed,
                stopwatch.Elapsed, 0, dispatches, errors, burnDispatches, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
        {
            Report(StressState.Failed, stopwatch.Elapsed, 0, dispatches, errors, burnDispatches, ex.Message);
        }
        catch (Exception ex)
        {
            // Last resort: an escape from a background thread would terminate
            // the whole process, possibly while an overclock is applied.
            Report(StressState.Failed, stopwatch.Elapsed, 0, dispatches, errors, burnDispatches,
                $"Unexpected failure: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static double Rate(long total, long previous, TimeSpan now, TimeSpan then)
    {
        double seconds = (now - then).TotalSeconds;
        return seconds > 0.05 ? (total - previous) / seconds : 0;
    }

    public void Dispose()
    {
        StopAndWait(TimeSpan.FromSeconds(5));
    }
}
