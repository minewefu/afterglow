using System.Runtime.InteropServices;
using Afterglow.Core.Stress;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Afterglow.Core.Tests;

/// <summary>
/// The burn workload itself: the production init and burn shaders, compiled and
/// dispatched on the D3D WARP software device with the production constants.
/// WARP runs the shader on the CPU, so nothing here touches the physical GPU —
/// and it is the only device these tests may use.
/// <para>
/// The engine's error detection is "every dispatch must reproduce the first one
/// bit for bit". That proves something only if the reference is a function of
/// the work: R-01 found every output overflowing to infinity, so a corrupted
/// input (or a miscalculated FMA) produced the same infinities and passed.
/// These tests pin the property the comparison depends on: every output is
/// finite, and a change to a fetched element, or to the arithmetic, reaches the
/// outputs of exactly the threads that consumed it.
/// </para>
/// </summary>
public sealed class BurnWorkloadTests
{
    /// <summary>
    /// ~16 MiB of source. Deliberately not a power of two, like the budget-derived
    /// working sets on UMA devices, so the shader's <c>idx % srcCount</c> is the
    /// real thing and not a mask.
    /// </summary>
    private const uint SourceElements = 1_000_003;

    private const int ElementBytes = 16;

    /// <summary>The engine's default <see cref="GpuStressTest.IterationsPerDispatch"/>.</summary>
    private const uint DefaultIntensity = 4096;

    /// <summary>Thread groups for the corruption tests: 16 Ki threads keeps WARP quick.</summary>
    private const int SmallDispatchGroups = 64;

    private const int FullDispatchGroups = GpuStressTest.ThreadCount / GpuStressTest.ThreadsPerGroup;

    [Theory]
    [InlineData(512u)]      // lightest: 8 gathers per thread
    [InlineData(4096u)]     // the default
    [InlineData(16384u)]    // heaviest: 256 gathers per thread
    public void Every_reference_output_is_finite_and_repeats_bit_for_bit(uint intensity)
    {
        using var rig = WarpBurnRig.CreateOrSkip();
        uint fetches = GpuStressTest.FetchesFor(intensity);

        // The full production dispatch at the default intensity, so every thread id
        // a real run uses is covered; the extremes use a slice to keep WARP quick.
        int groups = intensity == DefaultIntensity ? FullDispatchGroups : SmallDispatchGroups;

        byte[] reference = rig.Burn(fetches, groups);

        var nonFinite = NonFiniteComponents(reference);
        Assert.True(nonFinite.Count == 0,
            $"{nonFinite.Count} of {reference.Length / 4} output components are not finite " +
            $"(first at component {(nonFinite.Count > 0 ? nonFinite[0] : -1)}).");

        // ...and the engine's own guard agrees, so a real run would proceed.
        Assert.Null(GpuStressTest.ReferenceSelfCheck(reference));

        // The comparison the engine makes on every check: an identical dispatch on a
        // healthy device must reproduce the reference exactly.
        byte[] again = rig.Burn(fetches, groups);
        Assert.True(reference.AsSpan().SequenceEqual(again), "A repeated dispatch did not reproduce the reference.");
    }

    [Fact]
    public void Changing_one_source_element_changes_the_outputs_of_every_thread_that_fetched_it_and_no_other()
    {
        using var rig = WarpBurnRig.CreateOrSkip();
        uint fetches = GpuStressTest.FetchesFor(DefaultIntensity);
        int threads = SmallDispatchGroups * GpuStressTest.ThreadsPerGroup;

        // Thread 100's LAST gather: the hardest case for the floating-point chains,
        // which then have only one gather's worth of rounds to carry the change.
        uint element = FetchSequence(100, fetches)[^1];
        var fetchers = ThreadsFetching(element, threads, fetches);

        byte[] clean = rig.Burn(fetches, SmallDispatchGroups);

        // A wrong but plausible value — the kind of thing a bad read returns: x moved
        // by half its range, still a multiple of 2^-16 in [0, 1).
        byte[] corrupted = rig.SourceElement(element);
        float x = BitConverter.ToSingle(corrupted, 0);
        BitConverter.TryWriteBytes(corrupted.AsSpan(0, 4), ((x * 65536f + 32768f) % 65536f) / 65536f);
        Assert.NotEqual(x, BitConverter.ToSingle(corrupted, 0));
        rig.WriteSourceElement(element, corrupted);

        byte[] dirty = rig.Burn(fetches, SmallDispatchGroups);

        var floatPathBlind = new List<int>();
        var hashPathBlind = new List<int>();
        var bystandersChanged = new List<int>();
        for (int t = 0; t < threads; t++)
        {
            var before = clean.AsSpan(t * ElementBytes, ElementBytes);
            var after = dirty.AsSpan(t * ElementBytes, ElementBytes);
            if (fetchers.Contains(t))
            {
                // Both halves of the output must see it: x/y are the FP32 chains,
                // z/w the integer chain that every fetched bit enters.
                if (before[..8].SequenceEqual(after[..8]))
                {
                    floatPathBlind.Add(t);
                }

                if (before[8..].SequenceEqual(after[8..]))
                {
                    hashPathBlind.Add(t);
                }
            }
            else if (!before.SequenceEqual(after))
            {
                bystandersChanged.Add(t);
            }
        }

        Assert.Contains(100, fetchers);
        Assert.True(floatPathBlind.Count == 0,
            $"{floatPathBlind.Count} of {fetchers.Count} fetching threads' FP32 outputs ignored the corrupted element.");
        Assert.True(hashPathBlind.Count == 0,
            $"{hashPathBlind.Count} of {fetchers.Count} fetching threads' integer outputs ignored the corrupted element.");
        Assert.True(bystandersChanged.Count == 0,
            $"{bystandersChanged.Count} threads that never fetched the element changed anyway.");
    }

