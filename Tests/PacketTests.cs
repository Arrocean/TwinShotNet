using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using TwinShotNet;

static class PacketTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            checks++;
        }
        void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        Reject<ArgumentNullException>(() => Wire.Pack(null));
        Reject<ArgumentNullException>(() => Wire.Unpack(null));
        Reject<InvalidDataException>(() => Wire.Pack(new byte[Wire.MaxRaw + 1]));
        Reject<InvalidDataException>(() => Wire.Unpack(Array.Empty<byte>()));
        Reject<InvalidDataException>(() => Wire.Unpack(new byte[Wire.MaxPacked + 1]));
        Check(Wire.Unpack(Wire.Pack(Array.Empty<byte>())).Length == 0, "Empty raw round trip");
        Check(Wire.Unpack(Wire.Pack(new byte[Wire.MaxRaw])).Length == Wire.MaxRaw, "Raw size boundary");
        using (var stream = new MemoryStream())
        {
            using (var zip = new DeflateStream(stream, CompressionLevel.Fastest, true))
                zip.Write(new byte[Wire.MaxRaw + 1]);
            Reject<InvalidDataException>(() => Wire.Unpack(stream.ToArray()));
        }
        var cases = new (byte type, byte[] body)[] {
            (1, new byte[] { 2 }), (2, new byte[] { 63 }),
            (4, new byte[] { 1, 0, 0, 0, 0, 0, 1, 0, 42 }),
            (5, new byte[] { 3, 1 }), (6, new byte[12]),
            (7, Wire.EncodeNack(new MissingChunks(1, new[] { 0 }))),
            (8, new byte[] { 0, 1, 0, 0, 0, 2, 2, 0, 1 }),
            (9, Array.Empty<byte>()), (10, new byte[] { 42, 0, 0, 0 })
        };
        foreach (var packet in cases)
        {
            Check(PacketValidation.IsValid(packet.type, packet.body), "Valid type " + packet.type);
            Check(!PacketValidation.IsValid(packet.type, null), "Null body");
            for (int length = 0; length < packet.body.Length; length++)
                Check(!PacketValidation.IsValid(packet.type, packet.body.Take(length).ToArray()), "Truncated type " + packet.type);
            if (packet.type != 4)
                Check(!PacketValidation.IsValid(packet.type, packet.body.Concat(new byte[] { 0 }).ToArray()), "Trailing body");
        }
        Check(!PacketValidation.IsValid(1, new byte[] { 1 }), "Invalid slot");
        Check(!PacketValidation.IsValid(5, new byte[] { 4, 1 }), "Invalid skin");
        Check(!PacketValidation.IsValid(5, new byte[] { 0, 2 }), "Invalid boolean");
        for (int i = 0; i < 12; i++)
        {
            var lobby = new byte[12]; lobby[i] = 255;
            Check(!PacketValidation.IsValid(6, lobby), "Invalid lobby field");
        }
        foreach (int index in new[] { 5, 6, 7, 8 })
        {
            var match = (byte[])cases[6].body.Clone(); match[index] = 255;
            Check(!PacketValidation.IsValid(8, match), "Invalid match field");
        }
        Check(!PacketValidation.IsValid(10, BitConverter.GetBytes(-1)), "Negative coins");
        Check(!PacketValidation.IsValid(3, Array.Empty<byte>()), "Obsolete packet");
        Check(!PacketValidation.IsValid(4, new byte[Wire.ChunkSize + 9]), "Oversized chunk");
        var random = new Random(17);
        for (int i = 0; i < 1000; i++)
        {
            var bytes = new byte[random.Next(1024)]; random.NextBytes(bytes);
            PacketValidation.IsValid((byte)random.Next(12), bytes);
        }
        Check(true, "Malformed packet fuzz does not throw");
        Console.WriteLine($"Packet boundaries: {checks} checks passed");
    }
}
