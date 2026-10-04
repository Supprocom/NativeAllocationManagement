using System.Reflection;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class PublicSurfaceTests
{
    [Fact]
    public void PublicSurfaceSeparatesFastAndSynchronizedOwners()
    {
        Assembly assembly = typeof(NativePool<int>).Assembly;
        Assert.Null(typeof(NativeArena).GetMethod("Lease"));
        Assert.Null(typeof(NativeArena).GetMethod("LeaseScoped"));
        Assert.Null(typeof(NativeRegion).GetMethod("ReleaseLeasesToNativeMemory"));
        Assert.Null(typeof(NativeRegion).GetMethod("ReleaseLeasesToGarbageCollector"));
        Assert.Null(typeof(NativePool<int>).GetMethod("ReturnToNativeMemory"));
        Assert.Null(typeof(NativePool<int>).GetMethod("ReturnToGarbageCollector"));
        Assert.Null(typeof(NativePool<int>).GetMethod("ReleaseLeases"));

        Assert.Null(typeof(NativePool<int>).GetMethod("ReturnMemoryToNativeMemory"));
        Assert.Null(typeof(NativePool<int>).GetMethod("ReturnMemoryToGarbageCollector"));
        Assert.Null(typeof(NativePool<int>).GetMethod("ReleaseLeasesToNativeMemory"));
        Assert.Null(typeof(NativePool<int>).GetMethod("ReleaseLeasesToGarbageCollector"));
        Assert.NotNull(typeof(NativeConcurrentPool<int>).GetMethod("ReturnMemoryToNativeMemory"));
        Assert.NotNull(typeof(NativeConcurrentPool<int>).GetMethod("ReturnMemoryToGarbageCollector"));
        Assert.NotNull(typeof(NativeConcurrentPool<int>).GetMethod("ReleaseLeasesToNativeMemory"));
        Assert.NotNull(typeof(NativeConcurrentPool<int>).GetMethod("ReleaseLeasesToGarbageCollector"));
        Assert.NotNull(typeof(NativeArena).GetMethod("Scratch"));
        Assert.NotNull(typeof(NativeArena).GetMethod("ScratchScoped"));
        Assert.Null(typeof(NativeConcurrentArena).GetMethod(
            "ScratchTransferable"));
        Assert.Null(typeof(ConcurrentPooled<int>).GetProperty("Item"));
        Assert.Contains(
            typeof(ConcurrentPooled<int>).GetMethods(),
            method => string.Equals(method.Name, "Process", StringComparison.Ordinal) && method.GetGenericArguments().Length == 2
                && method.GetParameters().Length == 3);
        Assert.Contains(
            assembly.GetTypes(),
            type => string.Equals(type.Name, "NativeLeaseStateFunc`3", StringComparison.Ordinal));
        Assert.Null(typeof(ConcurrentArenaLease<int>).GetProperty("Item"));
        Assert.NotNull(typeof(NativeRegion).GetMethod("Lease"));
        Assert.NotNull(typeof(NativePool<int>).GetMethod("GetStatistics"));
        Assert.NotNull(typeof(NativeArena).GetMethod("GetStatistics"));
        Assert.NotNull(typeof(NativeRegion).GetMethod("GetStatistics"));
        Assert.DoesNotContain(
            typeof(NativeRegion).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => string.Equals(method.Name, "Allocate", StringComparison.Ordinal));
        Assert.Contains(
            typeof(NativeLeaseOperations).GetMethods(),
            method => string.Equals(method.Name, "Access", StringComparison.Ordinal) && method.GetGenericArguments().Length == 5);
        Assert.DoesNotContain(
            typeof(NativeLeaseOperations).GetMethods(),
            method => string.Equals(method.Name, "Access", StringComparison.Ordinal) && method.GetGenericArguments().Length == 1);
        Assert.Contains(
            assembly.GetTypes(),
            type => string.Equals(type.Name, "NativeLeaseQuintupleAction`5", StringComparison.Ordinal));
        Assert.DoesNotContain(
            assembly.GetTypes(),
            type => string.Equals(type.Name, "NativeLeaseUnaryAction`1", StringComparison.Ordinal));
        Assert.DoesNotContain(
            assembly.GetTypes(),
            type => type.Name.Contains("Mesh", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(typeof(NativeArena).GetMethod("RecycleScoped"));
        Assert.Null(typeof(NativePool<int>).GetMethod("RecycleScoped"));
        Assert.NotNull(typeof(NativeConcurrentPool<int>).GetMethod("RecycleScoped"));
        Assert.Null(typeof(NativeRegion).GetMethod("RecycleScoped"));
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
            Assert.DoesNotContain(assembly.GetTypes(), type => string.Equals(type.Name, forbidden, StringComparison.Ordinal));
        }

        ConstructorInfo[] poolConstructors =
            typeof(NativePool<int>).GetConstructors();
        Assert.Equal(2, poolConstructors.Length);
        ConstructorInfo? typedPoolConstructor =
            typeof(NativePool<int>).GetConstructor(
                [typeof(int), typeof(NativeMemoryReturn)]);
        Assert.NotNull(typedPoolConstructor);
        Assert.Equal(
            "preLease",
            typedPoolConstructor.GetParameters()[0].Name);
        ConstructorInfo? combinedPoolConstructor =
            typeof(NativePool<int>).GetConstructor(
                [
                    typeof(int),
                    typeof(nuint),
                    typeof(NativeMemoryReturn)
                ]);
        Assert.NotNull(combinedPoolConstructor);
        ParameterInfo[] combinedParameters =
            combinedPoolConstructor.GetParameters();
        Assert.Equal("preLease", combinedParameters[0].Name);
        Assert.Equal("preAllocateBytes", combinedParameters[1].Name);
        Assert.Contains(
            combinedParameters,
            parameter => string.Equals(parameter.Name, "returnMemoryOnDispose", StringComparison.Ordinal));
        Assert.DoesNotContain(
            combinedParameters,
            parameter => string.Equals(parameter.Name, "doNotLeaseOnDeclaration", StringComparison.Ordinal));
        Assert.DoesNotContain(
            poolConstructors.SelectMany(
                constructor => constructor.GetParameters()),
            parameter => string.Equals(parameter.Name, "initialCapacity", StringComparison.Ordinal));
        ConstructorInfo[] concurrentPoolConstructors =
            typeof(NativeConcurrentPool<int>).GetConstructors();
        Assert.Equal(2, concurrentPoolConstructors.Length);
        Assert.All(
            concurrentPoolConstructors,
            constructor => Assert.Contains(
                constructor.GetParameters(),
                parameter => string.Equals(parameter.Name, "doNotLeaseOnDeclaration", StringComparison.Ordinal)));
        ConstructorInfo[] builderConstructors = typeof(NativeBuilder<int>).GetConstructors();
        Assert.Equal(3, builderConstructors.Length);
        ConstructorInfo builderConstructor = SingleExpected(
            builderConstructors,
            static constructor => constructor.GetParameters().Length == 1
                && constructor.GetParameters()[0].ParameterType == typeof(int));
        Assert.Contains(builderConstructors,
            static constructor => constructor.GetParameters().Length == 0);
        ConstructorInfo budgetedBuilderConstructor = SingleExpected(
            builderConstructors,
            static constructor => constructor.GetParameters().Length == 2);
        Assert.Equal(typeof(NativeMemoryBudget), budgetedBuilderConstructor.GetParameters()[0].ParameterType);
        Assert.Equal("preLease", budgetedBuilderConstructor.GetParameters()[1].Name);
        Assert.Equal(
            "preLease",
            SingleExpected(builderConstructor.GetParameters()).Name);
        Assert.DoesNotContain(
            builderConstructor.GetParameters(),
            parameter => string.Equals(parameter.Name, "initialCapacity", StringComparison.Ordinal));
        FieldInfo[] builderFields = typeof(NativeBuilder<int>).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.DoesNotContain(
            builderFields,
            field => field.Name is "_borrowEpoch"
                or "_activeBorrowAuthority");
        FieldInfo[] borrowFields = typeof(NativeBuilderBorrow<int>)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(4, borrowFields.Length);
        Assert.Equal(
            typeof(NativeBuilder<int>),
            SingleExpected(
                borrowFields,
                static field => string.Equals(field.Name, "_builder", StringComparison.Ordinal)).FieldType);
        Assert.Equal(
            typeof(IntPtr).MakeByRefType(),
            SingleExpected(
                borrowFields,
                static field => string.Equals(field.Name, "_address", StringComparison.Ordinal)).FieldType);
        Assert.Equal(
            typeof(int).MakeByRefType(),
            SingleExpected(
                borrowFields,
                static field => string.Equals(field.Name, "_count", StringComparison.Ordinal)).FieldType);
        Assert.Equal(
            typeof(int).MakeByRefType(),
            SingleExpected(
                borrowFields,
                static field => string.Equals(field.Name, "_capacity", StringComparison.Ordinal)).FieldType);
        Assert.DoesNotContain(
            borrowFields,
            static field => field.Name is "_borrowEpoch"
                or "_activeBorrowAuthority");
        ConstructorInfo arenaConstructor = SingleExpected(typeof(NativeArena).GetConstructors());
        Assert.Contains(arenaConstructor.GetParameters(), parameter => string.Equals(parameter.Name, "returnMemoryOnDispose", StringComparison.Ordinal));
        Assert.DoesNotContain(
            arenaConstructor.GetParameters(),
            parameter => string.Equals(parameter.Name, "doNotLeaseOnDeclaration", StringComparison.Ordinal));
        ConstructorInfo concurrentArenaConstructor = SingleExpected(
            typeof(NativeConcurrentArena).GetConstructors());
        Assert.Contains(
            concurrentArenaConstructor.GetParameters(),
            parameter => string.Equals(parameter.Name, "doNotLeaseOnDeclaration", StringComparison.Ordinal));
    }
}
