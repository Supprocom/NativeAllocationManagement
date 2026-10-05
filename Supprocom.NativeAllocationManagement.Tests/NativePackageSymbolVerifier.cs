using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Supprocom.NativeAllocationManagement.Tests;

internal static class NativePackageSymbolVerifier
{
    private static readonly Guid SourceLinkKind = new("cc110556-a091-4d38-9fec-25ab9a351a6a");
    private static readonly Guid Sha256DocumentKind = new("8829d00f-11b8-4213-878b-770e8597ac16");

    internal static void VerifyPair(byte[] assembly, byte[] symbols)
    {
        using MemoryStream assemblyStream = new(assembly, writable: false);
        using PEReader pe = new(assemblyStream);
        using MemoryStream symbolStream = new(symbols, writable: false);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(symbolStream);
        MetadataReader metadata = provider.GetMetadataReader();
        BlobContentId id = new(metadata.DebugMetadataHeader!.Id);
        int codeViews = 0;
        int checksums = 0;
        foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
        {
            if (entry.Type == DebugDirectoryEntryType.CodeView)
            {
                CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(entry);
                if (codeView.Guid != id.Guid || entry.Stamp != id.Stamp || codeView.Age != 1)
                    throw new InvalidDataException("The portable PDB does not identify this assembly.");
                codeViews++;
            }
            else if (entry.Type == DebugDirectoryEntryType.PdbChecksum)
            {
                PdbChecksumDebugDirectoryData checksum = pe.ReadPdbChecksumDebugDirectoryData(entry);
                if (!string.Equals(checksum.AlgorithmName, "SHA256", StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(checksum.Checksum.AsSpan(), CompilerPdbChecksum(symbols, metadata.DebugMetadataHeader!)))
                    throw new InvalidDataException("The portable PDB does not match the compiler's SHA-256 checksum.");
                checksums++;
            }
        }
        if (codeViews != 1 || checksums != 1)
            throw new InvalidDataException("Exactly one CodeView identity and PDB checksum are required.");
    }

    private static byte[] CompilerPdbChecksum(byte[] symbols, DebugMetadataHeader header)
    {
        // Roslyn hashes the serialized PDB before filling its 20-byte content
        // ID. The archive SHA-256 separately hashes the final, unmodified bytes.
        if (header.Id.Length != 20) throw new InvalidDataException("Unexpected portable PDB content ID length.");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(symbols.AsSpan(0, header.IdStartOffset));
        Span<byte> zeroId = stackalloc byte[20];
        zeroId.Clear();
        hash.AppendData(zeroId);
        hash.AppendData(symbols.AsSpan(header.IdStartOffset + zeroId.Length));
        return hash.GetHashAndReset();
    }

    internal static NativeSymbolSourceEvidence VerifySource(byte[] symbols, string repositoryRoot, string commit)
    {
        using MemoryStream symbolStream = new(symbols, writable: false);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(symbolStream);
        MetadataReader metadata = provider.GetMetadataReader();
        string? sourceLink = null;
        foreach (CustomDebugInformationHandle handle in metadata.CustomDebugInformation)
        {
            CustomDebugInformation information = metadata.GetCustomDebugInformation(handle);
            if (metadata.GetGuid(information.Kind) != SourceLinkKind) continue;
            if (sourceLink is not null || information.Parent.Kind != HandleKind.ModuleDefinition)
                throw new InvalidDataException("Source Link must be unique and module-scoped.");
            sourceLink = System.Text.Encoding.UTF8.GetString(metadata.GetBlobBytes(information.Value));
        }
        if (sourceLink is null) throw new InvalidDataException("The portable PDB lacks Source Link.");
        using JsonDocument json = JsonDocument.Parse(sourceLink);
        Dictionary<string, string> maps = new(StringComparer.Ordinal);
        string expectedUrl = "https://raw.githubusercontent.com/Supprocom/NativeAllocationManagement/" + commit + "/*";
        foreach (JsonProperty property in json.RootElement.GetProperty("documents").EnumerateObject())
        {
            if (!property.Name.EndsWith('*') || !string.Equals(property.Value.GetString(), expectedUrl, StringComparison.Ordinal))
                throw new InvalidDataException("Source Link does not bind the exact repository commit.");
            maps.Add(property.Name[..^1], property.Value.GetString()!);
        }
        if (maps.Count == 0) throw new InvalidDataException("Source Link contains no document map.");
        string root = Path.GetFullPath(repositoryRoot) + Path.DirectorySeparatorChar;
        List<NativeSymbolDocumentEvidence> documents = [];
        int linked = 0;
        int generated = 0;
        foreach (DocumentHandle handle in metadata.Documents)
        {
            Document document = metadata.GetDocument(handle);
            string name = metadata.GetString(document.Name);
            if (metadata.GetGuid(document.HashAlgorithm) != Sha256DocumentKind)
                throw new InvalidDataException("Source documents must carry compiler SHA-256 hashes.");
            byte[] hash = metadata.GetBlobBytes(document.Hash);
            string file = Path.GetFileName(name);
            bool isGenerated = file.EndsWith(".AssemblyInfo.cs", StringComparison.Ordinal)
                || file.EndsWith(".GlobalUsings.g.cs", StringComparison.Ordinal)
                || (file.StartsWith(".NET", StringComparison.Ordinal) && file.EndsWith(".AssemblyAttributes.cs", StringComparison.Ordinal));
            string? path = null;
            foreach (string prefix in maps.Keys)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (path is not null) throw new InvalidDataException("A source document has ambiguous Source Link mappings.");
                path = Path.GetFullPath(Path.Combine(root, name[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("A source document escapes the repository root.");
            }
            if (path is not null && File.Exists(path))
            {
                if (isGenerated) generated++;
                else linked++;
            }
            else
            {
                if (!isGenerated || !Path.IsPathFullyQualified(name) || !File.Exists(name))
                    throw new InvalidDataException("A PDB source document cannot be reconciled: " + name);
                path = name;
                generated++;
            }
            if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(File.ReadAllBytes(path))))
                throw new InvalidDataException("The PDB source hash differs from the actual compiler input: " + name);
            documents.Add(new(name, path, Convert.ToHexString(hash)));
        }
        if (linked == 0) throw new InvalidDataException("No repository source document was verified.");
        return new(sourceLink, linked, generated, documents.ToArray());
    }
}

internal sealed record NativeSymbolDocumentEvidence(string PdbPath, string ActualPath, string SHA256);
internal sealed record NativeSymbolSourceEvidence(string SourceLink, int RepositoryDocuments, int GeneratedDocuments,
    NativeSymbolDocumentEvidence[] Documents);
