using System.Reflection;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PublicSurfaceTests
{
    [Fact]
    public void PublicSurfaceUsesTheGenerationLifecycleVocabularyOnly()
    {
        Assembly assembly = typeof(NativePool<int>).Assembly;
        Assert.Null(typeof(NativeRegion).GetMethod("Allocate"));
        Assert.Null(typeof(NativeArena).GetMethod("Lease"));
        Assert.Null(typeof(NativeArena).GetMethod("LeaseScoped"));
        Assert.Null(typeof(NativeRegion).GetMethod("ReleaseLeasesToNativeMemory"));
        Assert.Null(typeof(NativeRegion).GetMethod("ReleaseLeasesToGarbageCollector"));
        Assert.Null(typeof(NativePool<int>).GetMethod("ReturnToNativeMemory"));
        Assert.Null(typeof(NativePool<int>).GetMethod("ReturnToGarbageCollector"));
        Assert.Null(typeof(NativePool<int>).GetMethod("ReleaseLeases"));

        Assert.NotNull(typeof(NativePool<int>).GetMethod("ReturnMemoryToNativeMemory"));
        Assert.NotNull(typeof(NativePool<int>).GetMethod("ReturnMemoryToGarbageCollector"));
        Assert.NotNull(typeof(NativePool<int>).GetMethod("ReleaseLeasesToNativeMemory"));
        Assert.NotNull(typeof(NativePool<int>).GetMethod("ReleaseLeasesToGarbageCollector"));
        Assert.NotNull(typeof(NativeArena).GetMethod("Scratch"));
        Assert.NotNull(typeof(NativeArena).GetMethod("ScratchScoped"));
        Assert.NotNull(typeof(NativeArena).GetMethod("RecycleScoped"));
        Assert.NotNull(typeof(NativePool<int>).GetMethod("RecycleScoped"));
        Assert.NotNull(typeof(NativeRegion).GetMethod("RecycleScoped"));
        Assert.Null(typeof(ArenaLease<int>).GetMethod("Dispose"));
        Assert.Null(typeof(Pooled<int>).GetMethod("TrimRetainedMemory"));
        Assert.Null(typeof(Local<int>).GetMethod("TrimRetainedMemory"));
        Assert.Null(typeof(ArenaLease<int>).GetMethod("TrimRetainedMemory"));

        string[] forbiddenTypes =
        [
            "NativeReturn",
            "NativeSpanAction",
            "NativeSpanFunc",
            "NativeAllocationScope",
            "NativeAllocationMark",
            "ScopedLease"
        ];
        foreach (string forbidden in forbiddenTypes)
        {
            Assert.DoesNotContain(assembly.GetTypes(), type => type.Name == forbidden);
        }

        ConstructorInfo poolConstructor = Assert.Single(typeof(NativePool<int>).GetConstructors());
        Assert.Contains(poolConstructor.GetParameters(), parameter => parameter.Name == "returnMemoryOnDispose");
        Assert.Contains(poolConstructor.GetParameters(), parameter => parameter.Name == "doNotLeaseOnDeclaration");
        ConstructorInfo arenaConstructor = Assert.Single(typeof(NativeArena).GetConstructors());
        Assert.Contains(arenaConstructor.GetParameters(), parameter => parameter.Name == "returnMemoryOnDispose");
        Assert.Contains(arenaConstructor.GetParameters(), parameter => parameter.Name == "doNotLeaseOnDeclaration");
    }
}
