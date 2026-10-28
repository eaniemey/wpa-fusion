using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WpaFusion.Tbrw.Native;

namespace WpaFusion.Tbrw;

/// <summary>
/// Read-only access to a SEP .tb7 file. Opening reads all metadata (system, processes, threads,
/// events, record layouts); samples are streamed with <see cref="ReadSamples"/>.
/// Not thread-safe. Requires <see cref="TbrwLibrary.Initialize"/>.
/// </summary>
public sealed unsafe class TbrwFile : IDisposable
{
    private const uint SampleStream = 0;
    private readonly SafeTbrwHandle _handle;
    private readonly RecordLayout[] _layouts;
    private readonly IReadOnlyDictionary<ushort, ushort> _eventIndexById;

    private TbrwFile(string path, SafeTbrwHandle handle)
    {
        Path = path;
        _handle = handle;
        SystemInfo = ReadSystem();
        Processes = ReadProcesses();
        Threads = ReadThreads();
        WithStream(() =>
        {
            Events = ReadEvents();
            Descriptors = ReadDescriptors();
        });
        _eventIndexById = CreateEventIndexMap(Events);
        _layouts = BuildLayouts(Descriptors);
    }

    public string Path { get; }

    public TbrwSystemInfo SystemInfo { get; }

    public IReadOnlyList<TbrwProcessInfo> Processes { get; }

    public IReadOnlyList<TbrwThreadInfo> Threads { get; }

    public IReadOnlyList<TbrwEventInfo> Events { get; private set; } = Array.Empty<TbrwEventInfo>();

    public IReadOnlyList<TbrwDescriptor> Descriptors { get; private set; } = Array.Empty<TbrwDescriptor>();

    /// <summary>
    /// Opens a bound .tb7 for reading. Throws <see cref="TbrwUnboundFileException"/> if the file
    /// is not bound; WPA-Fusion never binds files it was asked to read.
    /// </summary>
    public static TbrwFile Open(string path)
    {
        var full = global::System.IO.Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($".tb7 file not found: '{full}'.", full);
        }

