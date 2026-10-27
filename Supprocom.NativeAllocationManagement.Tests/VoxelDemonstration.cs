namespace Supprocom.NativeAllocationManagement.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class VoxelDemonstrationFactAttribute : FactAttribute
{
    public VoxelDemonstrationFactAttribute()
    {
        if (!VoxelDemonstration.IsEnabled)
        {
            Skip = VoxelDemonstration.SkipMessage;
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class VoxelDemonstrationTheoryAttribute : TheoryAttribute
{
    public VoxelDemonstrationTheoryAttribute()
    {
        if (!VoxelDemonstration.IsEnabled)
        {
            Skip = VoxelDemonstration.SkipMessage;
        }
    }
}

internal static class VoxelDemonstration
{
    internal const string SkipMessage =
        "Set NAM_RUN_VOXEL_DEMO=1 to run the optional voxel demonstration.";

    internal static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(
                "NAM_RUN_VOXEL_DEMO"),
            "1",
            StringComparison.Ordinal);
}
