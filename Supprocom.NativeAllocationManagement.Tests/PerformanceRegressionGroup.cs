namespace Supprocom.NativeAllocationManagement.Tests;

// The statistical performance gates run alone, after the parallel tests: a test
// running beside a gate takes CPU time from the code it times. Other processes
// on the host still can.
[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires collection-definition classes to be public.
public sealed class PerformanceRegressionGroup
#pragma warning restore CA1515
{
    public const string Name = "Performance regression";
}
