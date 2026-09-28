namespace Supprocom.NativeAllocationManagement.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires collection-definition classes to be public.
public sealed class PerformanceRegressionGroup
#pragma warning restore CA1515
{
    public const string Name = "Performance regression";
}