    [Fact]
    public void A_single_bit_flip_in_a_source_element_reaches_every_thread_that_fetched_it()
    {
        using var rig = WarpBurnRig.CreateOrSkip();
        uint fetches = GpuStressTest.FetchesFor(DefaultIntensity);
        int threads = SmallDispatchGroups * GpuStressTest.ThreadsPerGroup;

        uint element = FetchSequence(100, fetches)[^1];
        var fetchers = ThreadsFetching(element, threads, fetches);

        byte[] clean = rig.Burn(fetches, SmallDispatchGroups);

        // The lowest mantissa bit of y: zero in every element the init shader writes
        // (they are multiples of 2^-16), so this is a pure memory-style bit flip worth
        // ~1e-8 — far below what the FP32 path can be relied on to notice.
        byte[] corrupted = rig.SourceElement(element);
        corrupted[4] ^= 1;
        rig.WriteSourceElement(element, corrupted);

        byte[] dirty = rig.Burn(fetches, SmallDispatchGroups);

        var blind = new List<int>();
        var bystandersChanged = new List<int>();
        for (int t = 0; t < threads; t++)
        {
            bool same = clean.AsSpan(t * ElementBytes, ElementBytes)
                .SequenceEqual(dirty.AsSpan(t * ElementBytes, ElementBytes));
            if (fetchers.Contains(t) && same)
            {
                blind.Add(t);
            }
            else if (!fetchers.Contains(t) && !same)
            {
                bystandersChanged.Add(t);
            }
        }

        Assert.True(blind.Count == 0,
            $"{blind.Count} of {fetchers.Count} fetching threads did not see a one-bit flip in their input.");
        Assert.True(bystandersChanged.Count == 0,
            $"{bystandersChanged.Count} threads that never fetched the element changed anyway.");
    }

