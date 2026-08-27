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
    long Transitions = 0);

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
public sealed class GpuStressTest : IDisposable
{
    private const int ThreadCount = 1 << 20;          // 1M threads → 16 MiB output
    private const int CheckElements = 16384;          // 256 KiB verify slice
    private const int ThreadsPerGroup = 256;

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
        if (IsRunning)
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

    /// <summary>Blocks until the burn thread has finished (used by the stepper).</summary>
    public void StopAndWait(TimeSpan timeout)
    {
        _stop = true;
        _thread?.Join(timeout);
    }

    private void Report(
        StressState state, TimeSpan elapsed, double dps, long dispatches, long errors,
        string? detail = null, string? phase = null, long transitions = 0)
    {
        var progress = new StressProgress(state, elapsed, dps, dispatches, errors, detail, phase, transitions);
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

        try
        {
            var result = D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0],
                out ID3D11Device? device, out ID3D11DeviceContext? context);
            if (result.Failure || device is null || context is null)
            {
                Report(StressState.Failed, stopwatch.Elapsed, 0, 0, 0, $"D3D11 device creation failed: {result}");
                return;
            }

            using (device)
            using (context)
            {
                // Size the streamed working set against available VRAM.
                ulong vramBytes = 8UL * 1024 * 1024 * 1024;
                using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
                using (var adapter = dxgiDevice.GetAdapter())
                {
                    vramBytes = (ulong)adapter.Description.DedicatedVideoMemory;
                }

                ulong requested = (ulong)Math.Max(64, WorkingSetMiB) * 1024 * 1024;
                ulong cap = Math.Max(64UL * 1024 * 1024, vramBytes / 6);
                uint srcBytes = (uint)Math.Min(requested, cap);
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

                byte[] current = new byte[CheckElements * 16];

                byte[] ReadSlice()
                {
                    context.CopySubresourceRegion(staging, 0, 0, 0, 0, dstBuffer, 0,
                        new Vortice.Mathematics.Box(0, 0, 0, CheckElements * 16, 1, 1));
                    var mapped = context.Map(staging, 0, MapMode.Read);
                    try
                    {
                        fixed (byte* dest = current)
                        {
                            System.Buffer.MemoryCopy((void*)mapped.DataPointer, dest, current.Length, current.Length);
                        }
                    }
                    finally
                    {
                        context.Unmap(staging, 0);
                    }

                    return current;
                }

                // Reference pass.
                context.Dispatch(ThreadCount / ThreadsPerGroup, 1, 1);
                context.Flush();
                byte[] reference = (byte[])ReadSlice().Clone();
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
                    var slice = ReadSlice();
                    if (!slice.AsSpan().SequenceEqual(reference))
                    {
                        errors++;
                        Report(StressState.ArtifactDetected, elapsed,
                            Rate(dispatches, dispatchesAtLastReport, elapsed, lastReport), dispatches, errors,
                            failureDetail, transitions: transitions);
                        healthy = false;
                        return false;
                    }

                    var reason = device.DeviceRemovedReason;
                    if (reason.Failure)
                    {
                        Report(StressState.DeviceLost, elapsed, 0, dispatches, errors,
                            $"The GPU device was removed/reset (0x{reason.Code:X8}) — driver TDR. " +
                            "The current clocks are unstable.", transitions: transitions);
                        healthy = false;
                        return false;
                    }

                    return true;
                }

                void Tick(string? phase)
                {
                    var elapsed = stopwatch.Elapsed;
                    if ((elapsed - lastReport).TotalSeconds >= 1)
                    {
                        Report(StressState.Running, elapsed,
                            Rate(dispatches, dispatchesAtLastReport, elapsed, lastReport), dispatches, errors,
                            phase: phase, transitions: transitions);
                        dispatchesAtLastReport = dispatches;
                        lastReport = elapsed;
                    }
                }

                void DispatchBatch(int count)
                {
                    for (int i = 0; i < count && !_stop; i++)
                    {
                        context.Dispatch(ThreadCount / ThreadsPerGroup, 1, 1);
                        dispatches++;
                    }

                    context.Flush();
                }

                bool VerifyDue(string failureDetail)
                {
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
                            while (!_stop && stopwatch.Elapsed < burstEnd)
                            {
                                DispatchBatch(2);
                            }

                            if (!VerifyDue(
                                "Computation mismatch during a boost excursion — the GPU miscalculates at " +
                                "its top boost clocks. This core offset is unsafe even if heavy loads pass."))
                            {
                                return;
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

                Report(StressState.Stopped, stopwatch.Elapsed, 0, dispatches, errors, transitions: transitions);
            }
        }
        catch (SharpGenException ex)
        {
            Report(
                ex.ResultCode.Code == unchecked((int)0x887A0005) // DXGI_ERROR_DEVICE_REMOVED
                    ? StressState.DeviceLost
                    : StressState.Failed,
                stopwatch.Elapsed, 0, dispatches, errors, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
        {
            Report(StressState.Failed, stopwatch.Elapsed, 0, dispatches, errors, ex.Message);
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
