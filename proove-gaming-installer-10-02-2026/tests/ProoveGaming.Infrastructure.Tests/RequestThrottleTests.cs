using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// RequestThrottle bounds how often a device is asked something — for TAIPAN, how often a radio frame
// is sent at a battery-powered mouse. The property that matters is not "it waits", it is that the
// claim is ATOMIC: the read-then-write it replaced let several callers pass at once, which is
// reachable whenever the App has more than one card on screen and fires a battery acquire per card.
public class RequestThrottleTests
{
    // Injected clock so the tests neither sleep nor depend on machine speed.
    private sealed class FakeClock
    {
        private long _ticks = DateTime.UtcNow.Ticks;
        public long Now() => Volatile.Read(ref _ticks);
        public void Advance(TimeSpan by) => Volatile.Write(ref _ticks, _ticks + by.Ticks);
    }

    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(20);

    [Fact]
    public void FirstClaim_IsAlwaysAllowed()
    {
        var throttle = new RequestThrottle(new FakeClock().Now);

        Assert.True(throttle.TryAcquire(Spacing));
    }

    // The default clock is monotonic uptime (Environment.TickCount64), whose values are SMALL
    // shortly after boot — a service starting with the machine sees "now" of a few seconds. The
    // first claim must still be allowed however small that is; the deep-negative initial sentinel
    // is what guarantees it, and this pins that against a return to an initial 0.
    [Fact]
    public void FirstClaim_IsAllowed_EvenWhenTheClockStartsNearZero()
    {
        var throttle = new RequestThrottle(() => TimeSpan.FromSeconds(5).Ticks);

        Assert.True(throttle.TryAcquire(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SecondClaimWithinTheInterval_IsRefused()
    {
        var clock = new FakeClock();
        var throttle = new RequestThrottle(clock.Now);

        Assert.True(throttle.TryAcquire(Spacing));

        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(throttle.TryAcquire(Spacing));
    }

    [Fact]
    public void ClaimAfterTheIntervalHasPassed_IsAllowed()
    {
        var clock = new FakeClock();
        var throttle = new RequestThrottle(clock.Now);

        Assert.True(throttle.TryAcquire(Spacing));

        clock.Advance(Spacing);
        Assert.True(throttle.TryAcquire(Spacing));
    }

    // A refused claim must not move the clock forward, or a stream of rejected callers would keep
    // pushing the next allowed one further away and the throttle would never open.
    [Fact]
    public void ARefusedClaim_DoesNotPostponeTheNextAllowedOne()
    {
        var clock = new FakeClock();
        var throttle = new RequestThrottle(clock.Now);

        Assert.True(throttle.TryAcquire(Spacing));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(throttle.TryAcquire(Spacing));   // refused half way through

        clock.Advance(TimeSpan.FromSeconds(10));      // 20s since the SUCCESSFUL claim
        Assert.True(throttle.TryAcquire(Spacing));
    }

    // One instance, two cadences over one clock — the periodic sweep and the on-demand path share a
    // throttle precisely so they cannot double up on the device.
    [Fact]
    public void AClaimAtOneInterval_AlsoSatisfiesALongerOne()
    {
        var clock = new FakeClock();
        var throttle = new RequestThrottle(clock.Now);

        Assert.True(throttle.TryAcquire(TimeSpan.FromSeconds(20)));

        // The slow path asks for 5 minutes and must be told no, because the fast path just asked.
        Assert.False(throttle.TryAcquire(TimeSpan.FromMinutes(5)));
    }

    // NOT COVERED HERE: that concurrent callers cannot all pass at once.
    //
    // That is the defect this type was written for — the read-then-write it replaced let every
    // caller who read the same stale timestamp proceed — but it is not reachable from a black-box
    // test. The window is the two or three instructions between reading the last claim and writing
    // the new one; once any thread writes, the others correctly see the update and are refused. A
    // barrier over the injected clock does not help, because it synchronises the clock read rather
    // than that window, and an attempt at exactly this test passed against a deliberately unsafe
    // implementation before being removed.
    //
    // The guarantee therefore rests on the primitive: TryAcquire claims the slot with
    // Interlocked.CompareExchange, so a caller whose expected value has moved is told no. Forcing
    // the interleave would need a test-only seam inside TryAcquire, which is a worse trade than
    // stating the gap here. The tests above cover the POLICY — intervals, refusal not postponing
    // the next claim, one clock serving two cadences — which is what changes by accident.
}
