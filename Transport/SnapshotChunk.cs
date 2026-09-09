namespace TwinShotNet;

// Payload 直接持有传入数组；缓存生成修复块时分配独立数组，不暴露缓存内部字节。
public sealed class SnapshotChunk
{
    public int Sequence { get; }
    public int Index { get; }
    public int Total { get; }
    public byte[] Payload { get; }

    internal SnapshotChunk(int sequence, int index, int total, byte[] payload)
    {
        Sequence = sequence;
        Index = index;
        Total = total;
        Payload = payload;
    }
}