using Microsoft.Performance.SDK.Processing;
using Microsoft.Performance.Toolkit.Engine;
using Xunit;

namespace WpaFusion.Tests;

/// <summary>
/// Loads WPA-Fusion plugins from their build output folders the way WPA does (SDK supplied by the
/// host, plugin assemblies discovered from the folder) and builds tables headless.
/// </summary>
internal static class EngineHarness
{
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
