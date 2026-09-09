namespace TwinShotNet;

// 保留传入索引数组的引用；构造器不复制数组，调用方负责其后续修改。
public sealed class MissingChunks
{
    public int Sequence { get; }
    public int[] Indices { get; }

    public MissingChunks(int sequence, int[] indices)
    {
        Sequence = sequence;
        Indices = indices;
    }
}