using WpaFusion.Common;

namespace WpaFusion.Capture;

internal sealed class CaptureBatch
{
    private readonly CaptureOptions _options;

    public CaptureBatch(CaptureOptions options) => _options = options;
    public string? RunDirectory { get; private set; }

    public int Run()
    {
        var folder = RunFolder.Create(_options.OutputRoot ?? RunFolder.DefaultOutputRoot(), DateTime.Now);
        RunDirectory = folder;
        var path = Path.Combine(folder, CaptureBatchManifest.FileName);
        var batch = new CaptureBatchManifest { WorkloadCommand = _options.Workload.ToList() };
        batch.Save(path);
        Console.WriteLine($"Capture batch: {folder}; {_options.RunCount} separate workload executions.");
        try
        {
            for (var index = 0; index < _options.RunCount; index++)
            {
                var emon = index % 2 != 0;
                var kind = emon ? "EMON" : "SEP";
                var session = new CaptureSession(_options.ForBatchRun(emon, Path.Combine(folder, $"{index + 1:D3}-{kind.ToLowerInvariant()}")));
                Console.WriteLine($"Run {index + 1}/{_options.RunCount}: {kind}");
                session.Run();
                var run = session.RunDirectory!;
                var manifest = SessionManifest.Load(Path.Combine(run, SessionManifest.FileName));
                if (manifest.WorkloadExitCode != 0)
                {
                    throw new InvalidOperationException($"{kind} workload did not complete successfully; batch remains incomplete.");
                }

                batch.Runs.Add(new CaptureBatchRun(kind, Path.GetRelativePath(folder, run), manifest.RunId));
                batch.Save(path);
            }

            batch.Complete = true;
            batch.Save(path);
            if (_options.SkipAttribute)
            {
                Console.WriteLine($"Output: {folder}");
                Console.WriteLine($"Attribute/fuse this batch with wpafusion-attribute \"{folder}\". Alignment compares separate executions, not simultaneous events.");
            }
            else
            {
                Console.WriteLine($"Raw batch complete: {folder}; automatic attribution/fusion starts next.");
            }
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(folder, "CAPTURE-FAILED.txt"), exception.ToString());
            throw;
        }
    }
}