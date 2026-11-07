using System.Diagnostics;
using GreatMaoNet.Updates;

namespace GreatMaoNet.Tests;

public sealed class ParentProcessIdentityTests
{
    private const string Boot = "01234567-89ab-cdef-0123-456789abcdef";

    [Theory]
    [InlineData("worker")]
    [InlineData("worker with spaces")]
    [InlineData("worker (nested) ) name")]
    public void KernelStarttimeIsParsedIndependentlyOfProcessName(string name)
    {
        string stat = $"123 ({name}) S " + string.Join(' ', Enumerable.Repeat("0", 18)) + " 123456 999 888\n";
        Assert.Equal($"linux:{Boot}:123456", ParentProcessIdentity.FromLinuxProc(stat, Boot + "\n"));
        Assert.NotEqual(ParentProcessIdentity.FromLinuxProc(stat, Boot),
            ParentProcessIdentity.FromLinuxProc(stat.Replace("123456 999", "123457 999"), Boot));
        Assert.NotEqual(ParentProcessIdentity.FromLinuxProc(stat, Boot),
            ParentProcessIdentity.FromLinuxProc(stat, "11234567-89ab-cdef-0123-456789abcdef"));
    }

    [Theory]
    [InlineData("123 worker S 0 0", Boot)]
    [InlineData("123 (worker) S 0 0", Boot)]
    [InlineData("123 (worker) S 0 0", "invalid-boot-id")]
    public void MalformedKernelIdentityIsRejected(string stat, string boot) =>
        Assert.Throws<InvalidDataException>(() => ParentProcessIdentity.FromLinuxProc(stat, boot));

    [Fact]
    public void FreshProcessHandlesMatchButDifferentIdentityDoesNot()
    {
        using var current = Process.GetCurrentProcess();
        using var reopened = Process.GetProcessById(current.Id);
        var plan = new UpdatePlan { ParentId = current.Id, ParentIdentity = ParentProcessIdentity.Capture(current) };
        Assert.True(ParentProcessIdentity.Matches(reopened, plan));
        plan.ParentIdentity += "-different";
        Assert.False(ParentProcessIdentity.Matches(reopened, plan));
    }

    [Fact]
    public void LegacyWallClockPlansAreAcceptedOnlyOnWindows()
    {
        using var current = Process.GetCurrentProcess();
        var plan = new UpdatePlan { ParentId = current.Id, ParentStartTicks = current.StartTime.ToUniversalTime().Ticks };
        Assert.Equal(OperatingSystem.IsWindows(), ParentProcessIdentity.Matches(current, plan));
    }
}
