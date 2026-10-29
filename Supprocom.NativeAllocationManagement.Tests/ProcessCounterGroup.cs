namespace Supprocom.NativeAllocationManagement.Tests;

// Tests that compare process-wide native-memory counters before and after
// their work run alone: any test running beside them allocates and frees too.
[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires collection-definition classes to be public.
public sealed class ProcessCounterGroup
#pragma warning restore CA1515
{
    public const string Name = "Process-wide counters";
}
