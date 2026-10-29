namespace Supprocom.NativeAllocationManagement.Tests;

// Any test that starts a thread may release what ended threads kept: tests
// that observe what an ended thread kept run alone.
[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires collection-definition classes to be public.
public sealed class EndedThreadGroup
#pragma warning restore CA1515
{
    public const string Name = "Ended threads";
}
