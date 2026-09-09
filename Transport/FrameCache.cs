using System;
using System.Collections.Generic;

namespace TwinShotNet;

// 每个主机会话只持有一个缓存，所有已认证对端共享修复预算；所有 API 单线程调用。
public sealed class FrameCache
{
    private sealed class Frame
    {
        public byte[] Packed;
        public double Created;
    }

    private readonly SortedDictionary<int, Frame> _frames = new SortedDictionary<int, Frame>();
    private readonly TransportClock _clock = new TransportClock();
    private int _latest = -1;
    private double _nextService;
    public int Count => _frames.Count;

    // Sequences must increase for the whole session, including after expiration.
    // Duplicate Store calls never replace bytes or refresh the creation time.
    // 序号跨过期仍须递增；重复或旧帧不替换字节、不刷新 TTL，保存副本归缓存所有。
    public bool Store(int sequence, byte[] packed, double nowMs)
    {
        Expire(nowMs);
        if (sequence <= _latest || sequence < 0 || packed == null || packed.Length < 1 ||
            packed.Length > Wire.MaxPacked) return false;
        if (_frames.Count >= Wire.FrameWindow)
        {
            using var e = _frames.GetEnumerator();
            e.MoveNext();
            _frames.Remove(e.Current.Key);
        }

        _latest = sequence;
        _frames.Add(sequence, new Frame { Packed = (byte[])packed.Clone(), Created = nowMs });
        return true;
    }

    public void Expire(double nowMs)
    {
        _clock.Advance(nowMs);
        var remove = new List<int>();
        foreach (var entry in _frames)
            if (nowMs - entry.Value.Created >= Wire.CacheLifetimeMs)
                remove.Add(entry.Key);
        foreach (int sequence in remove) _frames.Remove(sequence);
    }

    // Returns at most 32 chunks per 50 ms across ALL requests. There is no send
    // queue: denied requests are dropped and receivers may retry within their TTL.
    public SnapshotChunk[] Serve(MissingChunks request, double nowMs)
    {
        Expire(nowMs);
        if (nowMs < _nextService || request == null || !_frames.TryGetValue(request.Sequence, out var frame))
            return Array.Empty<SnapshotChunk>();
        int total = (frame.Packed.Length + Wire.ChunkSize - 1) / Wire.ChunkSize;
        if (!Wire.ValidNack(request.Sequence, request.Indices, total)) return Array.Empty<SnapshotChunk>();
        _nextService = nowMs + Wire.NackIntervalMs;
        var result = new SnapshotChunk[request.Indices.Length];
        for (int i = 0; i < result.Length; i++)
        {
            int index = request.Indices[i];
            int offset = index * Wire.ChunkSize;
            var payload = new byte[Math.Min(Wire.ChunkSize, frame.Packed.Length - offset)];
            Buffer.BlockCopy(frame.Packed, offset, payload, 0, payload.Length);
            result[i] = new SnapshotChunk(request.Sequence, index, total, payload);
        }

        return result;
    }
}