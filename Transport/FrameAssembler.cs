using System;
using System.Collections.Generic;

namespace TwinShotNet;

// 由网络轮询线程串行调用；所有待组装帧共享实例的 NACK 发送预算。
public sealed class FrameAssembler
{
    private sealed class Frame
    {
        public byte[][] Parts;
        public int Received;
        public int Size;
        public double Created;
        public double NextRequest;
        public int Attempts;
        public int RequestCursor;
    }

    private readonly SortedDictionary<int, Frame> _frames = new SortedDictionary<int, Frame>();
    private readonly TransportClock _clock = new TransportClock();
    private int _retired = -1;
    private double _nextRequest;
    public int PendingFrames => _frames.Count;

    // Use one monotonic millisecond clock throughout a session. The legacy
    // overload uses Wire.NowMilliseconds; explicit timestamps permit simulation.
    public byte[] Add(int sequence, int index, int total, byte[] payload)
        => Add(sequence, index, total, payload, Wire.NowMilliseconds);

    public byte[] Add(int sequence, int index, int total, byte[] payload, double nowMs)
    {
        Expire(nowMs);
        if (sequence <= _retired || sequence < 0 || total < 1 || total > Wire.MaxChunks || index < 0 ||
            index >= total || payload == null || payload.Length == 0 || payload.Length > Wire.ChunkSize) return null;
        if (!_frames.TryGetValue(sequence, out var frame))
        {
            if (_frames.Count >= Wire.FrameWindow)
            {
                using var e = _frames.GetEnumerator();
                e.MoveNext();
                if (sequence < e.Current.Key) return null;
                RetireThrough(e.Current.Key);
            }

            _frames[sequence] = frame = new Frame
            {
                Parts = new byte[total][], Created = nowMs,
                NextRequest = nowMs + Wire.NackIntervalMs
            };
        }

        if (frame.Parts.Length != total || frame.Parts[index] != null) return null;
        // 复制接收字节取得所有权，调用方复用缓冲区不会改变已保存的分块。
        frame.Parts[index] = (byte[])payload.Clone();
        frame.Received++;
        frame.Size += payload.Length;
        if (frame.Size > Wire.MaxPacked)
        {
            RetireThrough(sequence);
            return null;
        }

        if (frame.Received != total) return null;
        var result = new byte[frame.Size];
        int offset = 0;
        foreach (var part in frame.Parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        RetireThrough(sequence);
        return result;
    }

    public void Expire(double nowMs)
    {
        _clock.Advance(nowMs);
        int through = _retired;
        foreach (var entry in _frames)
            if (nowMs - entry.Value.Created >= Wire.AssemblyLifetimeMs)
                through = Math.Max(through, entry.Key);
        RetireThrough(through);
    }

    private void RetireThrough(int sequence)
    {
        // A permanent high-water mark prevents expired/evicted frames from being
        // resurrected by replay, without an unbounded tombstone collection.
        // 退休水位永久保留；完成、过期或淘汰新帧时，同时丢弃更旧帧，重放不能复活。
        _retired = Math.Max(_retired, sequence);
        var remove = new List<int>();
        foreach (var key in _frames.Keys)
            if (key <= _retired)
                remove.Add(key);
        foreach (var key in remove) _frames.Remove(key);
    }

    public MissingChunks GetMissingRequest(double nowMs)
    {
        Expire(nowMs);
        if (nowMs < _nextRequest) return null;
        Frame candidate = null;
        int sequence = -1;
        foreach (var entry in _frames)
        {
            var frame = entry.Value;
            if (frame.Attempts >= Wire.MaxNackAttempts || nowMs < frame.NextRequest) continue;
            // Favor recent snapshots; never wait for an older frame to finish.
            candidate = frame;
            sequence = entry.Key;
        }

        if (candidate == null) return null;
        var indices = new List<int>();
        int total = candidate.Parts.Length;
        int scanned = 0;
        while (scanned < total && indices.Count < Wire.MaxRepairChunks)
        {
            int index = (candidate.RequestCursor + scanned++) % total;
            if (candidate.Parts[index] == null) indices.Add(index);
        }

        candidate.RequestCursor = (candidate.RequestCursor + scanned) % total;
        indices.Sort();
        candidate.Attempts++;
        candidate.NextRequest = nowMs + Wire.RetryIntervalMs;
        _nextRequest = nowMs + Wire.NackIntervalMs;
        return new MissingChunks(sequence, indices.ToArray());
    }
}