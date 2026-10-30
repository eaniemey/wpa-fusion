using System.Xml.Linq;
using WpaFusion.Capture;
using WpaFusion.Capture.Tools;
using Xunit;

namespace WpaFusion.Tests;

public class CapturePreciseProfileTests
{
    [Fact]
    public void Precise_profile_records_context_switches_with_stacks_and_no_sampled_profile()
    {
        var doc = XDocument.Parse(WprCollector.PreciseProfileXml());
        var keywords = doc.Descendants("Keyword").Select(k => (string?)k.Attribute("Value")).ToArray();
        Assert.Contains("CSwitch", keywords);
        Assert.Contains("ReadyThread", keywords);
        Assert.Contains("ProcessThread", keywords);
        Assert.Contains("Loader", keywords);
        Assert.DoesNotContain("SampledProfile", keywords);
        Assert.Equal(new[] { "CSwitch", "ReadyThread" }, doc.Descendants("Stack").Select(s => (string?)s.Attribute("Value")).ToArray());
        Assert.Equal(WprCollector.PreciseProfileName, (string?)doc.Descendants("Profile").Single().Attribute("Name"));
    }

    [Fact]
    public void Default_wpr_profile_is_the_precise_one()
    {
        var options = CaptureOptions.Parse(new[] { "--duration", "1" });
        Assert.Equal(new[] { WprCollector.PreciseProfileName }, options.WprProfiles);
        Assert.Equal(1, options.RunCount);
        Assert.False(options.SkipAttribute);
        Assert.Null(options.SepResumeAfterSeconds);
    }

    [Fact]
    public void Automatic_attribution_is_default_and_raw_capture_is_an_explicit_opt_out()
    {
        Assert.False(CaptureOptions.Parse(Array.Empty<string>()).SkipAttribute);
        Assert.True(CaptureOptions.Parse(new[] { "--skip-attribute" }).SkipAttribute);
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--no-etw" }));
        Assert.True(CaptureOptions.Parse(new[] { "--no-etw", "--skip-attribute" }).SkipAttribute);
        var batch = CaptureOptions.Parse(new[] { "--runs", "2", "--", "repeatable.exe" });
        Assert.False(batch.SkipAttribute);
        Assert.True(batch.ForBatchRun(false, "sep").SkipAttribute);
        Assert.True(batch.ForBatchRun(true, "emon").SkipAttribute);
    }

