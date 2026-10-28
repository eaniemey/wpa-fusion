using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Events;
using WpaFusion.Common;

namespace WpaFusion.Capture.Clocks;

/// <summary>
/// Reads the WPA-Fusion sync markers back from the ETL with TraceProcessor (the engine WPA uses for
/// ETW) and derives the ETL's time zero in QPC and UTC terms.
/// </summary>
internal static class EtlClockReader
{
    /// <summary>Directory of the installed Windows Performance Toolkit (its native toolkit drives TraceProcessor).</summary>
    public static string ToolkitPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Windows Performance Toolkit");

    /// <summary>One marker as seen in the ETL.</summary>
    public sealed record EtlMarker(string Name, int Index, long PayloadQpc, long PayloadFileTime, long EventRawTimestamp, long RelativeNanoseconds, long EventFileTimeUtc);

    public static IReadOnlyList<EtlMarker> ReadMarkers(string etlPath)
    {
        var settings = new TraceProcessorSettings
        {
            AllowLostEvents = true,
            SuppressFirstTimeSetupMessage = true,
            ToolkitPath = ToolkitPath,
        };

        using var trace = TraceProcessor.Create(etlPath, settings);
        var pending = trace.UseGenericEvents(new[] { MarkerSource.ProviderGuid });
        trace.Process();

        var markers = new List<EtlMarker>();
        foreach (var e in pending.Result.Events)
        {
            if (e.ProviderId != MarkerSource.ProviderGuid)
            {
                continue;
            }

            var f = e.Fields;
            markers.Add(new EtlMarker(
                f["Name"].AsString,
                f["Index"].AsInt32,
                f["Qpc"].AsInt64,
                f["FileTimeUtc"].AsInt64,
                e.Timestamp.Value.RawValue,
                e.Timestamp.Nanoseconds,
                e.Timestamp.DateTimeOffset.UtcDateTime.ToFileTimeUtc()));
        }

        return markers;
    }

    /// <summary>
    /// Computes the ETL's time zero. For each marker, zero = event raw QPC minus its WPA-relative
    /// time; the median over markers is used. The UTC of zero comes from ETW's own conversion.
    /// </summary>
    public static EtlClockReference Derive(IReadOnlyList<EtlMarker> markers, long qpcFrequency)
    {
        if (markers.Count == 0)
        {
            throw new InvalidOperationException("No WPA-Fusion sync markers found in the ETL; cannot align plugin data with it.");
        }

        var zeroQpc = markers
            .Select(m => m.EventRawTimestamp - (long)Math.Round(m.RelativeNanoseconds * (double)qpcFrequency / 1e9))
            .OrderBy(v => v)
            .ElementAt(markers.Count / 2);
        var zeroFileTime = markers
            .Select(m => m.EventFileTimeUtc - (long)Math.Round(m.RelativeNanoseconds / 100.0))
            .OrderBy(v => v)
            .ElementAt(markers.Count / 2);
        return new EtlClockReference(zeroQpc, zeroFileTime);
    }
}
