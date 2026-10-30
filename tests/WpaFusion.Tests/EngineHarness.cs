using Microsoft.Performance.SDK.Processing;
using Microsoft.Performance.Toolkit.Engine;
using Xunit;
using WpaFusion.Common;

namespace WpaFusion.Tests;

/// <summary>
/// Loads WPA-Fusion plugins from their build output folders the way WPA does (SDK supplied by the
/// host, plugin assemblies discovered from the folder) and builds tables headless.
/// </summary>
internal static class EngineHarness
{
    public static ITableResult BuildEmonTable(PluginSet plugins, TableDescriptor table, params string[] additionalFiles)
        => WithEmonFixture(path => BuildTable(plugins, table, additionalFiles.Prepend(path).ToArray()));

    public static TResult WithEmonFixture<TResult>(Func<string, TResult> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpafusion-emon-table-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var zero = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            new SessionManifest
            {
                RunId = "emon-table-test",
                CaptureTimeZoneId = "UTC",
                QpcFrequency = 10_000_000,
                SyncStart = new SyncPoint(1000, 100, zero.ToFileTimeUtc(), 1),
                SyncEnd = new SyncPoint(2000, 200, zero.AddSeconds(3).ToFileTimeUtc(), 1),
                Etl = new EtlClockReference(100, zero.ToFileTimeUtc()),
            }.Save(Path.Combine(directory, SessionManifest.FileName));
            File.WriteAllText(Path.Combine(directory, "summary.csv"), """
                name,aggregated,description
                metric_CPI,2,Cycles per instruction
                metric_TMA_Retiring(%),50,Retiring
                """);
            File.WriteAllText(Path.Combine(directory, "details.csv"), """
                #sample,timestamp,metric_CPI,metric_TMA_Retiring(%),$samplingTime
                1,2026-10-30T12:00:01,1,40,1
                2,2026-10-30T12:00:02,3,60,1
                """);
            var path = Path.Combine(directory, "metrics.emon.json");
            File.WriteAllText(path, """{"schemaVersion":1,"details":"details.csv","summary":"summary.csv"}""");
            return action(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static string PluginDir(string project) =>
        Path.Combine(Fixtures.RepoRoot(), "src", project, "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net6.0");

    public static PluginSet LoadPlugins(params string[] projects)
    {
        var dirs = projects.Select(PluginDir).ToList();
        foreach (var d in dirs)
        {
            Assert.True(Directory.Exists(d), $"Plugin output folder missing: {d}");
        }

        var plugins = PluginSet.Load(dirs);
        Assert.Empty(plugins.CreationErrors.Select(e => e.ToString()));
        return plugins;
    }

    /// <summary>Processes the files and builds the given table.</summary>
    public static ITableResult BuildTable(PluginSet plugins, TableDescriptor table, params string[] files)
    {
        using var dataSources = DataSourceSet.Create(plugins, false);
        foreach (var f in files)
        {
            dataSources.AddDataSource(new FileDataSource(f));
        }

        using var engine = Engine.Create(new EngineCreateInfo(dataSources.AsReadOnly()));
        engine.EnableTable(table);
        using var results = engine.Process();
        var errors = results.ProcessingErrors.Select(e => string.Join("; ", e.GetType().GetProperties().Select(p => $"{p.Name}={p.GetValue(e)}"))).ToList();
        if (errors.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(string.Join(Environment.NewLine, errors));
        }

        return results.BuildTable(table);
    }
}
