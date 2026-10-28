using System.Globalization;
using WpaFusion.Common;
using WpaFusion.Tbrw;
using Xunit;

namespace WpaFusion.Tests;

/// <summary>LBR call-stack reading against a real `sep -lbr call_stack:usr` capture and sfdump's own dump of it.</summary>
[Collection(Fixtures.Tb7Collection)]
public class TbrwLbrTests
{
    static TbrwLbrTests() => TbrwLibrary.Initialize(SepInstall.Bin64);

    private sealed record Reference(ulong Ip, (ulong From, ulong To)[] Chain);

    private static List<Reference> LoadReference()
    {
        var result = new List<Reference>();
        foreach (var line in File.ReadLines(Fixtures.LbrSamplesHead).Skip(1))
        {
            var cells = line.Split('\t');
            var ip = ulong.Parse(cells[1].Split(':')[1].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var chain = new List<(ulong, ulong)>();
            foreach (var cell in cells.Skip(14).Where(c => c.Length > 0))
            {
                var parts = cell.Split(':');
                var from = ulong.Parse(parts[0].Trim().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var to = ulong.Parse(parts[1].Trim().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (from == 0 && to == 0)
                {
                    break;
                }

                chain.Add((from, to));
            }

            result.Add(new Reference(ip, chain.ToArray()));
        }

        return result;
    }

    [Fact]
    public void Lbr_entries_match_sfdump_newest_first_and_stop_at_the_first_empty_entry()
    {
        var reference = LoadReference();
        Assert.True(reference.Count >= 20);
        var actual = new List<Reference>();
        using var file = TbrwFile.Open(Fixtures.LbrTb7);
        Assert.True(file.HasLbr);
        var count = file.ReadLbrSamples((long index, in PmuSample sample, ReadOnlySpan<ulong> from, ReadOnlySpan<ulong> to) =>
        {
            Assert.Equal(from.Length, to.Length);
            if (index < reference.Count)
            {
                var chain = new (ulong, ulong)[from.Length];
                for (var i = 0; i < chain.Length; i++)
                {
                    chain[i] = (from[i], to[i]);
                }

                actual.Add(new Reference(sample.Ip, chain));
            }
        });

        Assert.Equal(5224, count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.Equal(reference[i].Ip, actual[i].Ip);
            Assert.Equal(reference[i].Chain, actual[i].Chain);
        }
    }

    [Fact]
    public void Lbr_samples_align_with_read_samples_order()
    {
        using var file = TbrwFile.Open(Fixtures.LbrTb7);
        var ips = new List<ulong>();
        file.ReadSamples((in PmuSample s) => ips.Add(s.Ip));
        var lbrIps = new List<ulong>();
        file.ReadLbrSamples((long index, in PmuSample s, ReadOnlySpan<ulong> from, ReadOnlySpan<ulong> to) =>
        {
            Assert.Equal(lbrIps.Count, index);
            lbrIps.Add(s.Ip);
        });

        Assert.Equal(ips, lbrIps);
    }

    [Fact]
    public void File_without_lbr_reports_none_and_delivers_empty_spans()
    {
        using var file = TbrwFile.Open(Fixtures.SmokeTb7);
        Assert.False(file.HasLbr);
        long total = 0;
        var nonEmpty = 0;
        file.ReadLbrSamples((long index, in PmuSample s, ReadOnlySpan<ulong> from, ReadOnlySpan<ulong> to) =>
        {
            total++;
            if (from.Length != 0 || to.Length != 0)
            {
                nonEmpty++;
            }
        });

        Assert.Equal(24446, total);
        Assert.Equal(0, nonEmpty);
    }
}
