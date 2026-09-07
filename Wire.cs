using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace TwinShotNet;

public static class Wire
{
    public const string Version = "TwinShotNet-0.2.0";
    public const int ChunkSize = 900;
    public const int MaxPacked = 512 * 1024;
    public const int MaxRaw = 2 * 1024 * 1024;
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
    }
    private readonly SortedDictionary<int, Frame> frames = new SortedDictionary<int, Frame>();
    private int completed = -1;
    public byte[] Add(int sequence, int index, int total, byte[] payload)
    {
        if (sequence <= completed || sequence < 0 || total < 1 || total > (Wire.MaxPacked + Wire.ChunkSize - 1) / Wire.ChunkSize || index < 0 || index >= total || payload.Length == 0 || payload.Length > Wire.ChunkSize) return null;
        if (!frames.TryGetValue(sequence, out var frame))
        {
            // Retain a few in-flight frames, so a reordered chunk cannot destroy a newer frame.
            if (frames.Count >= 4)
            {
                using var e = frames.GetEnumerator(); e.MoveNext();
                if (sequence < e.Current.Key) return null;
                frames.Remove(e.Current.Key);
            }
            frames[sequence] = frame = new Frame { Parts = new byte[total][] };
        }
        if (frame.Parts.Length != total || frame.Parts[index] != null) return null;
        frame.Parts[index] = payload;
        frame.Received++;
        frame.Size += payload.Length;
        if (frame.Size > Wire.MaxPacked) { frames.Remove(sequence); return null; }
        if (frame.Received != total) return null;
        var result = new byte[frame.Size];
        int offset = 0;
        foreach (var part in frame.Parts) { Buffer.BlockCopy(part, 0, result, offset, part.Length); offset += part.Length; }
        completed = sequence;
        var remove = new List<int>();
        foreach (var key in frames.Keys) if (key <= completed) remove.Add(key);
        foreach (var key in remove) frames.Remove(key);
        return result;
    }
}
