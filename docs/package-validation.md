# Package and symbol verification

Packing produces a primary `.nupkg` and a matching `.snupkg`. Portable symbols
cover every shipped assembly: runtime, analyzer and code fixes. The analyzer and
code-fix PDBs are also included beside their DLLs in the primary archive. A
missing bundled PDB is a packaging error, not an optional debugging limitation.

Package-consumer tests verify the symbol archive's package identity/version and
repository commit, all three assembly/PDB identities, the compiler's PDB SHA-256
checksum, Source Link and each source-document hash against the actual compiler
input. The retained `symbols-verification.json` distinguishes repository source
from generated input and records their identities. Negative tests reject swapped
PDBs, altered PDB bytes and a different claimed commit. Symbol coverage is not
established merely by finding files named `.pdb`.

Final release evidence must come from one exact clean source commit, with all
commands, exit codes, original compiler inputs, consumer caches, emitted binaries
and complete length/SHA-256 manifests retained outside the repository. A dirty
checkpoint can legitimately carry its parent's Source Link; it is not a final
release candidate. Isolated restores, analyzer enforcement, stale-handle runtime
guards, and actual rooted trimmed/NativeAOT execution remain separate gates.

The symbol archive follows the [NuGet symbol-package specification](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg):
PDB paths correspond to primary-package DLL paths, and symbols do not include
DLL payloads. The compiler checksum is distinct from the archive's SHA-256:
[Roslyn computes it before filling the PDB content ID](https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/PEWriter/PeWriter.cs),
and the [portable PDB builder fills that ID after hashing](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Reflection.Metadata/src/System/Reflection/Metadata/Ecma335/PortablePdbBuilder.cs).
Verification normalizes only that declared 20-byte ID and still checks its
unmodified value against the assembly's CodeView identity.
Publishing and symbol-server indexing require their own verified
release handoff; successful local packing does not establish either.
