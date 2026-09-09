using System.Collections.Generic;

namespace TwinShotNet;

// Queue transitions, not only the latest state: a press/release within one tick must survive.
public sealed class InputSlot
{
    private readonly Queue<byte> _pending = new Queue<byte>();
    public byte Held { get; private set; }
    private byte _lastReceived;

    public void Receive(byte value)
    {
        value &= 63;
        if (value == _lastReceived) return;
        _lastReceived = value;
        if (_pending.Count >= 120)
        {
            Clear();
            return;
        }

        _pending.Enqueue(value);
    }

    public byte Advance()
    {
        if (_pending.Count != 0) Held = _pending.Dequeue();
        return Held;
    }

    public void Clear()
    {
        _pending.Clear();
        Held = _lastReceived = 0;
    }
}