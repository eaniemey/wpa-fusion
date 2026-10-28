namespace WpaFusion.Tbrw;

/// <summary>One PMU sample. Timestamps are raw TSC values as recorded on <see cref="Cpu"/>.</summary>
public readonly struct PmuSample
{
    public PmuSample(ulong tsc, ulong ip, uint pid, uint tid, ushort cpu, ushort eventIndex)
    {
        Tsc = tsc;
        Ip = ip;
        Pid = pid;
        Tid = tid;
        Cpu = cpu;
        EventIndex = eventIndex;
    }

    /// <summary>Raw TSC of the sample on its CPU. Subtract the CPU's TSC offset to get CPU 0 time.</summary>
    public ulong Tsc { get; }

    /// <summary>Instruction pointer.</summary>
    public ulong Ip { get; }

    public uint Pid { get; }

    public uint Tid { get; }

    /// <summary>Logical CPU number.</summary>
    public ushort Cpu { get; }

    /// <summary>Index into <see cref="TbrwFile.Events"/>.</summary>
    public ushort EventIndex { get; }
}

/// <summary>Receives samples during <see cref="TbrwFile.ReadSamples"/>.</summary>
public delegate void PmuSampleHandler(in PmuSample sample);

/// <summary>Receives a sample (file-order <paramref name="index"/>) with its LBR branch entries, newest first.</summary>
public delegate void LbrSampleHandler(long index, in PmuSample sample, ReadOnlySpan<ulong> from, ReadOnlySpan<ulong> to);

/// <summary>A sampled event: its dense catalog index, native event id, name, and sample-after value.</summary>
public sealed record TbrwEventInfo(int Index, string Name, ulong SampleAfterValue, ushort GroupId)
{
    /// <summary>Event id stored in SEP sample records; it may be sparse relative to <see cref="Index"/>.</summary>
    public ushort EventId { get; init; }
}

/// <summary>A logical CPU from the hardware section.</summary>
public sealed record TbrwCpuInfo(
    uint Number,
    long TscOffset,
    uint SpeedMhz,
    ushort Package,
    ushort Core,
    ushort HardwareThread,
    ushort CoreType);

/// <summary>System description from the hardware section.</summary>
public sealed record TbrwSystemInfo(IReadOnlyList<TbrwCpuInfo> Cpus, int CpuFamily, int CpuModel, uint CpuIdSignature)
{
    /// <summary>TSC offset (TSC on CPU N minus TSC on CPU 0) by CPU number; 0 if unknown.</summary>
    public long TscOffsetOf(int cpu)
    {
        foreach (var c in Cpus)
        {
            if (c.Number == cpu)
            {
                return c.TscOffset;
            }
        }

        return 0;
    }
}

/// <summary>A process seen during collection.</summary>
public sealed record TbrwProcessInfo(uint Pid, string Name, string Path);

/// <summary>A thread seen during collection.</summary>
public sealed record TbrwThreadInfo(uint Pid, uint Tid, string Name);

/// <summary>One field of a sample record layout.</summary>
public sealed record TbrwDescriptor(int EventGroup, ushort Type, ushort Subtype, uint Offset, uint DataSize, string Name);