        var handle = SafeTbrwHandle.Open(full, TbrwAccess.FileRead);
        try
        {
            if (!IsBound(handle))
            {
                throw new TbrwUnboundFileException(full);
            }

            return new TbrwFile(full, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Returns true if the file's sample stream is bound to modules.</summary>
    public static bool IsBound(string path)
    {
        using var handle = SafeTbrwHandle.Open(global::System.IO.Path.GetFullPath(path), TbrwAccess.FileRead);
        return IsBound(handle);
    }

    /// <summary>
    /// Binds an unbound .tb7 in place. Only the capture tool calls this, right after collection.
    /// Returns false if the file was already bound.
    /// </summary>
    public static bool BindInPlace(string path)
    {
        var full = global::System.IO.Path.GetFullPath(path);
        if (IsBound(full))
        {
            return false;
        }

        using var handle = SafeTbrwHandle.Open(full, TbrwAccess.FileRead | TbrwAccess.FileWrite);
        TbrwException.Check(TbrwNative.TBRW_dobind(handle.DangerousGetHandle(), SampleStream), "TBRW_dobind");
        return true;
    }

    /// <summary>
    /// Streams every sample in file order (not time order; samples from different CPUs interleave).
    /// Returns the number of samples delivered.
    /// </summary>
    public long ReadSamples(PmuSampleHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Read(new SampleState(_layouts, _eventIndexById, handler));
    }

    /// <summary>True when the sample layouts carry LBR fields (the run used -lbr or adaptive PEBS LBR).</summary>
    public bool HasLbr => _layouts.Any(l => l.TosOffset >= 0);

    /// <summary>
    /// Streams every sample in file order (the same order as <see cref="ReadSamples"/>, so indices align) with
    /// its LBR entries, newest branch first. Samples without LBR data get empty spans. The spans are only
    /// valid during the call.
    /// </summary>
    public long ReadLbrSamples(LbrSampleHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Read(new SampleState(_layouts, _eventIndexById, null, handler));
    }

    private long Read(SampleState state)
    {
        var gc = GCHandle.Alloc(state);
        try
        {
            WithStream(() =>
            {
                WithStreamSection(TbrwStreamSection.Data, () =>
                {
                    delegate* unmanaged<void*, ulong, uint, void*, ulong*, uint> cb = &OnData;
                    var code = TbrwNative.TBRW_enumerate_data(Raw, SampleStream, (void**)&cb, (void*)GCHandle.ToIntPtr(gc), 0);
                    state.ThrowIfFailed();
                    TbrwException.Check(code, "TBRW_enumerate_data");
                });
            });

            if (state.CarryLength != 0)
            {
                throw new TbrwException($"Sample stream ended inside a record ({state.CarryLength} trailing bytes).");
            }

            return state.Count;
        }
        finally
        {
            gc.Free();
        }
    }

    public void Dispose() => _handle.Dispose();

    private IntPtr Raw => _handle.DangerousGetHandle();

    private static bool IsBound(SafeTbrwHandle handle)
    {
        TbrwException.Check(TbrwNative.TBRW_is_bound(handle.DangerousGetHandle(), SampleStream, out var bound), "TBRW_is_bound");
        return bound != 0;
    }

    private void WithSection(TbrwSection section, Action body)
    {
        TbrwException.Check(TbrwNative.TBRW_reading_section(Raw, section), $"TBRW_reading_section({section})");
        try
        {
            body();
        }
        finally
        {
            TbrwNative.TBRW_done_section(Raw, section);
        }
    }

    private void WithStream(Action body)
    {
        TbrwException.Check(TbrwNative.TBRW_reading_stream(Raw, SampleStream), "TBRW_reading_stream");
        try
        {
            body();
        }
        finally
        {
            TbrwNative.TBRW_done_stream(Raw, SampleStream);
        }
    }

    private void WithStreamSection(TbrwStreamSection section, Action body)
    {
        TbrwException.Check(TbrwNative.TBRW_reading_stream_section(Raw, SampleStream, section), $"TBRW_reading_stream_section({section})");
        try
        {
            body();
        }
        finally
        {
            TbrwNative.TBRW_done_stream_section(Raw, SampleStream, section);
        }
    }

    private string IdToString(TbrwStringOrId id)
    {
        if (id.UniqueId == 0)
        {
            return string.Empty;
        }

        uint needed = 0;
        TbrwNative.TBRW_convert_uniqueid_to_string(Raw, 0, &id, null, &needed);
        if (needed == 0)
        {
            return string.Empty;
        }

        // needed is in bytes; the buffer holds UTF-16 code units.
        var chars = new char[(needed + 1) / 2 + 1];
        fixed (char* buffer = chars)
        {
            uint ignored = 0;
            TbrwException.Check(
                TbrwNative.TBRW_convert_uniqueid_to_string(Raw, (uint)(chars.Length * sizeof(char)), &id, buffer, &ignored),
                "TBRW_convert_uniqueid_to_string");
        }

        var end = Array.IndexOf(chars, '\0');
        return new string(chars, 0, end < 0 ? chars.Length : end);
    }

    private TbrwSystemInfo ReadSystem()
    {
        TbrwSystemInfo? info = null;
        WithSection(TbrwSection.Hardware, () =>
        {
            uint needed = 0;
            TbrwNative.TBRW_get_system(Raw, 0, null, &needed);
            if (needed < (uint)sizeof(TbrwSystem))
            {
                throw new TbrwException($"TBRW_get_system reported an implausible buffer size ({needed} bytes).");
            }

            var buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                var system = (TbrwSystem*)p;
                system->Size = (uint)sizeof(TbrwSystem);
                uint used = 0;
                TbrwException.Check(TbrwNative.TBRW_get_system(Raw, needed, system, &used), "TBRW_get_system");
                info = ParseSystem(system);
            }
        });

        return info!;
    }

    private static TbrwSystemInfo ParseSystem(TbrwSystem* system)
    {
        var cpus = new List<TbrwCpuInfo>();
        uint signature = 0;
        for (var n = 0; n < system->NumNodes; n++)
        {
            var node = &system->NodeArray[n];
            for (var c = 0; c < node->NumAvailable; c++)
            {
                var cpu = &node->CpuArray[c];
                cpus.Add(new TbrwCpuInfo(cpu->CpuNumber, cpu->TscOffset, cpu->SpeedMhz, cpu->PackageNum, cpu->CoreNum, cpu->HardwareThreadNum, cpu->CoreType));
                if (signature == 0 && cpu->NumCpuArch > 0 && cpu->CpuArchArray != null)
                {
                    var arch = &cpu->CpuArchArray[0];
                    for (var i = 0; i < arch->NumCpuId; i++)
                    {
                        if (arch->CpuIdIa32Array[i].EaxInput == 1)
                        {
                            signature = arch->CpuIdIa32Array[i].Eax;
                            break;
                        }
                    }
                }
            }
        }

        var (family, model) = DecodeSignature(signature);
        return new TbrwSystemInfo(cpus, family, model, signature);
    }

    /// <summary>Decodes CPUID(1).EAX into display family and model per the Intel SDM.</summary>
    public static (int Family, int Model) DecodeSignature(uint eax)
    {
        var family = (int)((eax >> 8) & 0xF);
        var model = (int)((eax >> 4) & 0xF);
        if (family == 0xF)
        {
            family += (int)((eax >> 20) & 0xFF);
        }

        if (family == 0x6 || family >= 0xF)
        {
            model += (int)(((eax >> 16) & 0xF) << 4);
        }

        return (family, model);
    }

    private IReadOnlyList<TbrwProcessInfo> ReadProcesses()
    {
        var raw = new List<TbrwPid>();
        WithSection(TbrwSection.Process, () => Enumerate<TbrwPid>(raw, (cb, user) => TbrwNative.TBRW_enumerate_processes(Raw, cb, user, 0), "TBRW_enumerate_processes"));

        var byPid = new Dictionary<uint, TbrwProcessInfo>();
        foreach (var p in raw)
        {
            var name = IdToString(p.Name);
            if (!byPid.TryGetValue(p.PidId, out var existing) || (existing.Name.Length == 0 && name.Length > 0))
            {
                byPid[p.PidId] = new TbrwProcessInfo(p.PidId, name, IdToString(p.Path));
            }
        }

        return byPid.Values.OrderBy(p => p.Pid).ToList();
    }

    private IReadOnlyList<TbrwThreadInfo> ReadThreads()
    {
        var raw = new List<TbrwTid>();
        WithSection(TbrwSection.Thread, () => Enumerate<TbrwTid>(raw, (cb, user) => TbrwNative.TBRW_enumerate_threads(Raw, cb, user, 0), "TBRW_enumerate_threads"));

        var byTid = new Dictionary<(uint, uint), TbrwThreadInfo>();
        foreach (var t in raw)
        {
            var key = (t.AssociatedPid, t.TidId);
            var name = IdToString(t.Name);
            if (!byTid.TryGetValue(key, out var existing) || (existing.Name.Length == 0 && name.Length > 0))
            {
                byTid[key] = new TbrwThreadInfo(t.AssociatedPid, t.TidId, name);
            }
        }

        return byTid.Values.OrderBy(t => t.Pid).ThenBy(t => t.Tid).ToList();
    }

    private IReadOnlyList<TbrwEventInfo> ReadEvents()
    {
        var raw = new List<TbrwEvent>();
        WithStreamSection(TbrwStreamSection.EventDescription, () =>
            Enumerate<TbrwEvent>(raw, (cb, user) => TbrwNative.TBRW_enumerate_events(Raw, SampleStream, cb, user, 0), "TBRW_enumerate_events"));

        return raw.Select((e, i) => new TbrwEventInfo(i, IdToString(e.Name), e.SampleAfterValue, e.GroupId)
        {
            EventId = e.EventId,
        }).ToList();
    }

    private static IReadOnlyDictionary<ushort, ushort> CreateEventIndexMap(IReadOnlyList<TbrwEventInfo> events)
    {
        var indexById = new Dictionary<ushort, ushort>(events.Count);
        foreach (var e in events)
        {
            if (!indexById.TryAdd(e.EventId, checked((ushort)e.Index)))
            {
                throw new TbrwException($"SEP event ID {e.EventId} is duplicated in the event catalog.");
            }
        }

        return indexById;
    }

    private IReadOnlyList<TbrwDescriptor> ReadDescriptors()
    {
        var raw = new List<TbrwSampRecDescEntry>();
        WithStreamSection(TbrwStreamSection.DataDescription, () =>
            Enumerate<TbrwSampRecDescEntry>(raw, (cb, user) => TbrwNative.TBRW_enumerate_data_descriptor_entries(Raw, SampleStream, cb, user), "TBRW_enumerate_data_descriptor_entries"));

        var result = new List<TbrwDescriptor>(raw.Count);
        var group = -1;
        foreach (var d in raw)
        {
            if (d.Offset == 0)
            {
                group++;
            }

            result.Add(new TbrwDescriptor(group, d.Type, d.Subtype, d.Offset, d.DataSize, IdToString(d.Name)));
        }

        return result;
    }

    // ---- Array enumeration (processes, threads, events, descriptors) -------------------------

    private delegate uint EnumerateCall(void** callback, void* userPtr);

    private sealed class ArrayState
    {
        public ArrayState(Action<IntPtr, uint, uint> sink) => Sink = sink;

        public Action<IntPtr, uint, uint> Sink { get; }

        public Exception? Error { get; set; }
    }

    /// <summary>
    /// Runs a TBRW enumerate call whose callback receives (first entry, entry size or total size,
    /// count, user). Entries are copied out as <typeparamref name="T"/> using each entry's own
    /// declared size as the stride when it is at least sizeof(T).
    /// </summary>
    private static void Enumerate<T>(List<T> sink, EnumerateCall call, string operation)
        where T : unmanaged
    {
        var state = new ArrayState((first, dataSize, count) =>
        {
            if (first == IntPtr.Zero || count == 0)
            {
                return;
            }

            // Every TBRW entry struct starts with its own size field (uint).
            var stride = *(uint*)first;
            if (stride < (uint)sizeof(T))
            {
                stride = (uint)sizeof(T);
            }

            for (uint i = 0; i < count; i++)
            {
                sink.Add(Unsafe.ReadUnaligned<T>((byte*)first + (i * stride)));
            }
        });

        var gc = GCHandle.Alloc(state);
        try
        {
            delegate* unmanaged<IntPtr, uint, uint, void*, uint> cb = &OnArray;
            var code = call((void**)&cb, (void*)GCHandle.ToIntPtr(gc));
            if (state.Error is not null)
            {
                throw new TbrwException($"{operation} callback failed.", state.Error);
            }

            TbrwException.Check(code, operation);
        }
        finally
        {
            gc.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static uint OnArray(IntPtr first, uint dataSize, uint count, void* user)
    {
        var state = (ArrayState)GCHandle.FromIntPtr((IntPtr)user).Target!;
        if (state.Error is not null)
        {
            return 0;
        }

        try
        {
            state.Sink(first, dataSize, count);
            return 1;
        }
        catch (Exception ex)
        {
            state.Error = ex;
            return 0;
        }
    }

    // ---- Sample decoding --------------------------------------------------------------------

    /// <summary>Byte offsets of the fields WPA-Fusion reads, for one descriptor group (event layout).</summary>
    private readonly struct RecordLayout
    {
        public RecordLayout(int size, int pcOffset, int tscOffset, int tosOffset, int[] fromOffsets, int[] toOffsets)
        {
            Size = size;
            PcOffset = pcOffset;
            TscOffset = tscOffset;
            TosOffset = tosOffset;
            FromOffsets = fromOffsets;
            ToOffsets = toOffsets;
        }

        public int Size { get; }

        public int PcOffset { get; }

        public int TscOffset { get; }

        /// <summary>Offset of the LBR top-of-stack field, or -1 when the layout has no LBR data.</summary>
        public int TosOffset { get; }

        /// <summary>Offsets of LBR_FROM_n by depth n (empty when there is no LBR data).</summary>
        public int[] FromOffsets { get; }

        /// <summary>Offsets of LBR_TO_n by depth n, parallel to <see cref="FromOffsets"/>.</summary>
        public int[] ToOffsets { get; }
    }

    // SST_LBR_* subtype bases (tbrw_types.h): FROM/TO 0-15 are contiguous, then 16-31 in a second run.
    private const int LbrTos = 8;
    private const int LbrFrom0 = 43;
    private const int LbrTo0 = 59;
    private const int LbrFrom16 = 157;
    private const int LbrTo16 = 173;

    private static int LbrDepth(int subtype, int low, int high) =>
        subtype >= low && subtype < low + 16 ? subtype - low :
        subtype >= high && subtype < high + 16 ? subtype - high + 16 : -1;

    private static RecordLayout[] BuildLayouts(IReadOnlyList<TbrwDescriptor> descriptors)
    {
        var groups = descriptors.GroupBy(d => d.EventGroup).OrderBy(g => g.Key).ToList();
        var layouts = new RecordLayout[groups.Count];
        foreach (var g in groups)
        {
            var size = g.Max(d => (int)(d.Offset + d.DataSize));
            var pc = g.FirstOrDefault(d => d.Type == (ushort)TbrwDescType.LegacySampleRecord)
                ?? throw new TbrwException($"Record layout {g.Key} has no legacy sample record.");
            var tsc = g.FirstOrDefault(d => d.Type == (ushort)TbrwDescType.TimeStamp)
                ?? throw new TbrwException($"Record layout {g.Key} has no time stamp.");
            var id = g.FirstOrDefault(d => d.Type == (ushort)TbrwDescType.DescriptorId);
            if (id is null || id.Offset != 0 || id.DataSize != 8)
            {
                throw new TbrwException($"Record layout {g.Key} does not start with an 8-byte descriptor id.");
            }

            if (pc.DataSize < (uint)sizeof(TbrwSampleRecordPc) || tsc.DataSize < 8)
            {
                throw new TbrwException($"Record layout {g.Key} has undersized fields.");
            }

            var tos = -1;
            var from = new int[32];
            var to = new int[32];
            Array.Fill(from, -1);
            Array.Fill(to, -1);
            foreach (var d in g.Where(d => d.Type == (ushort)TbrwDescType.BranchTrace && d.DataSize >= 8))
            {
                if (d.Subtype == LbrTos)
                {
                    tos = (int)d.Offset;
                }
                else if (LbrDepth(d.Subtype, LbrFrom0, LbrFrom16) is var f and >= 0)
                {
                    from[f] = (int)d.Offset;
                }
                else if (LbrDepth(d.Subtype, LbrTo0, LbrTo16) is var t and >= 0)
                {
                    to[t] = (int)d.Offset;
                }
            }

            // Depth is the contiguous run of FROM/TO pairs starting at depth 0.
            var depth = 0;
            while (depth < 32 && from[depth] >= 0 && to[depth] >= 0)
            {
                depth++;
            }

            layouts[g.Key] = new RecordLayout(size, (int)pc.Offset, (int)tsc.Offset,
                depth > 0 ? tos : -1, from[..depth], to[..depth]);
        }

        return layouts;
    }

    private sealed class SampleState
    {
        private byte[] _carry = new byte[4096];

        private readonly ulong[] _from = new ulong[32];
        private readonly ulong[] _to = new ulong[32];

        public SampleState(RecordLayout[] layouts, IReadOnlyDictionary<ushort, ushort> eventIndexById,
            PmuSampleHandler? handler, LbrSampleHandler? lbrHandler = null)
        {
            Layouts = layouts;
            EventIndexById = eventIndexById;
            Handler = handler;
            LbrHandler = lbrHandler;
        }

        public RecordLayout[] Layouts { get; }

        public IReadOnlyDictionary<ushort, ushort> EventIndexById { get; }

        public PmuSampleHandler? Handler { get; }

        public LbrSampleHandler? LbrHandler { get; }

        public long Count { get; set; }

        public int CarryLength { get; private set; }

        public Exception? Error { get; set; }

        public void ThrowIfFailed()
        {
            if (Error is not null)
            {
                throw new TbrwException("Sample decoding failed.", Error);
            }
        }

        /// <summary>Consumes one chunk. Records split across chunks are reassembled via a carry buffer.</summary>
        public void Consume(ReadOnlySpan<byte> chunk)
        {
            // Complete a record started in the previous chunk. RecordSize returns 8 until the
            // descriptor id is complete, so this may take two rounds.
            while (CarryLength > 0)
            {
                var need = RecordSize(_carry.AsSpan(0, CarryLength)) - CarryLength;
                if (need > chunk.Length)
                {
                    Append(chunk);
                    return;
                }

                Append(chunk[..need]);
                chunk = chunk[need..];
                if (CarryLength == RecordSize(_carry.AsSpan(0, CarryLength)))
                {
                    Decode(_carry.AsSpan(0, CarryLength));
                    CarryLength = 0;
                }
            }

            while (chunk.Length >= 8)
            {
                var size = RecordSize(chunk);
                if (size > chunk.Length)
                {
                    break;
                }

                Decode(chunk[..size]);
                chunk = chunk[size..];
            }

            if (chunk.Length > 0)
            {
                Append(chunk);
            }
        }

        private int RecordSize(ReadOnlySpan<byte> record)
        {
            if (record.Length < 8)
            {
                // Size unknown until the 8-byte descriptor id is complete; ask for the rest of it.
                return 8;
            }

            var id = BinaryPrimitives.ReadUInt64LittleEndian(record);
            if (id >= (ulong)Layouts.Length)
            {
                throw new TbrwException($"Sample record references unknown layout {id} (file has {Layouts.Length}).");
            }

            return Layouts[id].Size;
        }

        private void Decode(ReadOnlySpan<byte> record)
        {
            var layout = Layouts[BinaryPrimitives.ReadUInt64LittleEndian(record)];
            var pc = record.Slice(layout.PcOffset);
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(pc.Slice(TbrwSampleRecordPc.BitFieldsOffset));
            var is64 = ((bits >> 29) & 1) != 0;
            var ip = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(pc) : BinaryPrimitives.ReadUInt32LittleEndian(pc);
            var cpu = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(pc.Slice(TbrwSampleRecordPc.CpuOffset)) & 0x0FFF);
            var tid = BinaryPrimitives.ReadUInt32LittleEndian(pc.Slice(TbrwSampleRecordPc.TidOffset));
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(pc.Slice(TbrwSampleRecordPc.PidOffset));
            var eventId = (ushort)((bits >> 20) & 0xFF);
            if (!EventIndexById.TryGetValue(eventId, out var eventIndex))
            {
                throw new TbrwException($"Sample references unknown SEP event ID {eventId}.");
            }

            var tsc = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(layout.TscOffset));

            var sample = new PmuSample(tsc, ip, pid, tid, cpu, eventIndex);
            Handler?.Invoke(in sample);
            if (LbrHandler is not null)
            {
                // The record holds the LBR ring in hardware order. TOS indexes the newest entry and older
                // entries sit at TOS-1, TOS-2, ... (wrapping). Valid depth ends at the first all-zero entry.
                var depth = 0;
                var ring = layout.FromOffsets.Length;
                if (layout.TosOffset >= 0)
                {
                    var tos = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(layout.TosOffset)) % (ulong)ring);
                    for (; depth < ring; depth++)
                    {
                        var slot = (int)(((tos - depth) % ring + ring) % ring);
                        var from = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(layout.FromOffsets[slot]));
                        var to = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(layout.ToOffsets[slot]));
                        if (from == 0 && to == 0)
                        {
                            break;
                        }

                        _from[depth] = from;
                        _to[depth] = to;
                    }
                }

