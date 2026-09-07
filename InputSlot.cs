using System.Collections.Generic;

namespace TwinShotNet;

// Queue transitions, not only the latest state: a press/release within one tick must survive.
public sealed class InputSlot
{
    private readonly Queue<byte> pending = new Queue<byte>();
    public byte Held { get; private set; }
    private byte lastReceived;
    public void Receive(byte value)
    {
        value &= 63;
        if (value == lastReceived) return;
        lastReceived = value;
        if (pending.Count >= 120) { Clear(); return; }
        pending.Enqueue(value);
    }
    public byte Advance()
    {
        if (pending.Count != 0) Held = pending.Dequeue();
        return Held;
    }
    public void Clear() { pending.Clear(); Held = lastReceived = 0; }
}
