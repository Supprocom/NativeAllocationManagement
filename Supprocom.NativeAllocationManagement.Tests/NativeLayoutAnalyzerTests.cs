using Microsoft.CodeAnalysis;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativeLayoutAnalyzerTests
{
    [Fact]
    public async Task GuardedPublicationAndIndependentFieldDetachRequireAndPreserveCleanup()
    {
        string source = Wrap("""
            if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _)) return;
            try
            {
                permission.Value.PrepareBacking();
                using NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, field,
                    static (writer, token) => writer.Region(token).Fill(17));
                _ = owner.Read(field, static (view, token) => view.Region(token)[0]);
                using NativeTransfer<int> copy = owner.DetachField(field, budget);
                _ = copy.Read(static view => view[0]);
            }
            finally { permission?.Dispose(); }
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    [Theory]
    [InlineData("layout.TryReserve(budget, out NativeLayoutReservation? permission, out _); permission.Value.PrepareBacking();", "NAM1050")]
    [InlineData("if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _)) return;", "NAM1025")]
    [InlineData("if (layout.TryReserve(budget, out NativeLayoutReservation? permission, out _)) permission.Value.Dispose(); else permission.Value.PrepareBacking();", "NAM1022")]
    [InlineData("layout.TryReserve(budget, out _, out _);", "NAM1026")]
    public async Task UnguardedRefusedUnreleasedAndDiscardedLayoutPermissionsAreRejected(string body, string expected)
    {
        string source = Wrap(body);
        AssertCompiles(source);
        Assert.Contains(expected, AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task DestructiveMoveInvalidatesItsSourceAndCannotDropTheDestination()
    {
        string source = Wrap("""
            if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _)) return;
            using NativeLayoutReservation moved = NativeLayoutReservation.Move(ref permission);
            moved.PrepareBacking();
            """);
        AssertCompiles(source);
        Assert.Empty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
        string invalid = source.Replace("moved.PrepareBacking();", "permission.Value.PrepareBacking();", StringComparison.Ordinal);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(invalid)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task AStaleLayoutReceiverCannotProduceDetachedOwnership()
    {
        string source = Wrap("""
            if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _)) return;
            NativeLayoutOwner? sourceOwner = NativeLayoutReservation.Activate(ref permission, field,
                static (writer, token) => writer.Region(token).Fill(17));
            using NativeLayoutOwner moved = NativeLayoutOwner.Move(ref sourceOwner);
            using NativeTransfer<int> copy = sourceOwner.Value.DetachField(field, budget);
            """);
        AssertCompiles(source);
        Assert.Contains("NAM1022", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("_ = owner.Read(field, static (view, token) => { Unscoped(view.Region(token)); return 0; });", "static void Unscoped(System.Span<int> values) { }")]
    [InlineData("", "static void Unscoped(NativeLayoutWriter writer) { }")]
    public async Task TypedViewAndInitializerCannotEnterUnknownUnscopedForwarding(string access, string helper)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(helper);
        string initializer = access.Length == 0 ? "Unscoped(writer); writer.Region(token).Fill(17);" : "writer.Region(token).Fill(17);";
        string source = Wrap($$"""
            if (!layout.TryReserve(budget, out NativeLayoutReservation? permission, out _)) return;
            using NativeLayoutOwner owner = NativeLayoutReservation.Activate(ref permission, field,
                static (writer, token) => { {{initializer}} });
            {{access}}
            """).Replace("public static void Run()", helper + "\npublic static void Run()", StringComparison.Ordinal);
        AssertCompiles(source);
        Assert.Contains("NAM1024", AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ASourceLookalikeCannotGrantRuntimeLayoutPermission()
    {
        const string source = """
            using Supprocom.NativeAllocationManagement;
            namespace Supprocom.NativeAllocationManagement
            {
                public sealed class NativeLayout
                {
                    public bool TryReserve(NativeMemoryBudget budget, out NativeLayoutReservation? permission,
                        out NativeMemoryAdmissionExhaustionReason reason)
                    { permission = default; reason = default; return true; }
                }
            }
            public static class Sample
            {
                public static void Run()
                {
                    NativeLayout layout = new();
                    if (!layout.TryReserve(new(8), out NativeLayoutReservation? permission, out _)) return;
                    permission.Value.PrepareBacking();
                    permission.Value.Dispose();
                }
            }
            """;
        AssertCompiles(source);
        Assert.NotEmpty(AnalyzerContractTests.NativeDiagnostics(await AnalyzerContractTests.AnalyzeAsync(source)));
    }

    private static string Wrap(string body) => $$"""
        using Supprocom.NativeAllocationManagement;
        public static class Sample
        {
            public static void Run()
            {
                NativeLayoutBuilder builder = new(1);
                NativeLayoutField<int> field = builder.Add<int>(2);
                NativeLayout layout = builder.Build();
                NativeMemoryBudget budget = new(layout.BackingBytes + 8);
                {{body}}
            }
        }
        """;

    private static void AssertCompiles(string source) => Assert.DoesNotContain(
        AnalyzerContractTests.Compile(source), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
