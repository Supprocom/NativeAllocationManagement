namespace Supprocom.NativeAllocationManagement.Tests;

internal static class CodeFixPackageFixture
{
    internal static string Project(string version) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Supprocom.NativeAllocationManagement" Version="{{version}}" GeneratePathProperty="true" />
            <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.14.0" PrivateAssets="all" />
            <Reference Include="Supprocom.NativeAllocationManagement.Analyzers">
              <HintPath>$(PkgSupprocom_NativeAllocationManagement)/analyzers/dotnet/cs/Supprocom.NativeAllocationManagement.Analyzers.dll</HintPath>
            </Reference>
            <Reference Include="Supprocom.NativeAllocationManagement.CodeFixes">
              <HintPath>$(PkgSupprocom_NativeAllocationManagement)/analyzers/dotnet/cs/Supprocom.NativeAllocationManagement.CodeFixes.dll</HintPath>
            </Reference>
          </ItemGroup>
        </Project>
        """;

    internal const string Program = """"
        using System.Collections.Immutable;
        using System.Composition.Hosting;
        using System.Reflection;
        using System.Security.Cryptography;
        using Microsoft.CodeAnalysis;
        using Microsoft.CodeAnalysis.CodeActions;
        using Microsoft.CodeAnalysis.CodeFixes;
        using Microsoft.CodeAnalysis.CSharp;
        using Microsoft.CodeAnalysis.Diagnostics;
        using Microsoft.CodeAnalysis.Text;
        using Supprocom.NativeAllocationManagement;
        using Supprocom.NativeAllocationManagement.Analyzers;
        using Supprocom.NativeAllocationManagement.CodeFixes;

        internal static class Program
        {
            private static async Task Main(string[] args)
            {
                if (args.Length != 3) throw new InvalidOperationException("Three exact package assembly hashes are required.");
                Verify(typeof(NativePool<int>).Assembly, args[0]);
                Verify(typeof(NativeAllocationAnalyzer).Assembly, args[1]);
                Assembly companion = typeof(NativeOwnershipCodeFixProvider).Assembly;
                Verify(companion, args[2]);
                using CompositionHost composition = new ContainerConfiguration().WithAssembly(companion).CreateContainer();
                CodeFixProvider[] discovered = composition.GetExports<CodeFixProvider>().ToArray();
                if (discovered.Length != 1) throw new InvalidOperationException("The actual packaged export was not uniquely discovered.");
                CodeFixProvider provider = discovered[0];
                ExportCodeFixProviderAttribute export = provider.GetType().GetCustomAttribute<ExportCodeFixProviderAttribute>()
                    ?? throw new InvalidOperationException("The export is not a language-scoped Roslyn provider.");
                if (!export.Languages.Contains(LanguageNames.CSharp, StringComparer.Ordinal))
                    throw new InvalidOperationException("C# discovery metadata is absent.");
                using AdhocWorkspace workspace = new();
                string platform = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                    ?? throw new InvalidOperationException("Runtime reference identities are absent.");
                MetadataReference[] references = platform.Split(Path.PathSeparator)
                    .Append(typeof(NativePool<int>).Assembly.Location).Distinct(StringComparer.Ordinal)
                    .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray();
                Project project = workspace.AddProject("IsolatedPackageConsumer", LanguageNames.CSharp)
                    .WithParseOptions(new CSharpParseOptions(LanguageVersion.CSharp13))
                    .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                    .WithMetadataReferences(references);
                Document original = project.AddDocument("Consumer.cs", SourceText.From("""
                    using Supprocom.NativeAllocationManagement;
                    public static class Consumer { public static void Run() { NativeRegion region = new(); } }
                    """));
                Compilation before = (await original.Project.GetCompilationAsync())!;
                Diagnostic[] defects = (await Analyze(before)).Where(static defect => defect.Id == "NAM1006").ToArray();
                if (defects.Length != 1) throw new InvalidOperationException("The package analyzer did not establish the original ownership defect.");
                List<CodeAction> actions = new();
                await provider.RegisterCodeFixesAsync(new CodeFixContext(original, defects[0],
                    (action, _) => actions.Add(action), CancellationToken.None));
                if (actions.Count != 1) throw new InvalidOperationException("The discovered package provider did not offer its proven correction.");
                ApplyChangesOperation[] changes = (await actions[0].GetOperationsAsync(CancellationToken.None))
                    .OfType<ApplyChangesOperation>().ToArray();
                if (changes.Length != 1) throw new InvalidOperationException("The actual package action did not produce one document correction.");
                Document corrected = changes[0].ChangedSolution.GetDocument(original.Id)!;
                Compilation after = (await corrected.Project.GetCompilationAsync())!;
                if (after.GetDiagnostics().Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    || (await Analyze(after)).Any(static diagnostic => diagnostic.Id.StartsWith("NAM", StringComparison.Ordinal)))
                    throw new InvalidOperationException("The applied correction did not compile with a clean ownership gate.");
                Console.WriteLine("packageMefExports=1;codeActions=1;correctedCompilerErrors=0;correctedNamDiagnostics=0");
            }

            private static Task<ImmutableArray<Diagnostic>> Analyze(Compilation compilation) => compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new NativeAllocationAnalyzer())).GetAnalyzerDiagnosticsAsync();

            private static void Verify(Assembly assembly, string expected)
            {
                using FileStream content = File.OpenRead(assembly.Location);
                string actual = Convert.ToHexString(SHA256.HashData(content));
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    throw new InvalidOperationException("Loaded assembly does not match candidate bytes: " + assembly.FullName);
                Console.WriteLine("loaded=" + assembly.Location + ";sha256=" + actual);
            }
        }
        """";
}
