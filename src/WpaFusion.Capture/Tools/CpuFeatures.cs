using System.Runtime.Intrinsics.X86;

namespace WpaFusion.Capture.Tools;

/// <summary>Small CPUID-based helpers used to tailor capture behavior to the processor.</summary>
internal static class CpuFeatures
{
    /// <summary>True on a hybrid CPU (P-cores and E-cores): CPUID leaf 7, EDX bit 15.</summary>
    public static bool IsHybrid() => X86Base.IsSupported && ((X86Base.CpuId(7, 0).Edx >> 15) & 1) != 0;
}
