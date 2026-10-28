using System.Diagnostics;
using System.Text;

namespace WpaFusion.Capture.Tools;

/// <summary>Runs collector command lines and logs their output to the run folder.</summary>
internal sealed class ProcessRunner
{
    private readonly RunLog _log;
    private readonly List<LogSink> _sinks = new();

    public ProcessRunner(RunLog log) => _log = log;

    /// <summary>Runs to completion; throws with the tool's output if the exit code is non-zero.</summary>
    public string Run(string exe, string arguments, TimeSpan timeout, bool throwOnError = true)
    {
        _log.Info($"> \"{exe}\" {arguments}");
        using var p = Start(exe, arguments, redirect: true);
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{Path.GetFileName(exe)} {arguments}' did not finish within {timeout.TotalSeconds:F0} s.");
        }

        // Do not call the parameterless WaitForExit: it also waits for EOF on the redirected
        // pipes, which never comes if the tool left a child process holding them.
        Thread.Sleep(50);
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        _log.Raw(text);
        if (throwOnError && p.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{Path.GetFileName(exe)} {arguments}' failed with exit code {p.ExitCode}:{Environment.NewLine}{text.Trim()}");
        }

        return text;
    }

    /// <summary>
    /// Starts a long-running process sharing this console (so it can receive Ctrl+C) and logs its output to
    /// <paramref name="logFile"/>. The log stays open until both output streams reach end-of-file or
    /// <see cref="CloseLogs"/> runs: the asynchronous reads keep delivering lines after the process exits, so
    /// closing on the Exited event would make a late line throw on a disposed writer.
    /// </summary>
    public Process StartBackground(string exe, string arguments, string logFile)
    {
        _log.Info($"> \"{exe}\" {arguments}   (background, output in {Path.GetFileName(logFile)})");
        var p = Start(exe, arguments, redirect: true);
        var sink = new LogSink(logFile);
        lock (_sinks)
        {
            _sinks.Add(sink);
        }

        var endedStreams = 0;
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                sink.WriteLine(e.Data);
            }
            else if (Interlocked.Increment(ref endedStreams) == 2)
            {
                sink.Dispose();
            }
        }

        p.OutputDataReceived += OnLine;
        p.ErrorDataReceived += OnLine;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    /// <summary>Closes every background log. Lines that arrive afterwards are dropped, never thrown.</summary>
    public void CloseLogs()
    {
        lock (_sinks)
        {
            foreach (var sink in _sinks)
            {
                sink.Dispose();
            }

            _sinks.Clear();
        }
    }

    private static Process Start(string exe, string arguments, bool redirect)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            RedirectStandardInput = redirect,
            CreateNoWindow = false,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{exe}'.");
    }
}

/// <summary>A process-output log file that can be written from callback threads and closed at any time.</summary>
internal sealed class LogSink : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    // FileShare.ReadWrite lets the log be tailed while the process is still running.
    public LogSink(string path) =>
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

    /// <summary>Appends a line; does nothing once the sink is closed.</summary>
    public void WriteLine(string line)
    {
        lock (_gate)
        {
            _writer?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>Writes progress to the console and to capture.log in the run folder.</summary>
internal sealed class RunLog : IDisposable
{
    private readonly StreamWriter _file;

    public RunLog(string path) => _file = new StreamWriter(path, append: false) { AutoFlush = true };

    public void Info(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(line);
        _file.WriteLine(line);
    }

    public void Raw(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _file.WriteLine(text.TrimEnd());
        }
    }

    public void Dispose() => _file.Dispose();
}
