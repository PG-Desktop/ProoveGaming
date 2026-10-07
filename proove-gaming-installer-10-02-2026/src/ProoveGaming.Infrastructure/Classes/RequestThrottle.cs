namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// Bounds how often something may be done, by claiming a time slot atomically.
/// </summary>
/// <remarks>
/// <para>
/// Written for device traffic: a battery request is a radio frame at a battery-powered mouse, so
/// "how recently did we last ask" has to be answered once and acted on once. The obvious
/// read-then-write —
/// </para>
/// <code>
/// if (DateTime.UtcNow - _last &lt; spacing) return;
/// _last = DateTime.UtcNow;
/// </code>
/// <para>
/// is check-then-act: two callers can both read the old value, both decide they are allowed, and
/// both proceed. That is reachable whenever more than one card is on screen — the App fires a
/// battery acquire per card, so several gRPC calls land at once — and it defeats the throttle in
/// exactly the case it exists for. <see cref="TryAcquire"/> claims the slot with a compare-exchange
/// instead, so the loser is told no.
/// </para>
/// <para>
/// The interval is per call rather than fixed at construction, so one instance can serve several
/// cadences over the same clock — a slow periodic sweep and a faster on-demand path that must not
/// double up with it.
/// </para>
/// <para>
/// The clock is injectable purely so tests need not sleep.
/// </para>
/// </remarks>
public sealed class RequestThrottle
{
    private readonly Func<long> _nowTicks;

    // Ticks, not DateTime: Interlocked works on long, and DateTime is not guaranteed to be read or
    // written atomically. Starts at a deep-negative sentinel so the first call is always allowed
    // whatever the clock's magnitude — the monotonic default below is SMALL shortly after boot,
    // so an initial 0 would keep the first interval closed on a service that starts with the
    // machine. MinValue/2 rather than MinValue, so `now - last` cannot overflow.
    private long _lastTicks = long.MinValue / 2;

    // Environment.TickCount64, not DateTime.UtcNow: the wall clock is not monotonic — an NTP
    // correction, a wake from sleep or a manual adjustment can step it BACKWARDS, turning
    // `now - last` negative and closing the throttle until the wall clock catches back up to the
    // stored claim. Uptime only moves forward, and millisecond resolution is plenty for the
    // second-scale intervals this serves.
    public RequestThrottle(Func<long>? nowTicks = null) =>
        _nowTicks = nowTicks ?? (() => Environment.TickCount64 * TimeSpan.TicksPerMillisecond);

    /// <summary>
    /// Claims the next slot if <paramref name="minInterval"/> has passed since the last successful
    /// claim. Returns false without side effects otherwise.
    /// </summary>
    public bool TryAcquire(TimeSpan minInterval)
    {
        var now = _nowTicks();
        var last = Interlocked.Read(ref _lastTicks);

        if (now - last < minInterval.Ticks) return false;

        // Whoever wins the exchange owns the slot; a concurrent caller that read the same `last`
        // fails here and is turned away rather than proceeding on a stale decision.
        return Interlocked.CompareExchange(ref _lastTicks, now, last) == last;
    }
}
