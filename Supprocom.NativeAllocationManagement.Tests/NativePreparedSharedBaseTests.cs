using System.Reflection;
using System.Runtime.CompilerServices;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePreparedSharedBaseTests
{
    [Fact]
    public void ColdBaseIsOneNonConstructibleOwnerWithoutAnotherControlObject()
    {
        Type shared = typeof(NativePreparedPoolBase);
        Assert.True(shared.IsPublic && shared.IsAbstract && !shared.IsGenericType);
        ConstructorInfo[] constructors = shared.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(constructors.Length is 1);
        ConstructorInfo constructor = constructors[0];
        Assert.True(constructor.IsFamilyAndAssembly);
        Assert.Empty(shared.GetConstructors());
        Assert.Empty(shared.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
        FieldInfo[] fields = shared.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.Equal(23, fields.Length);
        Assert.All(fields, static field => Assert.True(field.IsFamilyAndAssembly));
        Assert.Equal(3, fields.Count(static field => !field.FieldType.IsValueType));
        Type[] derived = shared.Assembly.GetTypes().Where(type => type.BaseType == shared).ToArray();
        Assert.True(derived.Length is 1);
        Assert.Equal(typeof(NativePreparedPool<>), derived[0]);
        foreach (Type element in new[] { typeof(byte), typeof(ushort), typeof(int), typeof(long), typeof(Guid), typeof(System.Numerics.Vector3) })
        {
            Type owner = typeof(NativePreparedPool<>).MakeGenericType(element);
            Assert.True(owner.IsSealed);
            Assert.Equal(shared, owner.BaseType);
            Assert.Empty(owner.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
            Assert.True(owner.GetConstructors().Length is 1);
            Assert.Equal(owner, owner.GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance)!.DeclaringType);
        }
    }

    [Fact]
    public void BanksBoundsThreadAndIdentityRemainReadonlyOnTheSameOwner()
    {
        FieldInfo[] fields = typeof(NativePreparedPoolBase).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.Equal("_id|_ownerThreadId|_pages|_preparation|_slabs",
            string.Join("|", fields.Where(static field => field.IsInitOnly).Select(static field => field.Name).Order(StringComparer.Ordinal)));
        Assert.DoesNotContain(fields, static field => field.Name is "_state" or "_kernel" or "_freshSegmentAllocationCount" or "_nextAllocationOrdinal");
    }

    [Fact]
    public void TypedHotOperationsKeepTheirOriginalNonvirtualInliningContracts()
    {
        Type owner = typeof(NativePreparedPool<int>);
        foreach (string name in new[] { "EnterBorrow", "Return", "ExitBorrow", "Move", "ValidateOwner", "ValidateThread", "ValidateLease", "TakeLeaseToken" })
        {
            MethodInfo method = owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
            Assert.NotNull(method);
            Assert.False(method.IsVirtual);
            Assert.Null(typeof(NativePreparedPoolBase).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance));
            if (name is "EnterBorrow" or "Return")
                Assert.Equal(MethodImplAttributes.NoInlining, method.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining);
            else if (name is not "Move")
                Assert.Equal(MethodImplAttributes.AggressiveInlining, method.GetMethodImplementationFlags() & MethodImplAttributes.AggressiveInlining);
        }
        Assert.All(typeof(NativePreparedPoolBase).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            static method => Assert.False(method.IsVirtual));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AllSixTypedOwnersSurviveRequestedCompactingCollectionDuringInitializationAndBorrow(int shape)
    {
        switch (shape)
        {
            case 0: CheckCollection<byte>(7); break;
            case 1: CheckCollection<ushort>(7); break;
            case 2: CheckCollection(7); break;
            case 3: CheckCollection(7L); break;
            case 4: CheckCollection(new Guid("07070707-0707-0707-0707-070707070707")); break;
            case 5: CheckCollection(new System.Numerics.Vector3(7, 11, 13)); break;
            default: Assert.Fail("Unexpected declared unmanaged shape."); break;
        }
    }

    [Fact]
    public void TrimAndDisposalPreserveOriginalPageHistoryAndTheRealOwnerIdentity()
    {
        NativeMemoryBudget budget = new(24);
        using NativePreparedPool<int> pool = new(new NativePoolPreparation(3, 2, 1), budget);
        long identity = pool.Id;
        using (PreparedPooled<int> lease = pool.Rent(2, 7))
        {
            Assert.Equal((nuint)16, pool.TrimRetainedMemory());
            Assert.Equal(3, pool.GetStatistics().FreshSegmentAllocationCount);
            Assert.Equal(1, pool.CapturePreparedSnapshot().RetainedPageCount);
            Assert.Equal(8, budget.CaptureStatistics().CommittedBytes);
            Assert.Equal(14, lease.Read(static view => view[0] + view[1]));
        }
        Assert.Equal((nuint)8, pool.TrimRetainedMemory());
        Assert.Equal(3, pool.GetStatistics().FreshSegmentAllocationCount);
        Assert.Equal(0, pool.CapturePreparedSnapshot().RetainedPageCount);
        pool.Dispose();
        Assert.Equal(identity, pool.Id);
        Assert.Equal(identity, pool.CapturePreparedSnapshot().OwnerId);
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(3, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(3, budget.CaptureStatistics().FreeCount);
    }

    [Theory]
    [InlineData("new NativePreparedPoolBase();", "CS0144")]
    [InlineData("NativePreparedPoolBase owner = null; _ = owner._slabs;", "CS0122")]
    [InlineData("NativePreparedPoolBase owner = null; _ = owner.GetStatisticsCore(4);", "CS0122")]
    [InlineData("", "CS0122")]
    public void ExternalConsumersCannotConstructDeriveOrReachColdAuthority(string body, string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(body);
        string source = body.Length == 0
            ? "using Supprocom.NativeAllocationManagement; sealed class Foreign : NativePreparedPoolBase { public Foreign() : base(default, null, 4) {} }"
            : "using Supprocom.NativeAllocationManagement; static class Consumer { static void Run() { " + body + " } }";
        var diagnostics = AnalyzerContractTests.Compile(source);
        Assert.Contains(diagnostics, item => string.Equals(item.Id, diagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AnalyzerStillRejectsErasureThroughTheImplementationBase(int escape)
    {
        string use = escape switch
        {
            0 => "NativePreparedPoolBase erased = owner; System.GC.KeepAlive(erased);",
            1 => "Accept(owner);",
            2 => "return owner;",
            _ => throw new ArgumentOutOfRangeException(nameof(escape))
        };
        string source = "using Supprocom.NativeAllocationManagement; static class Consumer { static void Accept(NativePreparedPoolBase erased) {} static "
            + (escape == 2 ? "NativePreparedPoolBase" : "void")
            + " Run() { using NativePreparedPool<int> owner = new(new NativePoolPreparation(1, 2, 1), null); " + use + " } }";
        var diagnostics = await AnalyzerContractTests.AnalyzeAsync(source, treatWarningsAsErrors: true);
        Assert.DoesNotContain(diagnostics, static item => item.Id.StartsWith("CS", StringComparison.Ordinal));
        Assert.Contains(diagnostics, static item => string.Equals(item.Id, "NAM1001", StringComparison.Ordinal));
    }

    private static void CheckCollection<T>(T value) where T : unmanaged
    {
        NativeMemoryBudget budget = new(checked(2L * Unsafe.SizeOf<T>()));
        using NativePreparedPool<T> pool = new(new NativePoolPreparation(1, 2, 1), budget);
        long identity = pool.Id;
        using (PreparedPooled<T> lease = pool.Rent(2, writer =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Assert.Equal(1, pool.CurrentInitializationCountForTest);
            writer.Fill(value);
        }))
        {
            lease.Access(view =>
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Assert.Equal(1, pool.CurrentGenerationActiveOperationsForTest);
                Assert.Equal(value, view[0]);
                Assert.Equal(value, view[1]);
            });
            Assert.Equal(identity, pool.CapturePreparedSnapshot().OwnerId);
        }
        pool.Dispose();
        Assert.Equal(0, budget.CaptureStatistics().CommittedBytes);
        Assert.Equal(1, budget.CaptureStatistics().AllocationCount);
        Assert.Equal(1, budget.CaptureStatistics().FreeCount);
        Assert.Equal(0, pool.CaptureDiagnosticSnapshot().OutstandingNativeBytes);
    }
}
