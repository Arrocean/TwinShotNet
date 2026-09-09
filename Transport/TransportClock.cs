using System;

namespace TwinShotNet;

// 同一实例的所有调用使用非负、有限且单调不减的毫秒时间，过期与限流共享此约束。
internal sealed class TransportClock
{
    private double _previous;

    public void Advance(double nowMs)
    {
        if (double.IsNaN(nowMs) || double.IsInfinity(nowMs) || nowMs < _previous)
            throw new ArgumentOutOfRangeException(nameof(nowMs), "Use nonnegative monotonic milliseconds");
        _previous = nowMs;
    }
}