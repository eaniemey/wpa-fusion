namespace WpaFusion.Plugin.Sep;

/// <summary>A distinct call stack as raw addresses, leaf first, in one process.</summary>
public sealed record AddressStack(uint Pid, ulong[] Addresses, long TimeNs);

/// <summary>
/// Turns one SEP sample plus its LBR call-stack entries into a leaf-first address stack: the sample IP,
/// then each LBR entry's FROM (the call site in the next caller, newest call first). Identical stacks in
/// the same process share one id, so the per-sample cost is one int.
/// </summary>
public sealed class LbrStackBuilder
{
    private readonly Dictionary<AddressStack, int> _index = new(new StackComparer());
    private readonly List<AddressStack> _stacks = new();

    public IReadOnlyList<AddressStack> Stacks => _stacks;

    /// <summary>Adds a sample and returns its stack id. <paramref name="timeNs"/> is kept from the first sample with this stack.</summary>
    public int Add(uint pid, long timeNs, ulong ip, ReadOnlySpan<ulong> from, int maxDepth = 32)
    {
        var depth = Math.Min(from.Length, Math.Max(0, maxDepth));
        var addresses = new ulong[depth + 1];
        addresses[0] = ip;
        from[..depth].CopyTo(addresses.AsSpan(1));
        var stack = new AddressStack(pid, addresses, timeNs);
        if (!_index.TryGetValue(stack, out var id))
        {
            id = _stacks.Count;
            _stacks.Add(stack);
            _index.Add(stack, id);
        }

        return id;
    }

    private sealed class StackComparer : IEqualityComparer<AddressStack>
    {
        public bool Equals(AddressStack? x, AddressStack? y) =>
            x is not null && y is not null && x.Pid == y.Pid && x.Addresses.AsSpan().SequenceEqual(y.Addresses);

        public int GetHashCode(AddressStack stack)
        {
            var hash = new HashCode();
            hash.Add(stack.Pid);
            foreach (var a in stack.Addresses)
            {
                hash.Add(a);
            }

            return hash.ToHashCode();
        }
    }
}
