using System.Reflection;
using Xunit.Sdk;

[assembly: Supprocom.NativeAllocationManagement.Tests.FreshNativeReuseSlots]

namespace Supprocom.NativeAllocationManagement.Tests;

// Per-thread reuse slots outlive a test on its thread; each test starts with
// empty slots so physical history assertions do not depend on test order.
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class FreshNativeReuseSlotsAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) =>
        NativeStorageReuse.InvalidateCurrentThreadForTest();
}
