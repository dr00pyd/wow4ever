using System.Runtime.CompilerServices;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>CrashGuard (1.0.11): beskrivningen i uploads.log och spärren mot upprepningar i journalen.</summary>
public class CrashReportTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWithPath() => throw new IOException(@"C:\Users\Kalle\World of Warcraft\_forever_\WTF\Account\HEMLIGT\SavedVariables\ForeverScout.lua is locked");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ThrowAsync()
    {
        await Task.Yield();
        throw new ArgumentOutOfRangeException("index");
    }

    [Fact]
    public void DescribeHasTypeHResultAndMethodsButNeverTheMessage()
    {
        var ex = Assert.Throws<IOException>(ThrowWithPath);
        var d = CrashReport.Describe(ex);
        Assert.StartsWith("IOException 0x", d);
        Assert.Contains("CrashReportTests.ThrowWithPath", d);
        Assert.DoesNotContain("Kalle", d);
        Assert.DoesNotContain("HEMLIGT", d);
        Assert.DoesNotContain(@"\", d);
        Assert.DoesNotContain(".cs", d); // inga filnamn/radnummer
    }

    [Fact]
    public async Task AsyncFramesAreReadableAndInnerExceptionsIncluded()
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(ThrowAsync);
        var wrapped = new InvalidOperationException("yttre", ex);
        try { throw wrapped; } catch (InvalidOperationException) { }
        var d = CrashReport.Describe(wrapped);
        Assert.Contains("InvalidOperationException 0x", d);
        Assert.Contains(" | inner: ArgumentOutOfRangeException 0x", d);
        Assert.Contains("CrashReportTests.ThrowAsync", d);
        Assert.DoesNotContain("MoveNext", d);
        Assert.DoesNotContain("d__", d);
    }

    [Fact]
    public void LambdaFramesNameTheirOwner()
    {
        Action a = () => throw new NotSupportedException();
        var ex = Assert.Throws<NotSupportedException>(a);
        var frame = CrashReport.Frames(ex, 1).Single();
        Assert.StartsWith("ForeverScout.Tests.CrashReportTests.", frame);
        Assert.EndsWith("(lambda)", frame);
        Assert.DoesNotContain("<", frame);
    }

    [Fact]
    public void SignatureIsStableForTheSameFault()
    {
        var a = Assert.Throws<IOException>(ThrowWithPath);
        var b = Assert.Throws<IOException>(ThrowWithPath);
        Assert.Equal(CrashReport.Signature(a), CrashReport.Signature(b));
        Assert.Equal("IOException@ForeverScout.Tests.CrashReportTests.ThrowWithPath", CrashReport.Signature(a));
        Assert.Equal("ArgumentException@?", CrashReport.Signature(new ArgumentException())); // aldrig kastat: ingen stack
    }

    [Fact]
    public void ThrottleReportsOncePerWindowAndCountsTheRest()
    {
        var t = new ErrorThrottle(TimeSpan.FromMinutes(1));
        var t0 = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(t.ShouldReport("A", t0, out var n)); Assert.Equal(0, n);
        Assert.False(t.ShouldReport("A", t0.AddSeconds(15), out _));
        Assert.False(t.ShouldReport("A", t0.AddSeconds(30), out _));
        Assert.True(t.ShouldReport("B", t0.AddSeconds(30), out n)); Assert.Equal(0, n); // annat fel: direkt
        Assert.True(t.ShouldReport("A", t0.AddSeconds(61), out n)); Assert.Equal(2, n);  // två bortfiltrerade sedan sist
        Assert.False(t.ShouldReport("A", t0.AddSeconds(90), out _));
    }

    [Fact]
    public void ThrottleNeverGrowsWithoutBound()
    {
        var t = new ErrorThrottle(TimeSpan.FromMinutes(1));
        var now = DateTime.UtcNow;
        for (int i = 0; i < 10_000; i++) Assert.True(t.ShouldReport("sig" + i, now, out _));
    }
}
