using Microsoft.Performance.SDK.Processing;

namespace WpaFusion.Plugin.Sep;

internal sealed class SourceTableProvider : IProcessingSourceTableProvider
{
    private readonly TableDescriptor[] _tables;

    public SourceTableProvider(params TableDescriptor[] tables) => _tables = tables;

    public IEnumerable<TableDescriptor> Discover(ITableConfigurationsSerializer tableConfigSerializer) => _tables;
}