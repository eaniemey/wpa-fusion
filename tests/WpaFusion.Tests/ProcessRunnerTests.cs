using System.Diagnostics;
using WpaFusion.Capture.Tools;
using Xunit;

namespace WpaFusion.Tests;

/// <summary>
/// Regression tests for the collector shutdown crash: background process output arrives on callback threads
/// after the process exits, and used to hit an already-disposed log writer (ObjectDisposedException).
/// </summary>
public class ProcessRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wpafusion-runner-" + Guid.NewGuid().ToString("N"));

    public ProcessRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_dir))
        {
            File.Delete(file);
        }

        Directory.Delete(_dir);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void LogSink_ignores_writes_after_it_is_closed_and_is_safe_from_many_threads()
    {
        var path = Path.Combine(_dir, "sink.log");
        var sink = new LogSink(path);
        Parallel.For(0, 200, i => sink.WriteLine($"line {i}"));
        sink.Dispose();
        sink.WriteLine("after close"); // used to be an ObjectDisposedException
        sink.Dispose();
        var lines = File.ReadAllLines(path);
        Assert.Equal(200, lines.Length);
        Assert.DoesNotContain("after close", lines);
    }

    [Fact]
    public void Short_lived_children_that_print_right_before_exiting_log_every_line_without_crashing()
    {
        using var log = new RunLog(Path.Combine(_dir, "capture.log"));
        var runner = new ProcessRunner(log);
        for (var run = 0; run < 25; run++)
        {
            var path = Path.Combine(_dir, $"child{run}.log");
            // stdout and stderr, 100 lines each, then exit immediately.
            using var p = runner.StartBackground(Cmd, "/c \"for /l %i in (1,1,100) do @(echo out %i & echo err %i 1>&2)\"", path);
            Assert.True(p.WaitForExit(30_000));
            WaitUntil(() => ReadShared(path).Contains("out 100") && ReadShared(path).Contains("err 100"), "the last output lines");
        }

        runner.CloseLogs();
    }

    [Fact]
    public void Output_arriving_after_the_logs_are_closed_is_dropped_not_thrown()
    {
        using var log = new RunLog(Path.Combine(_dir, "capture.log"));
        var runner = new ProcessRunner(log);
        var path = Path.Combine(_dir, "late.log");
        using var p = runner.StartBackground(Cmd, "/c \"ping -n 3 127.0.0.1 >nul & echo late\"", path);
        runner.CloseLogs(); // the process is still running; its output arrives afterwards
        Assert.True(p.WaitForExit(30_000));
        Thread.Sleep(300); // give the late callbacks time to fire; a regression crashes the test host
        Assert.DoesNotContain("late", ReadShared(path));
    }

    [Fact]
    public void Killed_long_running_child_does_not_crash_and_log_keeps_what_it_printed()
    {
        using var log = new RunLog(Path.Combine(_dir, "capture.log"));
        var runner = new ProcessRunner(log);
        var path = Path.Combine(_dir, "killed.log");
        using var p = runner.StartBackground(Cmd, "/c \"echo started & ping -n 30 127.0.0.1 >nul\"", path);
        WaitUntil(() => ReadShared(path).Contains("started"), "the first output line");
        p.Kill(entireProcessTree: true);
        Assert.True(p.WaitForExit(30_000));
        runner.CloseLogs();
        Assert.Contains("started", ReadShared(path));
    }
}
