using System.Text.Json;
using Supprocom.NativeAllocationManagement;

namespace Supprocom.NativeAllocationManagement.Tests;

public sealed class LifecycleConformanceTests
{
    [Fact]
    public void ExecutableLifecycleCorpusMatchesObservedOwnerStates()
    {
        string path = Path.Combine(FindRepositoryRoot(), "conformance", "native-allocation-lifecycle.json");
        LifecycleCase[] cases = JsonSerializer.Deserialize<LifecycleCase[]>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The executable lifecycle corpus was empty.");

        foreach (LifecycleCase testCase in cases)
        {
            NativeOwnerLifecycle[] actual = Execute(testCase).ToArray();
            Assert.Equal(testCase.States, actual.Select(state => state.ToString()).ToArray());
            Assert.Equal(testCase.Result, actual[^1].ToString());
        }

        Assert.Equal(18, cases.Length);
    }

    private static IReadOnlyList<NativeOwnerLifecycle> Execute(LifecycleCase testCase)
    {
        List<NativeOwnerLifecycle> states = [];
        NativeMemoryReturn returnPolicy = Enum.Parse<NativeMemoryReturn>(testCase.ReturnPolicy, ignoreCase: false);

        if (testCase.Owner == "pool")
        {
            NativePool<int> pool = new(testCase.InitialReservation, returnPolicy, testCase.DelayedActivation);
            states.Add(pool.CurrentLifecycle);
            if (testCase.ReturnKind == "disposeBeforeActivation")
            {
                pool.Dispose();
                states.Add(pool.CurrentLifecycle);
                return states;
            }

            if (testCase.DelayedActivation)
            {
                pool.LeaseFromMemory();
                states.Add(pool.CurrentLifecycle);
            }
            ExecuteReturn(pool, testCase.ReturnKind);
            if (testCase.ReturnKind != "none")
            {
                states.Add(pool.CurrentLifecycle);
            }

            if (testCase.ReLease)
            {
                pool.LeaseFromMemory();
                states.Add(pool.CurrentLifecycle);
            }

            pool.Dispose();
            states.Add(pool.CurrentLifecycle);
            return states;
        }

        if (testCase.Owner == "region")
        {
            NativeRegion region = new((nuint)testCase.InitialReservation, returnPolicy, testCase.DelayedActivation);
            states.Add(region.CurrentLifecycle);
            if (testCase.ReturnKind == "disposeBeforeActivation")
            {
                region.Dispose();
                states.Add(region.CurrentLifecycle);
                return states;
            }

            if (testCase.DelayedActivation)
            {
                region.LeaseFromMemory();
                states.Add(region.CurrentLifecycle);
            }
            ExecuteReturn(region, testCase.ReturnKind);
            if (testCase.ReturnKind != "none")
            {
                states.Add(region.CurrentLifecycle);
            }

            region.Dispose();
            states.Add(region.CurrentLifecycle);
            return states;
        }

        if (testCase.Owner == "arena")
        {
            NativeArena arena = new((nuint)testCase.InitialReservation, returnPolicy, testCase.DelayedActivation);
            states.Add(arena.CurrentLifecycle);
            if (testCase.ReturnKind == "disposeBeforeActivation")
            {
                arena.Dispose();
                states.Add(arena.CurrentLifecycle);
                return states;
            }

            if (testCase.DelayedActivation)
            {
                arena.LeaseFromMemory();
                states.Add(arena.CurrentLifecycle);
            }

            ExecuteReturn(arena, testCase.ReturnKind);
            if (testCase.ReturnKind is "native" or "garbageCollector")
            {
                states.Add(arena.CurrentLifecycle);
            }
            else if (testCase.ReturnKind is "releaseNative" or "releaseGarbageCollector")
            {
                states.Add(arena.CurrentLifecycle);
            }

            if (testCase.ReLease)
            {
                arena.LeaseFromMemory();
                states.Add(arena.CurrentLifecycle);
            }

            arena.Dispose();
            states.Add(arena.CurrentLifecycle);
            return states;
        }

        throw new InvalidDataException($"Unknown lifecycle owner '{testCase.Owner}'.");
    }

    private static void ExecuteReturn(NativePool<int> pool, string returnKind)
    {
        switch (returnKind)
        {
            case "native":
                pool.ReturnMemoryToNativeMemory();
                break;
            case "garbageCollector":
                pool.ReturnMemoryToGarbageCollector();
                break;
            case "none":
                break;
            default:
                throw new InvalidDataException($"Unknown pool return kind '{returnKind}'.");
        }
    }

    private static void ExecuteReturn(NativeRegion region, string returnKind)
    {
        switch (returnKind)
        {
            case "native":
                region.ReturnMemoryToNativeMemory();
                break;
            case "garbageCollector":
                region.ReturnMemoryToGarbageCollector();
                break;
            case "none":
                break;
            default:
                throw new InvalidDataException($"Unknown region return kind '{returnKind}'.");
        }
    }

    private static void ExecuteReturn(NativeArena arena, string returnKind)
    {
        switch (returnKind)
        {
            case "native":
                arena.ReturnMemoryToNativeMemory();
                break;
            case "garbageCollector":
                arena.ReturnMemoryToGarbageCollector();
                break;
            case "releaseNative":
                arena.ReleaseLeasesToNativeMemory();
                break;
            case "releaseGarbageCollector":
                arena.ReleaseLeasesToGarbageCollector();
                break;
            case "none":
                break;
            default:
                throw new InvalidDataException($"Unknown arena return kind '{returnKind}'.");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Supprocom.NativeAllocationManagement.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The repository root was not found from the test output directory.");
    }

    private sealed record LifecycleCase(
        string Name,
        string Owner,
        int InitialReservation,
        string ReturnPolicy,
        string ReturnKind,
        bool ReLease,
        bool DelayedActivation,
        string[] States,
        string Result);
}
