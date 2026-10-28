using System.Linq;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class SamplerAutoStartTests
{
    [Fact]
    public void BuildCreateArgs_UsesLogonTriggerWithHighestPrivilege()
    {
        var args = SamplerAutoStart.BuildCreateArgs(@"C:\Apps\CrashSniffer\CrashSniffer.exe");

        Assert.Equal("/Create", args[0]);
        Assert.Contains("/TN", args);
        Assert.Contains(SamplerAutoStart.TaskName, args);
        Assert.Contains("/SC", args);
        Assert.Contains("ONLOGON", args);
        Assert.Contains("/RL", args);
        Assert.Contains("HIGHEST", args);
        Assert.Contains("/F", args);
    }

    [Fact]
    public void BuildCreateArgs_QuotesExePathAndPassesSamplerFlag()
    {
        const string exe = @"C:\Program Files\Crash Sniffer\CrashSniffer.exe";
        var args = SamplerAutoStart.BuildCreateArgs(exe);

        int i = args.IndexOf("/TR");
        Assert.True(i >= 0 && i + 1 < args.Count);
        Assert.Equal($"\"{exe}\" --sampler", args[i + 1]);
    }
}
