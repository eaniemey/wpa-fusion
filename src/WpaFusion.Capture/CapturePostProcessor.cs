using System.Diagnostics;
using WpaFusion.Common;

namespace WpaFusion.Capture;

internal static class CapturePostProcessor
{
    public const string ExecutableEnvironmentVariable = "WPA_FUSION_ATTRIBUTE_EXE";

    internal static string FindExecutable(string appDirectory, string repositoryRoot, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim().Trim('"');
            return File.Exists(path) ? Path.GetFullPath(path) :
                throw new FileNotFoundException($"{ExecutableEnvironmentVariable} points to a missing executable: {path}");
        }

        const string name = "wpafusion-attribute.exe";
        var sibling = Path.Combine(appDirectory, name);
        if (File.Exists(sibling))
        {
            return sibling;
        }

        var builds = new[] { "Debug", "Release" }
            .Select(configuration => Path.Combine(repositoryRoot, "src", "WpaFusion.Attribution", "bin", configuration, "net6.0", name))
            .Where(File.Exists).Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc);
        var build = builds.FirstOrDefault();
        if (build is not null)
        {
            return build.FullName;
        }

        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.Combine(path.Trim('"'), name))
            .FirstOrDefault(File.Exists);
        return onPath ?? throw new FileNotFoundException($"Cannot find {name}. Build the solution, place it beside capture, set {ExecutableEnvironmentVariable}, or use --skip-attribute for raw-only capture.");
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string runDirectory)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(runDirectory);
        return info;
    }

    public static async Task RunAsync(string runDirectory)
    {
        try
        {
            var executable = FindExecutable(AppContext.BaseDirectory,
                Path.GetDirectoryName(RunFolder.DefaultOutputRoot())!, EnvironmentSetting.Get(ExecutableEnvironmentVariable));
            Console.WriteLine($"Capture complete. Running attribution: {executable}");
            using var process = Process.Start(CreateStartInfo(executable, runDirectory))
                ?? throw new InvalidOperationException("Could not start wpafusion-attribute.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("Automatic attribution exceeded 30 minutes.");
            }

            var output = await stdout;
            var errors = await stderr;
            File.WriteAllText(Path.Combine(runDirectory, "post-capture.log"), output + Environment.NewLine + errors);
            Console.Write(output);
            if (!string.IsNullOrWhiteSpace(errors))
            {
                Console.Error.Write(errors);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"wpafusion-attribute exited with code {process.ExitCode}; see post-capture.log.");
            }

            var line = output.Split('\n').LastOrDefault(value => value.StartsWith("Output: ", StringComparison.Ordinal))
                ?? throw new InvalidDataException("Attribution succeeded without reporting a processed Output path.");
            var processed = line["Output: ".Length..].Trim();
            if (!Directory.Exists(processed) ||
                (!File.Exists(Path.Combine(processed, SessionManifest.FileName)) && !File.Exists(Path.Combine(processed, FusedDatasetManifest.FileName))))
            {
                throw new InvalidDataException("Attribution reported an incomplete output dataset.");
            }

            Console.WriteLine($"Processed dataset: {processed}");
            Console.WriteLine($"Open with: scripts\\open-run.ps1 -Run \"{processed}\" -NoAttribute");
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(runDirectory, "ATTRIBUTION-FAILED.txt"), exception.ToString());
            throw new InvalidOperationException($"Capture completed successfully at '{runDirectory}', but automatic attribution failed. Raw data is preserved; retry wpafusion-attribute on this folder. {exception.Message}", exception);
        }
    }
}