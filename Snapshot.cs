using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TwinShotNet;

public sealed class Snapshot
{
    public int Level;
    public float CameraX, CameraY, CameraSize;
    public SnapshotPlayer[] Players;
    public SnapshotVisual[] Visuals;
}

public struct SnapshotPlayer
{
    public int Number, Hits, Score;
    public bool Alive;
    public byte Powerup;
}

public struct SnapshotVisual
{
    public int Id;
    public string Sprite;
    public float X, Y, Z, ScaleX, ScaleY, ScaleZ, Rotation;
    public byte R, G, B, A;
    public string Layer;
    public int Order;
    public bool FlipX, FlipY;
}

// Matches Replica.Capture's BinaryWriter layout, without any Unity dependencies.
public static class SnapshotCodec
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxPlayers = 4;
    public const int MaxVisuals = 10000;
    public const int MaxSpriteLength = 512;
    public const int MaxLayerLength = 128;
    public const float CoordinateLimit = 1000000f;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    // A malformed packet always fails before a caller receives any state to apply.
    public static Snapshot Decode(byte[] raw)
    {
        if (raw == null || raw.Length > MaxBytes) throw new InvalidDataException("Invalid snapshot size");
        try
        {
            using var stream = new MemoryStream(raw, false);
            using var reader = new BinaryReader(stream, Utf8);
            var snapshot = new Snapshot
            {
                Level = reader.ReadInt32(),
                CameraX = ReadFloat(reader), CameraY = ReadFloat(reader), CameraSize = ReadFloat(reader)
            };
            if (snapshot.CameraSize <= 0) throw new InvalidDataException("Invalid camera size");
            int playerCount = reader.ReadByte();
            if (playerCount < 1 || playerCount > MaxPlayers) throw new InvalidDataException("Invalid player count");
            snapshot.Players = new SnapshotPlayer[playerCount];
            var playerIds = new HashSet<int>();
            for (int i = 0; i < playerCount; i++)
            {
                var player = new SnapshotPlayer
                {
                    Number = reader.ReadInt32(), Hits = reader.ReadInt32(), Score = reader.ReadInt32(),
                    Alive = ReadBoolean(reader), Powerup = reader.ReadByte()
                };
                if (!playerIds.Add(player.Number)) throw new InvalidDataException("Duplicate player number");
                snapshot.Players[i] = player;
            }
            int count = reader.ReadInt32();
            // Even an empty-string visual occupies 44 bytes in the existing layout.
            if (count < 0 || count > MaxVisuals || count > (stream.Length - stream.Position) / 44)
                throw new InvalidDataException("Invalid visual count");
            snapshot.Visuals = new SnapshotVisual[count];
            var visualIds = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                var visual = new SnapshotVisual
                {
                    Id = reader.ReadInt32(), Sprite = ReadString(reader, MaxSpriteLength),
                    X = ReadFloat(reader), Y = ReadFloat(reader), Z = ReadFloat(reader),
                    ScaleX = ReadFloat(reader), ScaleY = ReadFloat(reader), ScaleZ = ReadFloat(reader),
                    Rotation = ReadFloat(reader),
                    R = reader.ReadByte(), G = reader.ReadByte(), B = reader.ReadByte(), A = reader.ReadByte(),
                    Layer = ReadString(reader, MaxLayerLength), Order = reader.ReadInt32(),
                    FlipX = ReadBoolean(reader), FlipY = ReadBoolean(reader)
                };
                if (!visualIds.Add(visual.Id)) throw new InvalidDataException("Duplicate visual ID");
                snapshot.Visuals[i] = visual;
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing snapshot data");
            return snapshot;
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated snapshot", ex); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid snapshot UTF-8", ex); }
    }

    private static float ReadFloat(BinaryReader reader)
    {
        float value = reader.ReadSingle();
        if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) >= CoordinateLimit)
            throw new InvalidDataException("Invalid snapshot coordinate");
        return value;
    }

    private static bool ReadBoolean(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        if (value > 1) throw new InvalidDataException("Invalid snapshot boolean");
        return value != 0;
    }

    private static string ReadString(BinaryReader reader, int maxLength)
    {
        uint length = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte part = reader.ReadByte();
            if (shift == 28 && part > 7) throw new InvalidDataException("Invalid string length prefix");
            length |= (uint)(part & 127) << shift;
            if ((part & 128) == 0) break;
        }
        // Bound allocation before reading bytes, then bound UTF-16 length as before.
        if (length > maxLength * 3 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid snapshot string length");
        string value = Utf8.GetString(reader.ReadBytes((int)length));
        if (value.Length > maxLength) throw new InvalidDataException("Snapshot string too long");
        return value;
    }
}
