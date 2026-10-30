namespace WpaFusion.Common;

public sealed record CrossRunAlignment(
    string SourceRunId,
    string ReferenceRunId,
    long OffsetNs,
    long ReferenceZeroFileTimeUtc,
    long SourceWorkloadDurationNs,
    long ReferenceWorkloadDurationNs)
{
    public string Method => "workload-start-offset";
    public double DurationDifferencePercent =>
        100.0 * (SourceWorkloadDurationNs - (double)ReferenceWorkloadDurationNs) / ReferenceWorkloadDurationNs;

    public static CrossRunAlignment Create(SessionManifest reference, SessionManifest source)
    {
        if (reference.Alignment is not null || source.Alignment is not null || reference.Etl is null)
        {
            throw new InvalidDataException("Fusion requires native, unaligned runs and an ETL clock reference for the SEP reference run.");
        }

        if (string.IsNullOrWhiteSpace(reference.Machine) ||
            !string.Equals(reference.Machine, source.Machine, StringComparison.OrdinalIgnoreCase) ||
            reference.WorkloadCommand.Count == 0 || !reference.WorkloadCommand.SequenceEqual(source.WorkloadCommand))
        {
            throw new InvalidDataException("Fusion requires the same machine and identical workload command templates.");
        }

        static (long Start, long Duration) Bounds(SessionManifest manifest)
        {
            if (manifest.WorkloadExitCode != 0)
            {
                throw new InvalidDataException("Fusion requires successful, completed workload runs.");
            }

            var starts = manifest.Markers.Where(marker => marker.Name == "workload-start").ToArray();
            var ends = manifest.Markers.Where(marker => marker.Name == "workload-end").ToArray();
            if (starts.Length != 1 || ends.Length != 1 || ends[0].Qpc <= starts[0].Qpc)
            {
                throw new InvalidDataException("Fusion requires one advancing workload-start/workload-end marker pair.");
            }

            var timeline = Timeline.FromManifest(manifest);
            var start = timeline.QpcToNanoseconds(starts[0].Qpc);
            var duration = checked(timeline.QpcToNanoseconds(ends[0].Qpc) - start);
            if (duration <= 0)
            {
                throw new InvalidDataException("Workload duration must be positive.");
            }

            return (start, duration);
        }

        var target = Bounds(reference);
        var origin = Bounds(source);
        return new CrossRunAlignment(source.RunId, reference.RunId, checked(target.Start - origin.Start),
            reference.Etl.ZeroFileTimeUtc, origin.Duration, target.Duration);
    }
}