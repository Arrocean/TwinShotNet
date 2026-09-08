using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace TwinShotNet;

public static class Wire
{
    public const string Version = "TwinShotNet-0.4.0";
    public const int ChunkSize = 900;
    public const int MaxPacked = 512 * 1024;
    public const int MaxRaw = 2 * 1024 * 1024;
    public const int MaxChunks = (MaxPacked + ChunkSize - 1) / ChunkSize;
    public const int FrameWindow = 32;
    public const double AssemblyLifetimeMs = 600;
    public const double CacheLifetimeMs = 800;
    public const double NackIntervalMs = 50;
    public const double RetryIntervalMs = 100;
    public const int MaxNackAttempts = 4;
    public const int MaxRepairChunks = 32;
    public const byte NackPacketType = 7;
    public static double NowMilliseconds => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);

    // NACK body, after packet type 7: little-endian int32 sequence,
    // uint16 count, then count strictly increasing uint16 chunk indices.
    public static byte[] EncodeNack(MissingChunks request)
    {
        if (request == null || !ValidNack(request.Sequence, request.Indices, MaxChunks))
            throw new ArgumentException("Invalid NACK", nameof(request));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(request.Sequence);
        writer.Write((ushort)request.Indices.Length);
        foreach (int index in request.Indices) writer.Write((ushort)index);
        return stream.ToArray();
    }

    public static bool TryDecodeNack(byte[] body, out MissingChunks request)
    {
        request = null;
        if (body == null || body.Length < 8 || body.Length > 6 + 2 * MaxRepairChunks) return false;
        using var reader = new BinaryReader(new MemoryStream(body));
        int sequence = reader.ReadInt32();
        int count = reader.ReadUInt16();
        if (count < 1 || count > MaxRepairChunks || body.Length != 6 + count * 2) return false;
        var indices = new int[count];
        for (int i = 0; i < count; i++) indices[i] = reader.ReadUInt16();
        if (!ValidNack(sequence, indices, MaxChunks)) return false;
        request = new MissingChunks(sequence, indices);
        return true;
    }

    internal static bool ValidNack(int sequence, int[] indices, int total)
    {
        if (sequence < 0 || indices == null || indices.Length < 1 || indices.Length > MaxRepairChunks) return false;
        int previous = -1;
        foreach (int index in indices)
        {
            if (index <= previous || index >= total) return false;
            previous = index;
        }
        return true;
    }
    public static byte[] Pack(byte[] raw)
    {
        if (raw.Length > MaxRaw) throw new InvalidDataException("Snapshot too large");
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(raw, 0, raw.Length);
        if (output.Length > MaxPacked) throw new InvalidDataException("Compressed snapshot too large");
        return output.ToArray();
    }
    public static byte[] Unpack(byte[] packed)
    {
        using var source = new MemoryStream(packed);
        using var zip = new DeflateStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = zip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + count > MaxRaw) throw new InvalidDataException("Snapshot exceeds limit");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}

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
    private readonly SortedDictionary<int, Frame> frames = new SortedDictionary<int, Frame>();
    private readonly TransportClock clock = new TransportClock();
    private int retired = -1;
    private double nextRequest;
    public int PendingFrames => frames.Count;

    // Use one monotonic millisecond clock throughout a session. The legacy
    // overload uses Wire.NowMilliseconds; explicit timestamps permit simulation.
    public byte[] Add(int sequence, int index, int total, byte[] payload)
        => Add(sequence, index, total, payload, Wire.NowMilliseconds);

    public byte[] Add(int sequence, int index, int total, byte[] payload, double nowMs)
    {
        Expire(nowMs);
        if (sequence <= retired || sequence < 0 || total < 1 || total > Wire.MaxChunks || index < 0 || index >= total || payload == null || payload.Length == 0 || payload.Length > Wire.ChunkSize) return null;
        if (!frames.TryGetValue(sequence, out var frame))
        {
            if (frames.Count >= Wire.FrameWindow)
            {
                using var e = frames.GetEnumerator(); e.MoveNext();
                if (sequence < e.Current.Key) return null;
                RetireThrough(e.Current.Key);
            }
            frames[sequence] = frame = new Frame
            {
                Parts = new byte[total][], Created = nowMs,
                NextRequest = nowMs + Wire.NackIntervalMs
            };
        }
        if (frame.Parts.Length != total || frame.Parts[index] != null) return null;
        frame.Parts[index] = (byte[])payload.Clone();
        frame.Received++;
        frame.Size += payload.Length;
        if (frame.Size > Wire.MaxPacked) { RetireThrough(sequence); return null; }
        if (frame.Received != total) return null;
        var result = new byte[frame.Size];
        int offset = 0;
        foreach (var part in frame.Parts) { Buffer.BlockCopy(part, 0, result, offset, part.Length); offset += part.Length; }
        RetireThrough(sequence);
        return result;
    }

    public void Expire(double nowMs)
    {
        clock.Advance(nowMs);
        int through = retired;
        foreach (var entry in frames)
            if (nowMs - entry.Value.Created >= Wire.AssemblyLifetimeMs) through = Math.Max(through, entry.Key);
        RetireThrough(through);
    }

    private void RetireThrough(int sequence)
    {
        // A permanent high-water mark prevents expired/evicted frames from being
        // resurrected by replay, without an unbounded tombstone collection.
        retired = Math.Max(retired, sequence);
        var remove = new List<int>();
        foreach (var key in frames.Keys) if (key <= retired) remove.Add(key);
        foreach (var key in remove) frames.Remove(key);
    }

    public MissingChunks GetMissingRequest(double nowMs)
    {
        Expire(nowMs);
        if (nowMs < nextRequest) return null;
        Frame candidate = null;
        int sequence = -1;
        foreach (var entry in frames)
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
        nextRequest = nowMs + Wire.NackIntervalMs;
        return new MissingChunks(sequence, indices.ToArray());
    }
}

