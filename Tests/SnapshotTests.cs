using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TwinShotNet;

public static class SnapshotTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Snapshot: " + name);
            checks++;
        }
        void Reject(byte[] bytes, string name)
        {
            try { SnapshotCodec.Decode(bytes); }
            catch (InvalidDataException) { checks++; return; }
            throw new Exception("Snapshot accepted: " + name);
        }

        var offsets = new Dictionary<string, int>();
        byte[] packet = Packet(offsets);
        byte[] Replace(string field, byte[] value)
        {
            byte[] changed = (byte[])packet.Clone();
            Buffer.BlockCopy(value, 0, changed, offsets[field], value.Length);
            return changed;
        }

        Snapshot decoded = SnapshotCodec.Decode(packet);
        Check(decoded.Level == -17 && decoded.CameraX == -123.5f && decoded.CameraY == 456.25f && decoded.CameraSize == 300,
            "legacy camera and level layout");
        SnapshotPlayer p = decoded.Players[0];
        Check(decoded.Players.Length == 1 && p.Number == -7 && p.Hits == int.MinValue && p.Score == int.MaxValue && p.Alive && p.Powerup == 255,
            "player fields and unknown enum values preserved");
        SnapshotVisual v = decoded.Visuals[0];
        Check(decoded.Visuals.Length == 1 && v.Id == -123 && v.Sprite == "sprite" && v.Layer == "Default" && v.Order == -12345,
            "visual identity and sorting layout");
        Check(v.X == 1.25f && v.Y == -2.5f && v.Z == 3.75f && v.ScaleX == -1 && v.ScaleY == 0 && v.ScaleZ == 2 && v.Rotation == 359,
            "all transform fields, including negative and zero scale");
        Check(v.R == 1 && v.G == 23 && v.B == 145 && v.A == 255 && v.FlipX && !v.FlipY, "color and flip layout");

        foreach (string field in new[] { "cameraX", "cameraY", "cameraSize", "x", "y", "z", "scaleX", "scaleY", "scaleZ", "rotation" })
        {
            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1000000f, -1000000f, float.MaxValue })
                Reject(Replace(field, BitConverter.GetBytes(bad)), field + " rejects " + bad);
            foreach (float valid in new[] { -999999f, 0f, 999999f })
            {
                if (field == "cameraSize" && valid <= 0) continue;
                Check(SnapshotCodec.Decode(Replace(field, BitConverter.GetBytes(valid))) != null, field + " accepts finite boundary");
            }
        }
        foreach (float size in new[] { 0f, -0f, -1f }) Reject(Replace("cameraSize", BitConverter.GetBytes(size)), "nonpositive camera size");
        foreach (string field in new[] { "alive", "flipX", "flipY" })
            foreach (byte bad in new byte[] { 2, 255 }) Reject(Replace(field, new[] { bad }), field + " boolean");
        foreach (byte count in new byte[] { 0, 5, 255 }) Reject(Replace("players", new[] { count }), "player count");
        foreach (int count in new[] { -1, 10001, int.MaxValue }) Reject(Replace("visuals", BitConverter.GetBytes(count)), "visual count");
        Reject(Replace("visuals", BitConverter.GetBytes(2)), "count exceeds remaining payload");
        Reject(Packet(new Dictionary<string, int>(), players: 2, duplicatePlayers: true), "duplicate players");
        Reject(Packet(new Dictionary<string, int>(), visuals: 2, duplicateVisuals: true), "duplicate visuals");
        Check(SnapshotCodec.Decode(Packet(new Dictionary<string, int>(), players: 4, visuals: 10000)).Visuals.Length == 10000,
            "maximum entity counts");
        Check(SnapshotCodec.Decode(Packet(new Dictionary<string, int>(), visuals: 0)).Visuals.Length == 0, "empty visual frame");

        foreach (string field in new[] { "sprite", "layer" })
        {
            Reject(Replace(field, new byte[] { 255, 255, 255, 255, 7 }), field + " huge length");
            Reject(Replace(field, new byte[] { 128, 128, 128, 128, 128 }), field + " unterminated prefix");
            Reject(Replace(field, new byte[] { 255, 255, 255, 255, 8 }), field + " overflowing prefix");
            Reject(Replace(field, new byte[] { 1, 255 }), field + " invalid UTF-8");
            Reject(Replace(field, new byte[] { 1, 194 }), field + " truncated UTF-8 sequence");
            int limit = field == "sprite" ? 512 : 128;
            foreach (char character in new[] { 'a', '\u754c' })
            {
                string valid = new string(character, limit);
                byte[] validPacket = Packet(new Dictionary<string, int>(), sprite: field == "sprite" ? valid : "sprite", layer: field == "layer" ? valid : "Default");
                SnapshotVisual result = SnapshotCodec.Decode(validPacket).Visuals[0];
                Check((field == "sprite" ? result.Sprite : result.Layer) == valid, field + " maximum string");
                string invalid = valid + character;
                Reject(Packet(new Dictionary<string, int>(), sprite: field == "sprite" ? invalid : "sprite", layer: field == "layer" ? invalid : "Default"), field + " oversized string");
            }
        }
        for (int length = 0; length < packet.Length; length++) Reject(packet.Take(length).ToArray(), "truncation at " + length);
        Reject(packet.Concat(new byte[] { 0 }).ToArray(), "trailing byte");
        Reject(null, "null input");
        Reject(new byte[SnapshotCodec.MaxBytes + 1], "total byte limit");
        Check(packet.SequenceEqual(Packet(new Dictionary<string, int>())), "decoding does not mutate input");
        Check(SnapshotCodec.Decode(packet).Visuals[0].Id == -123, "valid decode after rejected packets");
        Console.WriteLine($"Snapshot: {checks} checks passed");
    }

    // Independent legacy writer fixture: no production encode path can mask decode mistakes.
    private static byte[] Packet(Dictionary<string, int> offsets, int players = 1, int visuals = 1,
        bool duplicatePlayers = false, bool duplicateVisuals = false, string sprite = "sprite", string layer = "Default")
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Mark(string field) { offsets[field] = (int)stream.Position; }
        writer.Write(-17);
        Mark("cameraX"); writer.Write(-123.5f);
        Mark("cameraY"); writer.Write(456.25f);
        Mark("cameraSize"); writer.Write(300f);
        Mark("players"); writer.Write((byte)players);
        for (int i = 0; i < players; i++)
        {
            writer.Write(duplicatePlayers ? -7 : -7 + i); writer.Write(int.MinValue); writer.Write(int.MaxValue);
            Mark("alive"); writer.Write(true); writer.Write((byte)255);
        }
        Mark("visuals"); writer.Write(visuals);
        for (int i = 0; i < visuals; i++)
        {
            writer.Write(duplicateVisuals ? -123 : -123 + i);
            Mark("sprite"); writer.Write(sprite);
            Mark("x"); writer.Write(1.25f); Mark("y"); writer.Write(-2.5f); Mark("z"); writer.Write(3.75f);
            Mark("scaleX"); writer.Write(-1f); Mark("scaleY"); writer.Write(0f); Mark("scaleZ"); writer.Write(2f);
            Mark("rotation"); writer.Write(359f);
            writer.Write((byte)1); writer.Write((byte)23); writer.Write((byte)145); writer.Write((byte)255);
            Mark("layer"); writer.Write(layer); writer.Write(-12345);
            Mark("flipX"); writer.Write(true); Mark("flipY"); writer.Write(false);
        }
        return stream.ToArray();
    }
}
