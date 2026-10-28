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
