using System.Text.Json;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;
using WpaFusion.Common;
using WpaFusion.Plugin.Sep;
using WpaFusion.Tbrw;

var stacksMode = "lbr";
var stacksFlag = Array.IndexOf(args, "--stacks");
if (stacksFlag >= 0)
{
    if (stacksFlag + 1 >= args.Length || args[stacksFlag + 1] is not ("etw" or "lbr" or "both"))
    {
        Console.Error.WriteLine("--stacks takes etw, lbr or both.");
        return 2;
    }

    stacksMode = args[stacksFlag + 1];
    args = args.Where((_, i) => i != stacksFlag && i != stacksFlag + 1).ToArray();
}

if (args.Length is < 1 or > 2 || args[0] is "--help" or "-h")
{
    Console.WriteLine("Usage: wpafusion-attribute <run-directory> [symbol-path] [--stacks etw|lbr|both]");
    Console.WriteLine("--stacks picks the stack sources to write: etw (ETW CPU-sample join), lbr (SEP LBR call stacks, default) or both. etw needs an ETL with CPU sampling taken during the SEP run.");
    Console.WriteLine("Creates a NEW output run with copied inputs and SEP attribution. Never modifies the source.");
    Console.WriteLine("Supply a local symbol path or an explicitly approved symbol server path for function names.");
    return args.Length == 0 || args.Length > 2 ? 2 : 0;
}

