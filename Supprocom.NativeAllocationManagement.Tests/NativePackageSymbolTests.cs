using Supprocom.NativeAllocationManagement.Analyzers;
using Supprocom.NativeAllocationManagement.CodeFixes;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class NativePackageSymbolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualPortableSymbolsMatchTheirAssemblyAndRejectModification(int index)
    {
        string[] paths = [typeof(NativePool<int>).Assembly.Location,
            typeof(NativeAllocationAnalyzer).Assembly.Location, typeof(NativeOwnershipCodeFixProvider).Assembly.Location];
        byte[] assembly = File.ReadAllBytes(paths[index]);
        byte[] symbols = File.ReadAllBytes(Path.ChangeExtension(paths[index], ".pdb"));
        NativePackageSymbolVerifier.VerifyPair(assembly, symbols);
        // Mutate a source-name byte without damaging the metadata container.
        // GUID matching alone cannot detect this; the compiler checksum must.
        int offset = symbols.AsSpan().IndexOf("Supprocom.NativeAllocationManagement"u8);
        Assert.True(offset >= 0);
        symbols[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => NativePackageSymbolVerifier.VerifyPair(assembly, symbols));
    }

    [Fact]
    public void PortableSymbolsFromAnotherAssemblyCannotSatisfyThePair()
    {
        byte[] runtime = File.ReadAllBytes(typeof(NativePool<int>).Assembly.Location);
        byte[] analyzerSymbols = File.ReadAllBytes(Path.ChangeExtension(typeof(NativeAllocationAnalyzer).Assembly.Location, ".pdb"));
        Assert.Throws<InvalidDataException>(() => NativePackageSymbolVerifier.VerifyPair(runtime, analyzerSymbols));
    }

    [Fact]
    public void AnotherCommitCannotBeReportedAsVerifiedSourceLink()
    {
        byte[] symbols = File.ReadAllBytes(Path.ChangeExtension(typeof(NativePool<int>).Assembly.Location, ".pdb"));
        Assert.Throws<InvalidDataException>(() => NativePackageSymbolVerifier.VerifySource(symbols, Path.GetTempPath(), "not-the-source-commit"));
    }
}
