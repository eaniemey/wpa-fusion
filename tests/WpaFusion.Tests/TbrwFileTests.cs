using System.Runtime.CompilerServices;
using WpaFusion.Common;
using WpaFusion.Tbrw;
using WpaFusion.Tbrw.Native;
using Xunit;

namespace WpaFusion.Tests;

/// <summary>Verifies the TBRW wrapper against the smoke fixture and SEP's own sfdump output.</summary>
[Collection(Fixtures.Tb7Collection)]
public class TbrwFileTests
{
    static TbrwFileTests() => TbrwLibrary.Initialize(SepInstall.Bin64);

    [Fact]
    public unsafe void Struct_sizes_match_tbrw_header()
    {
        // TBRW_VERIFY_SIZEOF values from SEP 5.52 tbrw_types.h.
        Assert.Equal(8, sizeof(TbrwStringOrId));
        Assert.Equal(24, sizeof(TbrwCpuIdIa32));
        Assert.Equal(16, sizeof(TbrwCpuArch));
        Assert.Equal(64, sizeof(TbrwCpu));
        Assert.Equal(40, sizeof(TbrwNode));
        Assert.Equal(48, sizeof(TbrwSystem));
        Assert.Equal(48, sizeof(TbrwPid));
        Assert.Equal(40, sizeof(TbrwTid));
        Assert.Equal(48, sizeof(TbrwEvent));
        Assert.Equal(72, sizeof(TbrwSampRecDescEntry));
        Assert.Equal(32, sizeof(TbrwSampleRecordPc));
    }

    [Fact]
    public void Reads_metadata_of_smoke_fixture()
    {
        using var file = TbrwFile.Open(Fixtures.SmokeTb7);

        Assert.Equal(new[] { "INST_RETIRED.ANY", "CPU_CLK_UNHALTED.THREAD", "CPU_CLK_UNHALTED.REF_TSC" }.OrderBy(n => n), file.Events.Select(e => e.Name).OrderBy(n => n));
        Assert.All(file.Events, e => Assert.Equal(2000003UL, e.SampleAfterValue));
        Assert.Equal(6, file.SystemInfo.CpuFamily);
        Assert.Equal(0x55, file.SystemInfo.CpuModel); // Cascade Lake / Skylake server
        Assert.NotEmpty(file.SystemInfo.Cpus);
        Assert.Contains(file.Processes, p => p.Name == "esrv_svc.exe" && p.Pid == 1496);
    }

    [Fact]
    public void Sample_counts_match_sfdump_per_event()
    {
        using var file = TbrwFile.Open(Fixtures.SmokeTb7);
        var perEvent = new long[file.Events.Count];
        var total = file.ReadSamples((in PmuSample s) => perEvent[s.EventIndex]++);

        // sfdump -samples on the same file: 24,446 samples.
        Assert.Equal(24446, total);
        var byName = file.Events.ToDictionary(e => e.Name, e => perEvent[e.Index]);
        Assert.Equal(6098, byName["INST_RETIRED.ANY"]);
        Assert.Equal(9563, byName["CPU_CLK_UNHALTED.THREAD"]);
        Assert.Equal(8785, byName["CPU_CLK_UNHALTED.REF_TSC"]);
    }

    [Fact]
    public void Samples_match_sfdump_rows_field_by_field()
    {
        var expected = SfdumpRows(Fixtures.SmokeSamplesTxt).ToList();
        using var file = TbrwFile.Open(Fixtures.SmokeTb7);
        var events = file.Events;
        var actual = new List<(ulong Tsc, ulong Ip, uint Pid, uint Tid, int Cpu, string Event)>();
        file.ReadSamples((in PmuSample s) => actual.Add((s.Tsc, s.Ip, s.Pid, s.Tid, s.Cpu, events[s.EventIndex].Name)));

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void Diagnostic_real_capture_event_indexes_are_in_range()
    {
        var path = Environment.GetEnvironmentVariable("WPA_FUSION_DIAGNOSTIC_TB7");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        using var file = TbrwFile.Open(path);
        Assert.Equal(file.Events.Count, file.Events.Select(e => e.EventId).Distinct().Count());
        var knownEventIds = file.Events.Select(e => e.EventId).ToHashSet();
        var unknownEventIds = new List<(long Sample, int EventIndex)>();
        long sampleIndex = 0;
        file.ReadSamples((in PmuSample sample) =>
        {
            if (sample.EventIndex >= file.Events.Count && !knownEventIds.Contains(sample.EventIndex))
            {
                unknownEventIds.Add((sampleIndex, sample.EventIndex));
            }

            sampleIndex++;
        });

        Assert.True(unknownEventIds.Count == 0,
            $"{unknownEventIds.Count} of {sampleIndex} samples have event ids missing from the event catalog; first: {unknownEventIds.FirstOrDefault()} (event count {file.Events.Count}).");
    }

    [Fact]
    public void Unbound_file_is_rejected_without_modification()
    {
        // The uarch fixture is created unbound by SEP; copy it so the check cannot touch the original.
        var source = Fixtures.UnboundTb7;
        if (source is null)
        {
            return; // Unbound fixture not present on this machine.
        }

        var copy = Path.Combine(Path.GetTempPath(), $"wpa-fusion-unbound-{Guid.NewGuid():N}.tb7");
        File.Copy(source, copy);
        try
        {
            var before = File.ReadAllBytes(copy);
            Assert.False(TbrwFile.IsBound(copy));
            Assert.Throws<TbrwUnboundFileException>(() => TbrwFile.Open(copy).Dispose());
            Assert.Equal(before, File.ReadAllBytes(copy));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>Parses sfdump -samples output into (tsc, ip, pid, tid, cpu, event) rows.</summary>
    private static IEnumerable<(ulong, ulong, uint, uint, int, string)> SfdumpRows(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length < 13)
            {
                continue;
            }

            var ip = Convert.ToUInt64(f[1].Split(':')[1], 16);
            var tsc = Convert.ToUInt64(f[12], 16);
            yield return (tsc, ip, uint.Parse(f[7]), uint.Parse(f[9]), int.Parse(f[6]), f[10]);
        }
    }
}

internal static class Fixtures
{
    /// <summary>
    /// xUnit collection for tests that open .tb7 files. TBRW_open fails (status 44) while another
    /// handle has the same file open, so these tests must not run in parallel.
    /// </summary>
    public const string Tb7Collection = "tb7 files";

    public static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    public static string SmokeTb7 => Path.Combine(RepoRoot(), "fixtures", "smoke", "smoke.tb7");

    /// <summary>3 s `sep -lbr call_stack:usr` capture (5,224 samples) and the head of its sfdump -samples -lbr-raw output.</summary>
    public static string LbrTb7 => Path.Combine(RepoRoot(), "fixtures", "lbr", "lbr.tb7");

    public static string LbrSamplesHead => Path.Combine(RepoRoot(), "fixtures", "lbr", "samples_lbr_head.txt");

    public static string SmokeSamplesTxt => Path.Combine(RepoRoot(), "fixtures", "smoke", "samples.txt");

    public static string SmokeTypeperfCsv => Path.Combine(RepoRoot(), "fixtures", "smoke", "typeperf.csv");

    /// <summary>An unbound .tb7 straight from SEP, if one is present under fixtures.</summary>
    public static string? UnboundTb7
    {
        get
        {
            var path = Path.Combine(RepoRoot(), "fixtures", "unbound", "unbound.tb7");
            return File.Exists(path) ? path : null;
        }
    }
}
