using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WpaFusion.Common;

internal static class FusionPipeline
{
    public static async Task<int> ProcessEmonAsync(string source)
    {
        var manifest = SessionManifest.Load(Path.Combine(source, SessionManifest.FileName));
        if (File.Exists(Path.Combine(source, "CAPTURE-FAILED.txt")) || manifest.Files.EmonDat is null ||
            manifest.Alignment is not null || string.IsNullOrWhiteSpace(manifest.CaptureTimeZoneId))
        {
            throw new InvalidDataException("EMON processing requires a completed native capture with a collection time zone.");
        }

        var mpp = FindMpp();
        var output = Path.Combine(RunFolder.DefaultOutputRoot(), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-emon");
        if (Directory.Exists(output))
        {
            throw new IOException("Processed EMON output already exists; refusing to overwrite it.");
        }

        Directory.CreateDirectory(output);
        try
        {
            CopyDirectory(source, output);
            File.Delete(Path.Combine(output, "ATTRIBUTION-FAILED.txt"));
            var processed = await ProcessEmonDirectoryAsync(output, mpp);
            processed.ProcessedFrom = source;
            processed.Save(Path.Combine(output, SessionManifest.FileName));
            _ = WpaFusion.Tma.EmonMetricData.Load(Path.Combine(output, processed.Files.EmonMetrics!));
            Console.WriteLine($"Processed EMON capture with Intel MPP. Raw input: {source}");
            Console.WriteLine($"Output: {output}");
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(output, "PROCESSING-FAILED.txt"), exception.ToString());
            throw;
        }
    }

    public static async Task<int> RunAsync(string batchDirectory, string? symbolPath, string stacksMode)
    {
        var batch = CaptureBatchManifest.Load(Path.Combine(batchDirectory, CaptureBatchManifest.FileName));
        var inputs = batch.Runs.Select(run =>
        {
            var directory = CaptureBatchManifest.ResolveRun(batchDirectory, run);
            var manifest = SessionManifest.Load(Path.Combine(directory, SessionManifest.FileName));
            if (manifest.RunId != run.RunId || !manifest.WorkloadCommand.SequenceEqual(batch.WorkloadCommand) ||
                run.Kind == "SEP" && (manifest.Files.Tb7 is null || manifest.Files.Etl is null || manifest.Etl is null) ||
                run.Kind == "EMON" && manifest.Files.EmonDat is null)
            {
                throw new InvalidDataException("Batch source identity or collector inputs do not match the batch manifest.");
            }

            return (Run: run, Directory: directory, Manifest: manifest);
        }).ToArray();
        var reference = inputs[0].Manifest;
        var alignments = inputs.Select(input => CrossRunAlignment.Create(reference, input.Manifest)).ToArray();
        var mpp = FindMpp();
        var output = Path.Combine(RunFolder.DefaultOutputRoot(), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-fused");
        if (Directory.Exists(output))
        {
            throw new IOException("Fused output already exists; refusing to overwrite it.");
        }

        Directory.CreateDirectory(output);
        var fused = new FusedDatasetManifest { BatchId = batch.BatchId, ReferenceDirectory = "runs/001-sep" };
        fused.Warnings.Add("Separate workload executions aligned by workload-start markers. No time stretching or instruction/process-level causality is inferred.");
        try
        {
            for (var index = 0; index < inputs.Length; index++)
            {
                var input = inputs[index];
                var relative = $"runs/{index + 1:D3}-{input.Run.Kind.ToLowerInvariant()}";
                var destination = Path.Combine(output, relative);
                CopyDirectory(input.Directory, destination);
                SessionManifest processed;
                if (input.Run.Kind == "SEP")
                {
                    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the attribution executable.");
                    var arguments = new List<string>();
                    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    {
                        arguments.Add(typeof(FusionPipeline).Assembly.Location);
                    }

                    arguments.Add(input.Directory);
                    if (symbolPath is not null)
                    {
                        arguments.Add(symbolPath);
                    }

                    arguments.AddRange(new[] { "--stacks", stacksMode });
                    var text = await RunToolAsync(executable, arguments);
                    File.WriteAllText(Path.Combine(destination, "attribution.log"), text);
                    var line = text.Split('\n').LastOrDefault(value => value.StartsWith("Output: ", StringComparison.Ordinal))
                        ?? throw new InvalidDataException("SEP attribution did not report its processed run.");
                    var attributed = line["Output: ".Length..].Trim();
                    CopyDirectory(attributed, destination);
                    processed = SessionManifest.Load(Path.Combine(destination, SessionManifest.FileName));
                }
                else
                {
                    processed = await ProcessEmonDirectoryAsync(destination, mpp);
                }

                processed.Alignment = alignments[index];
                processed.ProcessedFrom = input.Directory;
                processed.Save(Path.Combine(destination, SessionManifest.FileName));
                fused.Sources.Add(new FusedDatasetSource(input.Run.Kind, relative, processed.RunId, alignments[index]));
                var difference = alignments[index].DurationDifferencePercent;
                var status = $"{input.Run.Kind} run {processed.RunId}: workload duration difference {difference:F2}% relative to {reference.RunId}; offset {alignments[index].OffsetNs / 1e6:F3} ms.";
                Console.WriteLine(status);
                if (Math.Abs(difference) > 10)
                {
                    fused.Warnings.Add(status + " Timing repeatability is weak; inspect phase correspondence before comparing metrics.");
                }
            }

            fused.Save(Path.Combine(output, FusedDatasetManifest.FileName));
            foreach (var warning in fused.Warnings)
            {
                Console.WriteLine("WARNING: " + warning);
            }

            Console.WriteLine($"Output: {output}");
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(output, "PROCESSING-FAILED.txt"), exception.ToString());
            throw;
        }
    }