                LbrHandler(Count, in sample, _from.AsSpan(0, depth), _to.AsSpan(0, depth));
            }

            Count++;
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            if (CarryLength + bytes.Length > _carry.Length)
            {
                Array.Resize(ref _carry, Math.Max(_carry.Length * 2, CarryLength + bytes.Length));
            }

            bytes.CopyTo(_carry.AsSpan(CarryLength));
            CarryLength += bytes.Length;
        }
    }

    [UnmanagedCallersOnly]
    private static uint OnData(void* data, ulong dataSize, uint numEntries, void* user, ulong* adjustment)
    {
        var state = (SampleState)GCHandle.FromIntPtr((IntPtr)user).Target!;
        if (adjustment != null)
        {
            // Partial records are carried over by WPA-Fusion, so TBRW must not re-deliver bytes.
            *adjustment = 0;
        }

        if (state.Error is not null)
        {
            return 0;
        }

        try
        {
            if (data != null && dataSize > 0)
            {
                state.Consume(new ReadOnlySpan<byte>(data, checked((int)dataSize)));
            }

            return 1;
        }
        catch (Exception ex)
        {
            state.Error = ex;
            return 0;
        }
    }
}

/// <summary>The .tb7 has not been bound. WPA-Fusion plugins do not modify input files.</summary>
public sealed class TbrwUnboundFileException : Exception
{
    public TbrwUnboundFileException(string path)
        : base($"'{path}' is not bound. Bind it after collection (wpafusion-capture does this automatically, or run SEP's sfdump on a copy).")
    {
        FilePath = path;
    }

    public string FilePath { get; }
}
