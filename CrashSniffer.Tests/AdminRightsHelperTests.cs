using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class AdminRightsHelperTests
{
    [Fact]
    public void IsRunningAsAdmin_ReturnsBoolean()
    {
        bool result = AdminRightsHelper.IsRunningAsAdmin();
        Assert.IsType<bool>(result);
    }
}