    private static async Task<SessionManifest> ProcessEmonDirectoryAsync(string directory, string mpp)
    {
        var manifest = SessionManifest.Load(Path.Combine(directory, SessionManifest.FileName));
        var dataName = manifest.Files.EmonDat;
        if (string.IsNullOrWhiteSpace(dataName) || Path.GetFileName(dataName) != dataName || dataName.Contains('/') || dataName.Contains('\\'))
        {
            throw new InvalidDataException("EMON data must be a filename inside its run.");
        }

        var definition = EmonMetricDefinitions.Snapshot(Path.Combine(directory, dataName), directory, SepInstall.Root);
        var metricInput = definition is null ? Path.Combine(SepInstall.Root, "config", "edp") : Path.Combine(directory, definition);
        var text = await RunToolAsync(mpp, new[] { "-m", metricInput, "-i", Path.Combine(directory, dataName), "-o", Path.Combine(directory, "emon") });
        File.WriteAllText(Path.Combine(directory, "mpp.log"), text);
        const string details = "emon_system_view_details.csv";
        const string summary = "emon_system_view_summary.csv";
        if (!File.Exists(Path.Combine(directory, details)) || !File.Exists(Path.Combine(directory, summary)))
        {
            throw new InvalidDataException("Intel MPP did not produce the expected system-view CSVs.");
        }

        File.WriteAllText(Path.Combine(directory, "metrics.emon.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, details, summary, metricDefinitions = definition }));
        manifest.Files.EmonMetrics = "metrics.emon.json";
        manifest.Files.EmonDefinition = definition;
        manifest.Tools["mpp"] = mpp;
        return manifest;
    }

    private static string FindMpp()
    {
        var configured = EnvironmentSetting.Get("WPA_FUSION_MPP_EXE");
        var development = Path.Combine(Path.GetDirectoryName(RunFolder.DefaultOutputRoot())!, "artifacts", "mpp-env", "Scripts", "mpp.exe");
        var candidates = string.IsNullOrWhiteSpace(configured)
            ? (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.Combine(path, "mpp.exe")).Append(development)
            : new[] { configured.Trim().Trim('"') };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Install Intel MPP and set WPA_FUSION_MPP_EXE before processing EMON data.");
    }

    private static async Task<string> RunToolAsync(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {executable}.");
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
            throw new TimeoutException($"{Path.GetFileName(executable)} exceeded the processing timeout.");
        }

        var text = await stdout + Environment.NewLine + await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed with code {process.ExitCode}: {text}");
        }

        return text;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Fusion input folders must not contain filesystem links.");
            }

            var target = Path.Combine(destination, Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                CopyDirectory(path, target);
            }
            else
            {
                File.Copy(path, target, overwrite: true);
            }
        }
    }
}