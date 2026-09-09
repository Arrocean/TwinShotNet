using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace TwinShotNet;

public static class Wire
{
    public const string Version = "TwinShotNet-0.5.0";
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
        if (raw == null) throw new ArgumentNullException(nameof(raw));
        if (raw.Length > MaxRaw) throw new InvalidDataException("Snapshot too large");
        using var output = new MemoryStream();
        using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(raw, 0, raw.Length);
        if (output.Length > MaxPacked) throw new InvalidDataException("Compressed snapshot too large");
        return output.ToArray();
    }

    public static byte[] Unpack(byte[] packed)
    {
        if (packed == null) throw new ArgumentNullException(nameof(packed));
        if (packed.Length < 1 || packed.Length > MaxPacked)
            throw new InvalidDataException("Invalid compressed snapshot size");
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