    [Theory]
    [InlineData(0)]     // mantissa LSB: the error FP32 rounding most often swallows
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(22)]    // mantissa MSB
    [InlineData(30)]    // exponent
    [InlineData(31)]    // sign
    public void A_one_bit_error_in_a_rounds_result_reaches_every_threads_output(int bit)
    {
        // Fault injection: a copy of the production shader that flips one bit of one
        // FMA result in every thread, at one round of one gather — a miscalculation.
        // The baseline is the same copy with the injection never firing, so both runs
        // execute the same compiled code. Chaos alone loses about half of the LSB
        // flips to rounding mid-run; the integer chain absorbing every round's
        // results must catch all of them, early, mid-run and in the very last round.
        using var rig = WarpBurnRig.CreateOrSkip(WithFaultInjection(GpuStressTest.BurnShaderSource));
        uint fetches = GpuStressTest.FetchesFor(DefaultIntensity);
        uint lastRound = GpuStressTest.AluRounds - 1;
        int threads = SmallDispatchGroups * GpuStressTest.ThreadsPerGroup;

        byte[] baseline = rig.Burn(fetches, SmallDispatchGroups, injection: [uint.MaxValue, 0, 0, 0]);

        foreach (var (fetch, round) in new[] { (0u, 0u), (fetches / 2, lastRound / 2), (fetches - 1, lastRound) })
        {
            byte[] faulty = rig.Burn(fetches, SmallDispatchGroups, injection: [fetch, round, (uint)bit, 0]);

            int unchanged = 0;
            for (int t = 0; t < threads; t++)
            {
                if (baseline.AsSpan(t * ElementBytes, ElementBytes).SequenceEqual(faulty.AsSpan(t * ElementBytes, ElementBytes)))
                {
                    unchanged++;
                }
            }

            Assert.True(unchanged == 0,
                $"Bit {bit} flipped at gather {fetch}, round {round}: {unchanged} of {threads} threads' outputs did not change.");
        }
    }

    /// <summary>
    /// The production burn shader with a fault hook: after the round's FMAs, flip
    /// bit <c>injectBit</c> of <c>a1.y</c> at gather <c>injectFetch</c>, round
    /// <c>injectRound</c> (constant buffer b1). Fails, never skips, if the shader no
    /// longer has the shape the hook is written against.
    /// </summary>
    private static string WithFaultInjection(string source)
    {
        const string ParamsAnchor = "cbuffer Params : register(b0)";
        const string RoundAnchor = "a3 = mad(-t3, a3, t3);";
        Assert.Contains(ParamsAnchor, source, StringComparison.Ordinal);
        Assert.Contains(RoundAnchor, source, StringComparison.Ordinal);

        return source
            .Replace(ParamsAnchor,
                "cbuffer Inject : register(b1) { uint injectFetch; uint injectRound; uint injectBit; uint injectPad; };\n" +
                ParamsAnchor, StringComparison.Ordinal)
            .Replace(RoundAnchor,
                RoundAnchor + "\nif (j == injectFetch && k == injectRound) { a1.y = asfloat(asuint(a1.y) ^ (1u << injectBit)); }",
                StringComparison.Ordinal);
    }

    [Fact]
    public void A_finite_reference_passes_the_self_check()
    {
        float[] reference = new float[4 * 8];
        for (int i = 0; i < reference.Length; i++)
        {
            reference[i] = (i * 0.37f) - 3f;
        }

        Assert.Null(GpuStressTest.ReferenceSelfCheck(MemoryMarshal.AsBytes(reference.AsSpan())));
    }

    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void A_reference_holding_a_non_finite_value_is_refused_and_says_where(float poison)
    {
        // R-01's reference was infinity everywhere; one bad value is already enough
        // to blind the comparison for that thread.
        float[] reference = new float[4 * 8];
        reference[13] = poison;     // thread 3, component y

        string? refusal = GpuStressTest.ReferenceSelfCheck(MemoryMarshal.AsBytes(reference.AsSpan()));

        Assert.NotNull(refusal);
        Assert.Contains("1 of 32 reference values are not finite", refusal, StringComparison.Ordinal);
        Assert.Contains("thread 3, component y", refusal, StringComparison.Ordinal);
        Assert.Contains("earns no verdict", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_all_infinite_reference_like_r01s_is_refused()
    {
        float[] reference = new float[4 * 256];
        Array.Fill(reference, float.PositiveInfinity);

        string? refusal = GpuStressTest.ReferenceSelfCheck(MemoryMarshal.AsBytes(reference.AsSpan()));

        Assert.NotNull(refusal);
        Assert.Contains("1,024 of 1,024 reference values are not finite", refusal, StringComparison.Ordinal);
        Assert.Contains("thread 0, component x", refusal, StringComparison.Ordinal);
    }

    /// <summary>The burn shader's gather addresses for one thread (mirrors its LCG).</summary>
    private static uint[] FetchSequence(uint thread, uint fetches)
    {
        var sequence = new uint[fetches];
        uint idx = thread;
        for (int j = 0; j < fetches; j++)
        {
            idx = unchecked((idx * 1664525u) + 1013904223u);
            sequence[j] = idx % SourceElements;
        }

        return sequence;
    }

    private static HashSet<int> ThreadsFetching(uint element, int threads, uint fetches)
    {
        var fetchers = new HashSet<int>();
        for (int t = 0; t < threads; t++)
        {
            if (Array.IndexOf(FetchSequence((uint)t, fetches), element) >= 0)
            {
                fetchers.Add(t);
            }
        }

        return fetchers;
    }

    private static List<int> NonFiniteComponents(byte[] output)
    {
        var bad = new List<int>();
        var floats = MemoryMarshal.Cast<byte, float>(output);
        for (int i = 0; i < floats.Length; i++)
        {
            if (!float.IsFinite(floats[i]))
            {
                bad.Add(i);
            }
        }

        return bad;
    }

    /// <summary>
    /// A WARP device holding the production source buffer, filled by the production
    /// init shader exactly as a real run fills it, and the compiled burn shader.
    /// </summary>
    private sealed class WarpBurnRig : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11ComputeShader _burn;
        private readonly ID3D11Buffer _source;
        private readonly ID3D11ShaderResourceView _sourceView;
        private readonly byte[] _sourceBytes;

        private WarpBurnRig(ID3D11Device device, ID3D11DeviceContext context, string burnSource)
        {
            _device = device;
            _context = context;
            _burn = device.CreateComputeShader(Compiler.Compile(burnSource, "main", "stress-burn", "cs_5_0").Span);

            _source = device.CreateBuffer(new BufferDescription(
                SourceElements * ElementBytes, BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, ElementBytes));
            _sourceView = device.CreateShaderResourceView(_source);

            using var init = device.CreateComputeShader(
                Compiler.Compile(GpuStressTest.InitShaderSource, "main", "stress-init", "cs_5_0").Span);
            using var sourceUav = device.CreateUnorderedAccessView(_source);
            using var initConstants = device.CreateBuffer<uint>([SourceElements, 0, 0, 0], BindFlags.ConstantBuffer);
            context.CSSetShader(init);
            context.CSSetUnorderedAccessView(0, sourceUav);
            context.CSSetConstantBuffer(0, initConstants);
            context.Dispatch(FullDispatchGroups, 1, 1);
            context.CSSetUnorderedAccessView(0, null);

            _sourceBytes = ReadBack(_source, SourceElements * ElementBytes);
        }

        /// <summary>
        /// The rig, or a SKIPPED test when this machine has no WARP device. A skip is
        /// reported as one; it is never a silent pass.
        /// </summary>
        public static WarpBurnRig CreateOrSkip(string burnSource = GpuStressTest.BurnShaderSource)
        {
            var result = D3D11.D3D11CreateDevice(
                null, DriverType.Warp, DeviceCreationFlags.None, [FeatureLevel.Level_11_0],
                out ID3D11Device? device, out ID3D11DeviceContext? context);
            if (result.Success && device is not null && context is not null)
            {
                return new WarpBurnRig(device, context, burnSource);
            }

            context?.Dispose();
            device?.Dispose();
            Assert.Skip($"The D3D WARP software device is unavailable here ({result}); the burn shader was not run.");
            throw new InvalidOperationException("unreachable: Assert.Skip throws");
        }

        public byte[] SourceElement(uint element) =>
            _sourceBytes.AsSpan((int)(element * ElementBytes), ElementBytes).ToArray();

        public void WriteSourceElement(uint element, byte[] value)
        {
            int offset = (int)(element * ElementBytes);
            _context.UpdateSubresource(value, _source, 0, 0, 0,
                new Vortice.Mathematics.Box(offset, 0, 0, offset + ElementBytes, 1, 1));
        }

        /// <summary>
        /// One burn dispatch of <paramref name="groups"/> thread groups with the
        /// production constants; returns its output. <paramref name="injection"/>
        /// fills constant buffer b1 for a <see cref="WithFaultInjection"/> shader.
        /// </summary>
        public byte[] Burn(uint fetches, int groups, uint[]? injection = null)
        {
            uint bytes = (uint)(groups * GpuStressTest.ThreadsPerGroup * ElementBytes);
            using var output = _device.CreateBuffer(new BufferDescription(
                bytes, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None,
                ResourceOptionFlags.BufferStructured, ElementBytes));
            using var outputUav = _device.CreateUnorderedAccessView(output);
            using var constants = _device.CreateBuffer<uint>(
                GpuStressTest.BurnParameters(fetches, SourceElements), BindFlags.ConstantBuffer);
            using var injectionConstants = _device.CreateBuffer<uint>(
                injection ?? [uint.MaxValue, 0, 0, 0], BindFlags.ConstantBuffer);

            _context.CSSetShader(_burn);
            _context.CSSetShaderResource(0, _sourceView);
            _context.CSSetUnorderedAccessView(0, outputUav);
            _context.CSSetConstantBuffer(0, constants);
            _context.CSSetConstantBuffer(1, injectionConstants);
            _context.Dispatch((uint)groups, 1, 1);
            _context.CSSetUnorderedAccessView(0, null);

            return ReadBack(output, bytes);
        }

        private byte[] ReadBack(ID3D11Buffer buffer, uint bytes)
        {
            using var staging = _device.CreateBuffer(new BufferDescription(
                bytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read,
                ResourceOptionFlags.BufferStructured, ElementBytes));
            _context.CopyResource(staging, buffer);
            var mapped = _context.Map(staging, 0, MapMode.Read);
            try
            {
                var data = new byte[bytes];
                Marshal.Copy(mapped.DataPointer, data, 0, data.Length);
                return data;
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }

        public void Dispose()
        {
            _sourceView.Dispose();
            _source.Dispose();
            _burn.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
