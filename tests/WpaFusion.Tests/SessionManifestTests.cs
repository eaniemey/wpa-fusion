using WpaFusion.Common;
using Xunit;

namespace WpaFusion.Tests;

public class SessionManifestTests
{
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