    [Fact]
    public void Attribution_executable_selection_and_arguments_support_portable_capture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpafusion-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sibling = Path.Combine(directory, "wpafusion-attribute.exe");
            File.WriteAllText(sibling, "test executable path only");
            Assert.Equal(sibling, CapturePostProcessor.FindExecutable(directory, directory, null));
            Assert.Equal(sibling, CapturePostProcessor.FindExecutable(directory, directory, $"\"{sibling}\""));
            Assert.Throws<FileNotFoundException>(() => CapturePostProcessor.FindExecutable(directory, directory, Path.Combine(directory, "missing.exe")));
            var start = CapturePostProcessor.CreateStartInfo(sibling, Path.Combine(directory, "run with spaces"));
            Assert.False(start.UseShellExecute);
            Assert.Equal(Path.Combine(directory, "run with spaces"), Assert.Single(start.ArgumentList));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Attribution_failure_preserves_raw_data_and_does_not_mark_capture_failed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpafusion-post-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var raw = Path.Combine(directory, "raw-data.txt");
            File.WriteAllText(raw, "preserve this capture");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CapturePostProcessor.RunAsync(directory));
            Assert.Contains("Capture completed successfully", error.Message);
            Assert.Equal("preserve this capture", File.ReadAllText(raw));
            Assert.True(File.Exists(Path.Combine(directory, "ATTRIBUTION-FAILED.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "CAPTURE-FAILED.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Timed_sep_resume_is_opt_in_and_requires_start_paused()
    {
        var options = CaptureOptions.Parse(new[] { "--sep-start-paused", "--sep-resume-after", "30", "--duration", "5" });
        Assert.Equal(30, options.SepResumeAfterSeconds);
        Assert.Equal(5, options.DurationSeconds);
        Assert.Contains(" -sp ", SepCollector.BuildCommandLine(options, "run"));
        Assert.DoesNotContain(" -sd ", SepCollector.BuildCommandLine(options, "run"));
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--sep-resume-after", "30" }));
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--sep-start-paused", "--sep-resume-after", "Infinity" }));
    }

    [Fact]
    public void Sep_only_duration_preserves_full_workload_capture_and_is_valid_for_opt_in_batch()
    {
        var options = CaptureOptions.Parse(new[] { "--runs", "2", "--sep-start-paused", "--sep-resume-after", "30",
            "--sep-duration", "5", "--", "repeatable.exe" });
        Assert.Null(options.DurationSeconds);
        var sep = options.ForBatchRun(false, "sep-output");
        Assert.True(sep.Etw);
        Assert.True(sep.SepStartPaused);
        Assert.Equal(30, sep.SepResumeAfterSeconds);
        Assert.Equal(5, sep.SepDurationSeconds);
        var emon = options.ForBatchRun(true, "emon-output");
        Assert.False(emon.SepStartPaused);
        Assert.Null(emon.SepResumeAfterSeconds);
        Assert.Null(emon.SepDurationSeconds);
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--sep-duration", "5" }));
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--sep-duration", "5", "--duration", "5", "--", "repeatable.exe" }));
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--sep-duration", "5", "--no-etw", "--", "repeatable.exe" }));
    }

    [Fact]
    public void Multi_run_is_explicit_and_requires_completed_repeatable_workloads()
    {
        var options = CaptureOptions.Parse(new[] { "--runs", "2", "--", "repeatable.exe" });
        Assert.Equal(2, options.RunCount);
        Assert.True(options.ForBatchRun(false, "sep-output").Sep);
        Assert.True(options.ForBatchRun(false, "sep-output").Etw);
        Assert.False(options.ForBatchRun(false, "sep-output").Emon);
        Assert.True(options.ForBatchRun(true, "emon-output").Emon);
        Assert.False(options.ForBatchRun(true, "emon-output").Sep);
        Assert.False(options.ForBatchRun(true, "emon-output").Etw);
        Assert.Equal(1, options.ForBatchRun(true, "emon-output").RunCount);
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--runs", "2" }));
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--runs", "3", "--", "repeatable.exe" }));
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--runs", "4", "--", "repeatable.exe" }));
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--runs", "2", "--duration", "1", "--", "repeatable.exe" }));
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--runs", "2", "--emon", "--", "repeatable.exe" }));
    }

    [Fact]
    public void Emon_mode_replaces_sep_but_keeps_etw_and_typeperf()
    {
        var options = CaptureOptions.Parse(new[] { "--emon" });
        Assert.True(options.Emon);
        Assert.False(options.Sep);
        Assert.True(options.Etw);
        Assert.True(options.Typeperf);
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--emon", "--sep-start-paused" }));
        Assert.False(CaptureOptions.Parse(new[] { "--emon", "--no-etw" }).Etw);
    }

    [Fact]
    public void Emon_event_override_uses_a_run_local_snapshot()
    {
        var path = Path.GetTempFileName();
        try
        {
            var options = CaptureOptions.Parse(new[] { "--emon", "--emon-events", path });
            var command = EmonCollector.BuildCommandLine(options, "run folder");
            Assert.Contains("-collect-edp", command);
            Assert.Contains("\"edp_file=" + Path.Combine("run folder", EmonCollector.EventsFileName) + "\"", command);
            Assert.DoesNotContain(path, command);
            Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--emon-events", path }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sep_pause_options_parse_and_require_sep()
    {
        var options = CaptureOptions.Parse(new[] { "--sep-start-paused", "--sep-sampling-delay", "2.5" });
        Assert.True(options.SepStartPaused);
        Assert.Equal(2.5, options.SepSamplingDelaySeconds);
        Assert.Throws<ArgumentException>(() => CaptureOptions.Parse(new[] { "--no-sep", "--sep-start-paused" }));
    }
}

public class CaptureLbrOptionTests
{
    private static CaptureOptions Parse(params string[] args) => CaptureOptions.Parse(args);

    [Fact]
    public void Default_sep_collection_uses_uarch_exploration_and_user_lbr()
    {
        var options = Parse();
        var command = SepCollector.BuildCommandLine(options, "run");
        Assert.Equal("uarch-exploration", options.SepAtype);
        Assert.Equal("call_stack:usr", options.SepLbr);
        Assert.Contains("-atype uarch-exploration", command);
        Assert.Contains("-lbr call_stack:usr", command);
    }

    [Fact]
    public void Explicit_mode_enables_lbr_and_off_disables_it()
    {
        Assert.Equal("call_stack:os", Parse("--sep-lbr", "call_stack:os").SepLbr);
        Assert.Equal("call_stack:usr", Parse("--sep-lbr", "call_stack:usr").SepLbr);
        var options = Parse("--sep-lbr", "call_stack:usr", "--sep-lbr", "off");
        Assert.Null(options.SepLbr);
        Assert.DoesNotContain("-lbr", SepCollector.BuildCommandLine(options, "run"));
    }

    [Fact]
    public void An_lbr_given_through_sep_args_is_preserved_without_duplication()
    {
        var options = Parse("--sep-args", "-lbr no_filter");
        var command = SepCollector.BuildCommandLine(options, "run");
        Assert.Contains("-lbr no_filter", command);
        Assert.DoesNotContain("-lbr call_stack:usr", command);
        Assert.Equal(1, command.Split("-lbr").Length - 1);
    }

    [Fact]
    public void Sep_command_line_carries_lbr_with_the_analysis_type_and_extra_args()
    {
        var options = Parse("--sep-lbr", "call_stack:usr", "--sep-args", "-sam 4");
        var command = SepCollector.BuildCommandLine(options, "run");
        Assert.Contains("-atype uarch-exploration", command);
        Assert.Contains("-lbr call_stack:usr", command);
        Assert.EndsWith("-sam 4", command);
    }

    [Fact]
    public void Sep_command_line_starts_paused_and_applies_sampling_delay()
    {
        var options = Parse("--sep-start-paused", "--sep-sampling-delay", "2.5");
        var command = SepCollector.BuildCommandLine(options, "run");
        Assert.Contains("-sp", command);
        Assert.Contains("-sd 2.5", command);
    }

    [Fact]
    public void Sep_atype_list_is_parsed_from_sep_output()
    {
        var atypes = CaptureSession.SepAtypes("atype: hotspots \r\natype: uarch-exploration \r\n\r\n");
        Assert.Equal(new[] { "hotspots", "uarch-exploration" }, atypes);
    }
}
