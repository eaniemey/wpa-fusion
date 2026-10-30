using WpaFusion.Common;
using Xunit;

namespace WpaFusion.Tests;

public class SessionManifestTests
{
    [Fact]
    public void Emon_definition_snapshot_uses_the_captured_filename_and_then_survives_without_installation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpafusion-definitions-" + Guid.NewGuid().ToString("N"));
        var edp = Path.Combine(directory, "sep", "config", "edp");
        var run = Path.Combine(directory, "run");
        Directory.CreateDirectory(edp);
        Directory.CreateDirectory(run);
        try
        {
            var raw = Path.Combine(run, "emon.dat");
            File.WriteAllText(raw, "EDP metric file:captured-platform.xml\n");
            var selected = Path.Combine(edp, "captured-platform.xml");
            File.WriteAllText(selected, "<root />");
            Assert.Equal(selected, EmonMetricDefinitions.Find(raw, Path.Combine(directory, "sep")));
            Assert.Equal(EmonMetricDefinitions.SnapshotFileName, EmonMetricDefinitions.Snapshot(raw, run, Path.Combine(directory, "sep")));
            File.Delete(selected);
            Assert.Equal(Path.Combine(run, EmonMetricDefinitions.SnapshotFileName), EmonMetricDefinitions.Find(raw, "not-installed"));
            Assert.Equal("<root />", File.ReadAllText(Path.Combine(run, EmonMetricDefinitions.SnapshotFileName)));
            File.Delete(Path.Combine(run, EmonMetricDefinitions.SnapshotFileName));
            File.WriteAllText(raw, "EDP metric file:../outside.xml\n");
            Assert.Throws<InvalidDataException>(() => EmonMetricDefinitions.Find(raw, Path.Combine(directory, "sep")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Cross_run_alignment_uses_workload_start_without_stretching_time()
    {
        var reference = Sample();
        reference.RunId = "sep";
        reference.Machine = "host";
        reference.Etl = new EtlClockReference(100, 200);
        reference.WorkloadCommand.Add("repeatable.exe");
        reference.WorkloadExitCode = 0;
        reference.Markers.Add(new SessionMarker("workload-start", 0, 1100, 120, 220));
        reference.Markers.Add(new SessionMarker("workload-end", 0, 1900, 220, 320));
        var source = Sample();
        source.RunId = "emon";
        source.Machine = "host";
        source.Etl = new EtlClockReference(100, 200);
        source.WorkloadCommand.Add("repeatable.exe");
        source.WorkloadExitCode = 0;
        source.Markers.Add(new SessionMarker("workload-start", 0, 1300, 150, 250));
        source.Markers.Add(new SessionMarker("workload-end", 0, 1900, 270, 370));
        var alignment = CrossRunAlignment.Create(reference, source);
        Assert.Equal(-3000, alignment.OffsetNs);
        Assert.Equal(12000, alignment.SourceWorkloadDurationNs);
        Assert.Equal(10000, alignment.ReferenceWorkloadDurationNs);
        Assert.Equal(20, alignment.DurationDifferencePercent);
        source.Etl = null;
        var emonWithoutEtl = CrossRunAlignment.Create(reference, source);
        Assert.Equal(alignment.OffsetNs, emonWithoutEtl.OffsetNs);
        source.WorkloadExitCode = 1;
        Assert.Throws<InvalidDataException>(() => CrossRunAlignment.Create(reference, source));
        source.WorkloadExitCode = 0;
        source.WorkloadCommand.Add("different-argument");
        Assert.Throws<InvalidDataException>(() => CrossRunAlignment.Create(reference, source));
        source.WorkloadCommand.RemoveAt(1);
        source.Machine = "different-machine";
        Assert.Throws<InvalidDataException>(() => CrossRunAlignment.Create(reference, source));
    }

    [Fact]
    public void Batch_and_fusion_manifests_preserve_identity_and_reject_unsafe_paths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpafusion-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var batch = new CaptureBatchManifest { WorkloadCommand = new List<string> { "repeatable.exe" } };
            batch.Runs.Add(new CaptureBatchRun("SEP", "001-sep/run", "sep"));
            batch.Runs.Add(new CaptureBatchRun("EMON", "002-emon/run", "emon"));
            var path = Path.Combine(directory, CaptureBatchManifest.FileName);
            batch.Save(path);
            Assert.Throws<InvalidDataException>(() => CaptureBatchManifest.Load(path));
            batch.Complete = true;
            batch.Save(path);
            Assert.Equal(batch.BatchId, CaptureBatchManifest.Load(path).BatchId);
            Assert.Throws<InvalidDataException>(() => CaptureBatchManifest.ResolveRun(directory,
                new CaptureBatchRun("EMON", "../outside", "emon")));
            var alignment = new CrossRunAlignment("emon", "sep", 100, 1000, 100, 90);
            var fusion = new FusedDatasetManifest { BatchId = batch.BatchId, ReferenceDirectory = "runs/sep" };
            fusion.Sources.Add(new FusedDatasetSource("SEP", "runs/sep", "sep", alignment with { SourceRunId = "sep", OffsetNs = 0 }));
            fusion.Sources.Add(new FusedDatasetSource("EMON", "runs/emon", "emon", alignment));
            var fusedPath = Path.Combine(directory, FusedDatasetManifest.FileName);
            fusion.Save(fusedPath);
            var loaded = FusedDatasetManifest.Load(fusedPath);
            Assert.True(loaded.SeparateExecutions);
            Assert.Equal(alignment, loaded.Sources[1].Alignment);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SessionManifest Sample() => new()
    {
        RunId = "20261021-100000",
        QpcFrequency = 10_000_000,
        SyncStart = new SyncPoint(1000, 100, 200, 5),
        SyncEnd = new SyncPoint(2000, 300, 400, 5),
    };

    [Fact]
    public void Processed_from_round_trips_through_session_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpafusion-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, SessionManifest.FileName);
            var manifest = Sample();
            manifest.ProcessedFrom = @"F:\wpa-fusion\output\20261021-100000";
            manifest.Save(path);

            var loaded = SessionManifest.Load(path);
            Assert.Equal(manifest.ProcessedFrom, loaded.ProcessedFrom);
            Assert.Equal("20261021-100000", loaded.RunId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Raw_capture_without_processed_from_still_loads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpafusion-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, SessionManifest.FileName);
            Sample().Save(path);

            var loaded = SessionManifest.Load(path);
            Assert.Null(loaded.ProcessedFrom);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
