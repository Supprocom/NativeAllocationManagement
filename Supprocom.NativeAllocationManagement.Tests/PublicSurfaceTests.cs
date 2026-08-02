using System.Reflection;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PublicSurfaceTests
{
    [Fact]
    public void PublicSurfaceUsesTheGenerationLifecycleVocabularyOnly()
    {
        Assembly assembly = typeof(NativePool<int>).Assembly;
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
        Assert.NotNull(typeof(NativeRegion).GetMethod("Lease"));
        Assert.NotNull(typeof(NativePool<int>).GetMethod("GetStatistics"));
        Assert.NotNull(typeof(NativeArena).GetMethod("GetStatistics"));
        Assert.NotNull(typeof(NativeRegion).GetMethod("GetStatistics"));
        Assert.DoesNotContain(
            typeof(NativeRegion).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name == "Allocate");
        Assert.Contains(
            typeof(NativeLeaseOperations).GetMethods(),
            method => method.Name == "Access" && method.GetGenericArguments().Length == 5);
        Assert.DoesNotContain(
            typeof(NativeLeaseOperations).GetMethods(),
            method => method.Name == "Access" && method.GetGenericArguments().Length == 1);
        Assert.Contains(
            assembly.GetTypes(),
            type => type.Name == "NativeLeaseQuintupleAction`5");
        Assert.DoesNotContain(
            assembly.GetTypes(),
            type => type.Name == "NativeLeaseUnaryAction`1");
        Assert.DoesNotContain(
            assembly.GetTypes(),
            type => type.Name.Contains("Mesh", StringComparison.OrdinalIgnoreCase));
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

        ConstructorInfo[] poolConstructors =
            typeof(NativePool<int>).GetConstructors();
        Assert.Equal(2, poolConstructors.Length);
        ConstructorInfo? typedPoolConstructor =
            typeof(NativePool<int>).GetConstructor(
                [typeof(int), typeof(NativeMemoryReturn), typeof(bool)]);
        Assert.NotNull(typedPoolConstructor);
        Assert.Equal(
            "preLease",
            typedPoolConstructor.GetParameters()[0].Name);
        ConstructorInfo? combinedPoolConstructor =
            typeof(NativePool<int>).GetConstructor(
                [
                    typeof(int),
                    typeof(nuint),
                    typeof(NativeMemoryReturn),
                    typeof(bool)
                ]);
        Assert.NotNull(combinedPoolConstructor);
        ParameterInfo[] combinedParameters =
            combinedPoolConstructor.GetParameters();
        Assert.Equal("preLease", combinedParameters[0].Name);
        Assert.Equal("preAllocateBytes", combinedParameters[1].Name);
        Assert.Contains(
            combinedParameters,
            parameter => parameter.Name == "returnMemoryOnDispose");
        Assert.Contains(
            combinedParameters,
            parameter => parameter.Name == "doNotLeaseOnDeclaration");
        Assert.DoesNotContain(
            poolConstructors.SelectMany(
                constructor => constructor.GetParameters()),
            parameter => parameter.Name == "initialCapacity");
        ConstructorInfo arenaConstructor = Assert.Single(typeof(NativeArena).GetConstructors());
        Assert.Contains(arenaConstructor.GetParameters(), parameter => parameter.Name == "returnMemoryOnDispose");
        Assert.Contains(arenaConstructor.GetParameters(), parameter => parameter.Name == "doNotLeaseOnDeclaration");
    }
}