try
{
    var source = Path.GetFullPath(args[0]);
    var manifest = SessionManifest.Load(Path.Combine(source, SessionManifest.FileName));
    string Input(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
        {
            throw new InvalidDataException("Manifest inputs must be filenames within the run directory.");
        }

        return Path.Combine(source, name);
    }

    var tb7 = Input(manifest.Files.Tb7);
    var etl = Input(manifest.Files.Etl);
    if (manifest.Etl is null)
    {
        throw new InvalidDataException("Attribution requires the matching ETL clock reference in session.json.");
    }

    TbrwLibrary.Initialize(SepInstall.Bin64);
    var data = SepData.Load(tb7);
    // Older captures lack the platform; derive it from the .tb7's CPUID so the processed run records it.
    manifest.Platform = SepPlatform.Resolve(manifest, data.System.CpuIdSignature);
    var edp = manifest.Platform?.EdpMetricsPath(SepInstall.Root);
    Console.WriteLine(manifest.Platform is null
        ? $"Platform: CPUID 0x{data.System.CpuIdSignature:X} is not known to this SEP; TMA will be unavailable."
        : $"Platform: {manifest.Platform}; TMA metrics: {edp ?? "none in this SEP install"}");
    var output = RunFolder.Create(RunFolder.DefaultOutputRoot(), DateTime.Now);
    Console.WriteLine($"Output: {output}");
    var settings = new TraceProcessorSettings
    {
        AllowLostEvents = false,
        SuppressFirstTimeSetupMessage = true,
    };
    using var trace = TraceProcessor.Create(etl, settings);
    var processes = trace.UseProcesses();
    var cpuSamples = stacksMode != "lbr" ? trace.UseCpuSamplingData() : null;
    var symbols = args.Length == 2 ? trace.UseSymbols() : null;
    trace.Process();
    if (symbols is not null)
    {
        await symbols.Result.LoadSymbolsAsync(
            new SymCachePath(Path.Combine(output, "symcache")), new RawSymbolPath(args[1]));
    }

    if (!processes.Result.Processes.Any(p => p.Images.Count > 0))
    {
        throw new InvalidDataException("ETL has no image metadata. Record CPU or GeneralProfile, not marker-only ETW.");
    }

    var resolver = new ImageResolver(processes.Result.Processes, symbols is not null);
    var locations = new List<SampleLocation>();
    var indices = new Dictionary<SampleLocation, int>();
    var rows = new int[data.Count];
    var resolved = 0;
    for (var i = 0; i < data.Count; i++)
    {
        var ns = data.TimeNs[i];
        var activeProcesses = resolver.ActiveProcesses(data.Pid[i], ns);
        var location = resolver.Resolve(activeProcesses, data.Ip[i], ns);
        if (location is not null)
        {
            resolved++;
        }

        location = (location ?? new SampleLocation("[Unresolved module]", "[Unresolved function]")) with
        {
            ProcessStartNs = activeProcesses.Length == 1
                ? activeProcesses[0].CreateTime?.RelativeTimestamp.Nanoseconds ?? long.MinValue
                : long.MinValue,
            ProcessName = activeProcesses.Length == 1 ? activeProcesses[0].ImageName ?? string.Empty : string.Empty,
        };
        if (!indices.TryGetValue(location, out var index))
        {
            index = locations.Count;
            locations.Add(location);
            indices.Add(location, index);
        }

        rows[i] = index;
    }

    foreach (var name in new[] { manifest.Files.Etl, manifest.Files.Tb7, manifest.Files.TypeperfCsv, manifest.Files.TypeperfCounters })
    {
        if (!string.IsNullOrEmpty(name))
        {
            File.Copy(Input(name), Path.Combine(output, name));
        }
    }

    // Logs are copied when present so the processed run keeps its diagnostics (sep.log, capture.log).
    foreach (var log in new[] { "sep.log", "capture.log" })
    {
        if (File.Exists(Path.Combine(source, log)))
        {
            File.Copy(Path.Combine(source, log), Path.Combine(output, log));
        }
    }

    // The copy keeps the original run id; ProcessedFrom links it back to the capture folder.
    manifest.ProcessedFrom = source;
    manifest.Save(Path.Combine(output, SessionManifest.FileName));
    Console.WriteLine($"Processed from: {source}");

    var map = new SampleAttribution { Tb7Sha256 = SampleAttribution.Hash(tb7), Locations = locations.ToArray(), SampleLocations = rows };
    using (var file = File.Create(Path.Combine(output, Path.GetFileName(tb7)) + SampleAttribution.Suffix))
    {
        JsonSerializer.Serialize(file, map);
    }

    if (cpuSamples is not null)
    {
        // ETW CPU samples with stacks, joined to SEP samples for the CPU Usage (SEP Events) table.
        var frames = new List<SampleLocation>();
        var frameIndex = new Dictionary<SampleLocation, int>();
        var frameCache = new Dictionary<(IImage?, ulong), int>();
        var stacks = new List<int[]>();
        var stackIndex = new Dictionary<string, int>();
        var etw = new List<EtwCpuSample>();
        var total = 0;
        foreach (var sample in cpuSamples.Result.Samples)
        {
            total++;
            if (sample.Process is null || sample.Thread is null)
            {
                continue;
            }

            // Samples without a stack (idle, or stack walk failed) still bound their interval, so keep them.
            var ids = new List<int>();
            foreach (var frame in sample.Stack?.Frames ?? Array.Empty<StackFrame>())
            {
                var key = (frame.Image, unchecked((ulong)frame.Address.Value));
                if (!frameCache.TryGetValue(key, out var fi))
                {
                    var image = frame.Image;
                    var location = image is null
                        ? new SampleLocation("[Unresolved module]", "[Unresolved function]")
                        : new SampleLocation(image.Path ?? image.FileName,
                            symbols is null ? "[Symbols not loaded]" : image.GetSymbol(frame.Address)?.FunctionName ?? "[Unresolved function]");
                    if (!frameIndex.TryGetValue(location, out fi))
                    {
                        fi = frames.Count;
                        frames.Add(location);
                        frameIndex.Add(location, fi);
                    }

                    frameCache.Add(key, fi);
                }

                ids.Add(fi);
            }

            var si = SampledStackAttribution.NoStack;
            if (ids.Count > 0)
            {
                var text = string.Join(',', ids);
                if (!stackIndex.TryGetValue(text, out si))
                {
                    si = stacks.Count;
                    stacks.Add(ids.ToArray());
                    stackIndex.Add(text, si);
                }
            }

            etw.Add(new EtwCpuSample(sample.Timestamp.RelativeTimestamp.Nanoseconds, sample.Processor,
                unchecked((uint)sample.Process.Id), unchecked((uint)sample.Thread.Id), si,
                sample.Weight.Nanoseconds));
        }

        if (etw.Count == 0)
        {
            Console.WriteLine("No ETW CPU samples in the ETL (record CPU sampling); skipped the sampled-stack sidecar.");
        }
        else
        {
            Console.WriteLine($"ETW samples span {etw.Min(e => e.TimeNs) / 1e9:F3}-{etw.Max(e => e.TimeNs) / 1e9:F3} s on {etw.Select(e => e.Cpu).Distinct().Count()} CPUs; SEP samples span {data.FirstNs / 1e9:F3}-{data.LastNs / 1e9:F3} s on {data.Cpu.Distinct().Count()} CPUs.");
            var weights = etw.Select(e => e.WeightNs).OrderBy(w => w).ToArray();
            Console.WriteLine($"ETW sample weight (interval) min/median/max: {weights[0] / 1000}/{weights[weights.Length / 2] / 1000}/{weights[^1] / 1000} us.");
            var inWindow = etw.Count(e => e.TimeNs >= data.FirstNs && e.TimeNs <= data.LastNs);
            if (inWindow == 0)
            {
                Console.WriteLine("WARNING: the ETL has no CPU samples during the SEP window (SEP collection suppresses ETW sampled profile on some machines); skipped the ETW-join sidecar. Use --stacks lbr with an LBR capture.");
            }
            else
            {
                var sep = new SepSampleSet(data.TimeNs, data.Cpu, data.Tid, data.Pid, data.EventIndex, data.Events.Count);
                var joined = SampledStackJoin.Join(etw, sep, frames.ToArray(), stacks.ToArray(), SampleAttribution.Hash(tb7),
                    new FileInfo(etl).Length, manifest.Etl.ZeroFileTimeUtc, out var stats);
                using (var file = File.Create(Path.Combine(output, Path.GetFileName(tb7)) + SampledStackAttribution.Suffix))
                {
                    JsonSerializer.Serialize(file, joined);
                }

                Console.WriteLine($"Sampled stacks: {etw.Count}/{total} ETW samples, {etw.Count(e => e.StackId != SampledStackAttribution.NoStack)} with stacks, {stacks.Count} distinct stacks; {stats.Matched}/{data.Count} SEP samples matched, {stats.Unmatched} unmatched (synthetic rows): {stats.NoCoveringSample} no covering ETW sample, {stats.OutOfInterval} outside the ETW sample interval, {stats.ThreadMismatch} thread mismatch; median gap to covering sample {stats.MedianGapNs / 1000} us.");
            }
        }
    }

    // SEP's own LBR call stacks (sep -lbr call_stack:usr): no ETW samples needed, only image lifetimes.
    if (stacksMode != "etw")
    {
        // TBRW holds an exclusive handle on the .tb7, so close it before hashing the file for the sidecar.
        var builder = new LbrStackBuilder();
        var sampleStacks = new int[data.Count];
        var hasLbr = false;
        long delivered = 0;
        using (var tbFile = TbrwFile.Open(tb7))
        {
            hasLbr = tbFile.HasLbr;
            if (hasLbr)
            {
                delivered = tbFile.ReadLbrSamples((long index, in PmuSample sample, ReadOnlySpan<ulong> from, ReadOnlySpan<ulong> to) =>
                    sampleStacks[index] = builder.Add(data.Pid[index], data.TimeNs[index], sample.Ip, from));
            }
        }

        if (!hasLbr)
        {
            Console.WriteLine("No LBR data in the .tb7 (collect with sep -lbr call_stack:usr); skipped the LBR stack sidecar.");
        }
        else
        {
            if (delivered != data.Count)
            {
                throw new InvalidDataException($"LBR pass delivered {delivered} samples, expected {data.Count}.");
            }

            Console.WriteLine($"LBR pass read {delivered} samples, {builder.Stacks.Count} distinct stacks; resolving frames...");
            var lbrFrames = new List<SampleLocation>();
            var lbrFrameIndex = new Dictionary<SampleLocation, int>();
            var lbrStacks = new int[builder.Stacks.Count][];
            var unresolvedFrames = 0;
            var frameCount = 0;
            for (var i = 0; i < lbrStacks.Length; i++)
            {
                var stack = builder.Stacks[i];
                var active = resolver.ActiveProcesses(stack.Pid, stack.TimeNs);
                lbrStacks[i] = stack.Addresses.Select(address =>
                {
                    var location = resolver.Resolve(active, address, stack.TimeNs);
                    frameCount++;
                    if (location is null)
                    {
                        unresolvedFrames++;
                        location = new SampleLocation("[Unresolved module]", "[Unresolved function]");
                    }

                    if (!lbrFrameIndex.TryGetValue(location, out var frame))
                    {
                        frame = lbrFrames.Count;
                        lbrFrames.Add(location);
                        lbrFrameIndex.Add(location, frame);
                    }

                    return frame;
                }).ToArray();
            }

            var lbrMap = new LbrStackAttribution
            {
                Tb7Sha256 = SampleAttribution.Hash(tb7),
                Frames = lbrFrames.ToArray(),
                Stacks = lbrStacks,
                StackId = sampleStacks,
            };
            using (var file = File.Create(Path.Combine(output, Path.GetFileName(tb7)) + LbrStackAttribution.Suffix))
            {
                JsonSerializer.Serialize(file, lbrMap);
            }

            var deep = sampleStacks.Count(id => lbrStacks[id].Length > 1);
            Console.WriteLine($"LBR stacks: {data.Count} samples, {lbrStacks.Length} distinct stacks, {deep} with callers; {frameCount - unresolvedFrames}/{frameCount} stack frames resolved to modules, max depth {lbrStacks.Max(s => s.Length)}.");
        }
    }
    Console.WriteLine($"Attributed {resolved}/{data.Count} samples to modules; {locations.Count} distinct module/function locations.");
    Console.WriteLine($"Open {Path.Combine(output, Path.GetFileName(tb7))} in WPA with the SEP plugin.");
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Attribution failed: {ex}");
    return 1;
}
