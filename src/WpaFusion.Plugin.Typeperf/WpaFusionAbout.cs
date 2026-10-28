using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Typeperf;

/// <summary>Shared launcher metadata for the Typeperf and session processing sources.</summary>
internal static class WpaFusionAbout
{
    public static ProcessingSourceInfo Info { get; } = new()
    {
        Owners = Array.Empty<ContactInfo>(),
        CopyrightNotice = "Internal tool. Not for redistribution.",
        AdditionalInformation = new[]
        {
            "WPA-Fusion adds Typeperf counters and Intel SEP/TMA data to WPA, aligned with ETW traces.",
            "Capture with wpafusion-capture; open trace.etl, typeperf.csv and sep.tb7 from one run folder in a single WPA session.",
        },
    };
}
