using Afterglow.Core.Hardware;
using Afterglow.Core.Tuning;

namespace Afterglow.Cli;

/// <summary>
/// `vfcurve [--seconds N] [--load] [--json]` — records and prints the measured
/// voltage/frequency curve. With --load, drives the GPU through a range of
/// intensities so the curve fills in across voltages instead of only where the
/// desktop happens to sit.
/// </summary>
internal static class VfCurveCommand
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        int seconds = 40;
        bool drive = args.Contains("--load");
        bool probe = args.Contains("--probe");
        bool json = args.Contains("--json");
        bool fresh = args.Contains("--fresh");

        // Probe outcome, surfaced in the exit code AND in the --json payload so a
        // scripted consumer can tell a partial sweep from a complete one.
        bool probeIncomplete = false;
        bool probeRestoreFailed = false;

        // A fault from the load engine, in EITHER mode. The passive recorder
        // drives the same engine and ignored it completely, so `vfcurve --load`
        // on a machine where the burn refused to start printed idle-voltage
        // samples under the heading "under load" and exited 0.
        string? loadFailure = null;

        int probeStepsDone = 0;
        int probeStepsTotal = 0;

        if (CliArgs.Validate(args, "vfcurve") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

        if (CliArgs.TryInt(args, "--seconds", 5, 900, ref seconds) is string secondsError)
        {
            Console.Error.WriteLine(secondsError);
            return 2;
        }

        using var manager = new GpuManager();
        if (manager.Gpus.Count == 0)
        {
            Console.Error.WriteLine($"No supported GPU found (NVML: {manager.NvmlStatus}, IGCL: {manager.IgclStatus}).");
            return 1;
        }

        if (!CliGpu.TryIndexOrFirst(args, manager.Gpus[0].Index, out uint gpuIndex, out string? gpuArgError))
        {
            Console.Error.WriteLine(gpuArgError);
            return 2;
        }

        var gpu = manager.Gpus.FirstOrDefault(g => g.Index == gpuIndex);
        if (gpu is null)
        {
            Console.Error.WriteLine($"GPU {gpuIndex} not found — {manager.Gpus.Count} GPU(s) detected.");
            return 2;
        }

        // A V/F curve is voltage plotted against clock, so a driver that does not
        // report core voltage cannot produce one — no matter how long the run.
        // Establish that from real reads BEFORE committing to the work: the
        // passive mode would otherwise sample for up to fifteen minutes and the
        // probe would lock this GPU's clock through a full sweep, both to print
        // "0 voltage points from 0 samples". Verified on the Intel Arc B390,
        // whose every telemetry read returns a null core voltage.
        int reads = 0;
        if (gpu.CoreVoltageUnavailableReason is not null || !VoltageSensorAnswers(gpu, out reads))
        {
            // Name the real cause. On NVIDIA without an NVAPI pairing the driver
            // does report voltage — Afterglow just has no way to read it — and
            // blaming the driver pointed users away from the actual fix.
            string cause = gpu.CoreVoltageUnavailableReason
                ?? $"this driver reported no core voltage ({reads} reads, none carried one)";
            string why =
                $"{gpu.Name}: {cause}, so a V/F curve cannot be measured on this GPU. " +
                "Clock, power and utilisation are still available in `monitor`.";
            if (json)
            {
                // Same field set as the normal path, plus the reason. An early
                // return that emitted a SHORTER object would hand a scripted
                // consumer `undefined` for fields it reads on every other run.
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    gpu = gpu.Name,
                    samples = 0,
                    mode = probe ? "probe" : "passive",
                    voltage_sensor = false,
                    error = "this GPU's driver does not report core voltage",
                    saved = false,
                    probe_complete = probe ? false : (bool?)null,
                    probe_clock_restored = probe ? true : (bool?)null,
                    load_failure = (string?)null,
                    probe_steps_measured = probe ? 0 : (int?)null,
                    probe_steps_total = probe ? 0 : (int?)null,
                    points = Array.Empty<VfBin>(),
                }, JsonOut));
            }
            else
            {
                Console.Error.WriteLine(why);
            }

            return 3;
        }

        var recorder = new VfCurveRecorder
        {
            DeviceIndex = gpu.Index,
            GpuUuid = gpu.Uuid,
            VendorId = gpu.PciVendorId,
            PersistPath = VfCurveRecorder.PathFor(gpu.Uuid, isPrimary: gpu.Index == manager.Gpus[0].Index),
        };
        if (!fresh)
        {
            recorder.Load();
        }

        if (probe)
        {
            // Active sweep: lock the clock at each step under load and record the
            // voltage the driver selects — the definitive way to map the curve.
            if (!json)
            {
                Console.WriteLine("Probing the V/F curve: locking each clock step under load (requires administrator)…");
            }

            var vfProbe = new VfCurveProbe(gpu.Tuner, () => gpu.Poller.Poll())
            {
                TargetPciBusId = gpu.PciBusId,
                TargetVendorId = gpu.PciVendorId,
            };
            // Take the outcome the probe reports rather than sniffing the phase
            // text for "refused": that missed every other early exit, and a
            // failed clock-lock RESTORE — which can leave the GPU pinned — is
            // exactly the case the CLI most needs to report.
            string? abortDetail = null;
            vfProbe.ProgressChanged += progress =>
            {
                // Anything other than a fully completed sweep, or a failed
                // clock-lock restore, is a reason to fail. Matching only Aborted
                // let a Ctrl+C-cancelled sweep — an exit this command's own
                // handler created — print a normal-looking curve and exit 0,
                // which without --fresh is mostly reloaded persisted samples.
                // The restore case matters even for a full sweep: the GPU may
                // still be pinned.
                if (!progress.Running)
                {
                    // Counts come from EVERY terminal record, not only failures.
                    // Setting them inside the failure guard made a clean 12-step
                    // sweep serialize "0 of 0" next to probe_complete: true.
                    probeStepsDone = progress.StepIndex;
                    probeStepsTotal = progress.StepCount;

                    // Coverage and hardware state are independent signals — the
                    // engine documents them that way, and a fully measured sweep
                    // can still fail to put the clock back. Folding them into one
                    // flag printed "probe_complete: false" beside "12 of 12", so
                    // a scripted consumer discarded a perfectly good curve.
                    probeIncomplete = progress.Outcome != VfProbeOutcome.Completed;
                    probeRestoreFailed = progress.RestoreFailed;
                    loadFailure = progress.LoadFailure;

                    if (probeIncomplete || probeRestoreFailed || loadFailure is not null)
                    {
                        abortDetail =
                            $"{progress.Phase} (measured {progress.StepIndex} of {progress.StepCount} steps)";
                    }
                }

                if (!json && progress.Running && progress.MeasuredVoltageMv is double mv)
                {
                    Console.WriteLine(
                        $"  step {progress.StepIndex,2}/{progress.StepCount}: lock {progress.TargetClockMHz,5} MHz -> " +
                        $"{progress.MeasuredClockMHz,7:F0} MHz @ {mv,7:F1} mV");
                }
            };
            // Ctrl+C must NOT terminate the process here. The probe pins the core
            // clock at an EXACT frequency and undoes it only in its own finally
            // block, so the default terminate would leave the GPU pinned at
            // whatever step it had reached — at driver level, until an explicit
            // `set --lock-clock off` or a reboot, with nothing recorded to warn
            // about it. Cancelling instead lets the sweep unwind properly. Every
            // other long-running command already does this; this one did not.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                if (!json)
                {
                    Console.WriteLine();
                    Console.WriteLine("Stopping the probe and restoring the previous clock state…");
                }

                vfProbe.Cancel();
            };

            vfProbe.Run(recorder);
            if (abortDetail is not null)
            {
                // Name which of the two actually happened. "Probe did not
                // complete" over a 12-of-12 sweep whose only fault was a refused
                // restore misdescribed both the run and the hazard.
                Console.Error.WriteLine(probeIncomplete
                    ? $"Probe did not complete: {abortDetail}"
                    : probeRestoreFailed
                        ? $"Probe completed, but the clock state was not restored: {abortDetail}"
                        : $"Probe completed, but the GPU proved unstable under load: {loadFailure}");
            }
        }
        else
        {
            if (!json)
            {
                Console.WriteLine($"Recording the V/F curve for {seconds} s{(drive ? " while driving load" : string.Empty)}…");
            }

            Stress.GpuStressTestRunner? runner = null;
            try
            {
                if (drive)
                {
                    runner = new Stress.GpuStressTestRunner { TargetPciBusId = gpu.PciBusId, TargetVendorId = gpu.PciVendorId };
                    runner.Start();
                }

                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                int step = 0;
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(250);
                    recorder.Add(gpu.Poller.Poll());

                    // Sweep intensity so the GPU visits several voltage/clock states.
                    if (drive && ++step % 24 == 0)
                    {
                        runner!.NextIntensity();
                    }
                }
            }
            finally
            {
                // Dispose is what stops the final burn and runs its closing
                // bit-exact verification, so the verdict must be read AFTER it.
                // Reading first missed an ArtifactDetected or DeviceLost raised
                // by that last check — no warning printed, load_failure null,
                // exit 0. The probe path was fixed for this same window.
                runner?.Dispose();
                loadFailure = runner?.LoadFailure;
            }
        }

        bool saved = recorder.Save();
        if (!saved && !json)
        {
            Console.Error.WriteLine(
                "  ⚠ This curve was NOT saved: the curve file on disk belongs to another GPU (or could not be " +
                "written). The results above are complete but will not be reloaded next time.");
        }

        var curve = recorder.GetCurve();


        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                gpu = gpu.Name,
                samples = recorder.TotalSamples,
                mode = probe ? "probe" : "passive",

                // Always present, so a consumer can tell "no curve because this
                // GPU has no voltage sensor" from "no curve yet" without
                // string-matching an error message.
                voltage_sensor = true,
                error = (string?)null,
                saved,
                probe_complete = probe ? !probeIncomplete : (bool?)null,
                probe_clock_restored = probe ? !probeRestoreFailed : (bool?)null,
                load_failure = loadFailure,
                probe_steps_measured = probe ? probeStepsDone : (int?)null,
                probe_steps_total = probe ? probeStepsTotal : (int?)null,
                points = curve,
            }, JsonOut));
            return probeIncomplete || probeRestoreFailed || loadFailure is not null ? 1 : curve.Count > 0 ? 0 : 3;
        }

        Console.WriteLine($"{gpu.Name}: {curve.Count} voltage points from {recorder.TotalSamples:N0} samples under load");
        if (probeIncomplete)
        {
            Console.WriteLine(
                "  ⚠ The probe did not complete, so this curve is not a full measured V/F map — " +
                "it also includes any previously recorded samples (use --fresh to start clean).");
        }

        if (loadFailure is not null)
        {
            Console.WriteLine($"  ⚠ {loadFailure}");
        }

        if (probeRestoreFailed)
        {
            Console.WriteLine(
                "  ⚠ The clock state could NOT be restored — this GPU may still be pinned. " +
                "Run `afterglow-cli set --lock-clock off` to clear it.");
        }

        foreach (var bin in curve)
        {
            Console.WriteLine(
                $"  {bin.VoltageMv,7:F0} mV -> max {bin.MaxClockMHz,7:F0} MHz  (avg {bin.AvgClockMHz,7:F0}, {bin.Samples,5} samples)");
        }

        return probeIncomplete || probeRestoreFailed || loadFailure is not null ? 1 : curve.Count > 0 ? 0 : 3;
    }

    /// <summary>
    /// Takes a handful of real telemetry reads and reports whether ANY carried a
    /// core voltage. One null is a glitch — voltage and clock come from separate
    /// driver calls — so this asks several times before concluding the sensor is
    /// absent, and stops at the first answer so a working GPU pays ~nothing.
    /// </summary>
    private static bool VoltageSensorAnswers(GpuContext gpu, out int reads)
    {
        reads = 0;
        if (PollForVoltage(gpu, 8, ref reads))
        {
            return true;
        }

        // Silent at idle is not the same as absent. A card can report no core
        // voltage until it leaves its lowest power state, and refusing on the
        // idle reads alone would deny a curve to hardware that would have
        // produced one — the opposite error, and the more expensive one since it
        // cannot be discovered by waiting. So ask again under the load this
        // command was about to apply anyway, and only then conclude.
        try
        {
            using var probe = new Stress.GpuStressTestRunner
            {
                TargetPciBusId = gpu.PciBusId,
                TargetVendorId = gpu.PciVendorId,
            };
            probe.Start();

            // Read under load only once the load is actually running. The ten
            // reads used to start the instant Start() returned, and on a big
            // card they all landed inside adapter selection, device creation
            // and the working-set fill — refusing, on idle evidence, a sensor
            // that reports only above the idle P-state. Time-box the wait, not
            // the read count.
            var loadDeadline = DateTime.UtcNow.AddSeconds(15);
            while (!probe.LoadRunning && probe.LoadFailure is null && DateTime.UtcNow < loadDeadline)
            {
                Thread.Sleep(100);
            }

            if (probe.LoadRunning && PollForVoltage(gpu, 10, ref reads))
            {
                return true;
            }

            // If the load never ran, the second pass proved nothing: the GPU was
            // idle for it too. Say the sensor is present rather than refuse on
            // evidence that does not exist — the normal path then runs and
            // reports the load failure itself, which is the accurate complaint.
            if (probe.LoadFailure is { } loadFailure)
            {
                Core.Diagnostics.Log.Info(
                    $"V/F voltage check could not confirm under load ({loadFailure}); not refusing on idle reads.");
                return true;
            }

            if (!probe.LoadRunning)
            {
                Core.Diagnostics.Log.Info(
                    "V/F voltage check: the load engine had not started within 15 s; not refusing on idle reads.");
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // No load engine available — the idle evidence is all there is.
            Core.Diagnostics.Log.Info($"V/F voltage check could not drive load ({ex.Message}); using idle reads.");
            return false;
        }
    }

    /// <summary>Polls telemetry up to <paramref name="attempts"/> times, stopping
    /// at the first reading that carries a core voltage.</summary>
    private static bool PollForVoltage(GpuContext gpu, int attempts, ref int reads)
    {
        for (int i = 0; i < attempts; i++)
        {
            reads++;
            if (gpu.Poller.Poll().CoreVoltageMv is not null)
            {
                return true;
            }

            Thread.Sleep(120);
        }

        return false;
    }
}