public sealed class MissingChunks
{
    public int Sequence { get; }
    public int[] Indices { get; }
    public MissingChunks(int sequence, int[] indices) { Sequence = sequence; Indices = indices; }
}

public sealed class SnapshotChunk
{
    public int Sequence { get; }
    public int Index { get; }
    public int Total { get; }
    public byte[] Payload { get; }
    internal SnapshotChunk(int sequence, int index, int total, byte[] payload)
    { Sequence = sequence; Index = index; Total = total; Payload = payload; }
}

// One cache per host session, shared by all authenticated peers. Its repair
// budget is aggregate, so adding peers cannot multiply retransmit bandwidth.
// All APIs are single-threaded, like the network polling loop.
public sealed class FrameCache
{
    private sealed class Frame
    {
        public byte[] Packed;
        public double Created;
    }
    private readonly SortedDictionary<int, Frame> frames = new SortedDictionary<int, Frame>();
    private readonly TransportClock clock = new TransportClock();
    private int latest = -1;
    private double nextService;
    public int Count => frames.Count;

    // Sequences must increase for the whole session, including after expiration.
    // Duplicate Store calls never replace bytes or refresh the creation time.
    public bool Store(int sequence, byte[] packed, double nowMs)
    {
        Expire(nowMs);
        if (sequence <= latest || sequence < 0 || packed == null || packed.Length < 1 || packed.Length > Wire.MaxPacked) return false;
        if (frames.Count >= Wire.FrameWindow)
        {
            using var e = frames.GetEnumerator(); e.MoveNext();
            frames.Remove(e.Current.Key);
        }
        latest = sequence;
        frames.Add(sequence, new Frame { Packed = (byte[])packed.Clone(), Created = nowMs });
        return true;
    }

    public void Expire(double nowMs)
    {
        clock.Advance(nowMs);
        var remove = new List<int>();
        foreach (var entry in frames)
            if (nowMs - entry.Value.Created >= Wire.CacheLifetimeMs) remove.Add(entry.Key);
        foreach (int sequence in remove) frames.Remove(sequence);
    }

    // Returns at most 32 chunks per 50 ms across ALL requests. There is no send
    // queue: denied requests are dropped and receivers may retry within their TTL.
    public SnapshotChunk[] Serve(MissingChunks request, double nowMs)
    {
        Expire(nowMs);
        if (nowMs < nextService || request == null || !frames.TryGetValue(request.Sequence, out var frame))
            return Array.Empty<SnapshotChunk>();
        int total = (frame.Packed.Length + Wire.ChunkSize - 1) / Wire.ChunkSize;
        if (!Wire.ValidNack(request.Sequence, request.Indices, total)) return Array.Empty<SnapshotChunk>();
        nextService = nowMs + Wire.NackIntervalMs;
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

internal sealed class TransportClock
{
    private double previous;
    public void Advance(double nowMs)
    {
        if (double.IsNaN(nowMs) || double.IsInfinity(nowMs) || nowMs < previous)
            throw new ArgumentOutOfRangeException(nameof(nowMs), "Use nonnegative monotonic milliseconds");
        previous = nowMs;
    }
}
