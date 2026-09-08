using System;
using System.Linq;
using TwinShotNet;

static class Program
{
    static int checks;
    static void Check(bool result, string name)
    {
        if (!result) throw new Exception(name);
        checks++; Console.WriteLine("PASS " + name);
    }
    static void Main()
    {
        SnapshotTests.Run();
        var a = new InputSlot(); var b = new InputSlot();
        a.Receive(16); a.Receive(0);
        Check(a.Advance() == 16 && a.Advance() == 0, "Short tap preserved");
        a.Receive(1); b.Receive(2);
        Check(a.Advance() == 1 && b.Advance() == 2, "Slots independent");
        a.Clear(); Check(a.Advance() == 0 && b.Advance() == 2, "Disconnect releases only owned slot");
        b.Receive(255); Check(b.Advance() == 63, "Unknown input bits removed");
        var data = Enumerable.Range(0, 6000).Select(i => (byte)(i % 251)).ToArray();
        Check(Wire.Unpack(Wire.Pack(data)).SequenceEqual(data), "Compression round trip");
        var assembler = new FrameAssembler();
        Check(assembler.Add(1, 1, 2, new byte[] { 3 }) == null, "Out of order first chunk");
        Check(assembler.Add(1, 1, 2, new byte[] { 3 }) == null, "Duplicate ignored");
        Check(assembler.Add(1, 0, 2, new byte[] { 1, 2 }).SequenceEqual(new byte[] { 1, 2, 3 }), "Reassembly ordered");
        Check(assembler.Add(0, 0, 1, new byte[] { 1 }) == null, "Old frame ignored");
        Check(assembler.Add(2, 0, 9999, new byte[] { 1 }) == null, "Allocation bound");
        Check(assembler.Add(2, 3, 2, new byte[] { 1 }) == null, "Index bound");
        TransportTests();
        Console.WriteLine($"{checks} checks passed");
    }

    static void TransportTests()
    {
        Check(Wire.Version == "TwinShotNet-0.5.0", "Protocol version");
        var raw = new byte[12000];
        new Random(7).NextBytes(raw);
        var packed = Wire.Pack(raw);
        int total = (packed.Length + Wire.ChunkSize - 1) / Wire.ChunkSize;
        byte[] Part(int index) => packed.Skip(index * Wire.ChunkSize).Take(Wire.ChunkSize).ToArray();
        var cache = new FrameCache();
        var a = new FrameAssembler();
        Check(cache.Store(10, packed, 0), "Store full compressed snapshot");
        for (int i = total - 1; i >= 0; i--)
            if (i != 2 && i != 5) a.Add(10, i, total, Part(i), 40);
        Check(a.GetMissingRequest(89) == null, "Reordering grace period");
        var lostRequest = a.GetMissingRequest(90);
        Check(lostRequest.Sequence == 10 && lostRequest.Indices.SequenceEqual(new[] { 2, 5 }), "Only missing indices requested");
        Check(a.GetMissingRequest(140) == null, "Per-frame retry interval");
        var retry = a.GetMissingRequest(190);
        Check(retry.Indices.SequenceEqual(lostRequest.Indices), "Lost NACK retried");
        var repairs = cache.Serve(retry, 230);
        Check(repairs.Length == 2 && repairs[0].Index == 2 && repairs[1].Index == 5, "Host serves requested sequence and indices");
        a.Add(10, repairs[1].Index, total, repairs[1].Payload, 270);
        Check(a.GetMissingRequest(290).Indices.SequenceEqual(new[] { 2 }), "Lost repair requested again");
        var finalRepair = cache.Serve(new MissingChunks(10, new[] { 2 }), 330)[0];
        var complete = a.Add(10, 2, total, finalRepair.Payload, 370);
        Check(Wire.Unpack(complete).SequenceEqual(raw), "Loss and retransmit compression round trip");
        Check(a.GetMissingRequest(400) == null && a.PendingFrames == 0, "Completed frame stops NACKs");

        a = new FrameAssembler();
        a.Add(1, 0, 2, new byte[] { 1 }, 0);
        for (int s = 2; s <= 10; s++) a.Add(s, 1, 2, new byte[] { (byte)s }, s * 33);
        Check(a.PendingFrames == 10, "30 Hz window survives more than four frames");
        Check(a.Add(1, 1, 2, new byte[] { 2 }, 340).SequenceEqual(new byte[] { 1, 2 }), "Delayed old repair survives 340 ms RTT window");
        Check(a.Add(10, 0, 2, new byte[] { 9 }, 350).SequenceEqual(new byte[] { 9, 10 }), "Newer frame completes independently");
        Check(a.Add(9, 0, 2, new byte[] { 8 }, 360) == null && a.PendingFrames == 0, "Late older repair cannot roll back state");

        a = new FrameAssembler();
        a.Add(2, 0, 2, new byte[] { 2 }, 0);
        a.Add(1, 0, 2, new byte[] { 1 }, 10);
        Check(a.Add(1, 1, 2, new byte[] { 3 }, 20).SequenceEqual(new byte[] { 1, 3 }) && a.PendingFrames == 1,
            "Cross-frame reverse first arrival retains newer partial frame");
        a.Add(2, 0, 2, new byte[] { 9 }, 599);
        Check(a.Add(2, 1, 2, new byte[] { 4 }, 600) == null && a.PendingFrames == 0, "Duplicate does not renew fixed assembly TTL");
        Check(a.Add(2, 0, 2, new byte[] { 2 }, 1000) == null && a.PendingFrames == 0, "Expired sequence cannot be resurrected");
        a.Add(3, 0, 2, new byte[] { 1 }, 1000);
        a.Expire(1600);
        Check(a.PendingFrames == 0, "Idle explicit expiry");

        a = new FrameAssembler();
        for (int s = 0; s <= Wire.FrameWindow; s++) a.Add(s, 0, 2, new byte[] { 1 }, 0);
        Check(a.PendingFrames == Wire.FrameWindow && a.Add(0, 1, 2, new byte[] { 2 }, 1) == null, "Capacity eviction cannot be replayed");
        var newest = a.GetMissingRequest(50);
        Check(newest.Sequence == Wire.FrameWindow && a.GetMissingRequest(99) == null, "Newest eligible frame and global NACK rate");

        a = new FrameAssembler();
        a.Add(1, 0, 2, new byte[] { 1 }, 0);
        for (int i = 0; i < Wire.MaxNackAttempts; i++) Check(a.GetMissingRequest(50 + i * 100) != null, "Bounded retry " + i);
        Check(a.GetMissingRequest(450) == null, "Per-frame attempts capped");
        a = new FrameAssembler();
        a.Add(1, 99, 100, new byte[] { 1 }, 0);
        var batch1 = a.GetMissingRequest(50);
        var batch2 = a.GetMissingRequest(150);
        Check(batch1.Indices.Length == 32 && batch2.Indices[0] == 32, "Large loss batches bounded and cursor advances");

        cache = new FrameCache();
        Check(cache.Store(1, packed, 0) && !cache.Store(1, new byte[] { 9 }, 799), "Repeated store cannot refresh cache TTL");
        Check(cache.Serve(new MissingChunks(1, new[] { 0 }), 800).Length == 0 && cache.Count == 0,
            "Cache expires at exact deadline");
        Check(!cache.Store(1, packed, 900), "Expired cache sequence cannot be restored");
        for (int s = 2; s <= 34; s++) cache.Store(s, packed, 900);
        Check(cache.Count == 32 && cache.Serve(new MissingChunks(2, new[] { 0 }), 900).Length == 0, "Cache capacity bound");
        var invalid = new[] {
            new MissingChunks(-1, new[] { 0 }), new MissingChunks(34, null),
            new MissingChunks(34, Array.Empty<int>()), new MissingChunks(34, new[] { -1 }),
            new MissingChunks(34, new[] { total }), new MissingChunks(34, new[] { 0, 0 }),
            new MissingChunks(34, new[] { 1, 0 }), new MissingChunks(34, Enumerable.Range(0, 33).ToArray()),
            new MissingChunks(int.MaxValue, new[] { 0 }) };
        Check(invalid.All(r => cache.Serve(r, 900).Length == 0), "Malicious requests rejected atomically");
        Check(cache.Serve(new MissingChunks(34, new[] { 0 }), 900).Length == 1, "Invalid request does not consume service slot");
        Check(cache.Serve(new MissingChunks(33, new[] { 0 }), 949).Length == 0, "Aggregate host rate across frames and peers");
        Check(cache.Serve(new MissingChunks(33, new[] { 0 }), 950).Length == 1, "Host service budget recovers");
        var large = new byte[Wire.MaxPacked];
        cache.Store(35, large, 1000);
        var maxRequest = new MissingChunks(35, Enumerable.Range(0, 32).ToArray());
        var maxResponse = cache.Serve(maxRequest, 1000);
        Check(maxResponse.Length == 32 && maxResponse.Sum(c => c.Payload.Length) == 32 * Wire.ChunkSize, "Max repair byte and packet budget");
        Check(cache.Serve(maxRequest, 1000).Length == 0, "Replay storm produces no extra repairs");
        cache.Serve(maxRequest, 1799);
        Check(cache.Serve(maxRequest, 1800).Length == 0 && cache.Count == 0, "Serving never renews cache TTL");

        var body = Wire.EncodeNack(new MissingChunks(42, new[] { 0, 7, Wire.MaxChunks - 1 }));
        Check(Wire.TryDecodeNack(body, out var decoded) && decoded.Sequence == 42 && decoded.Indices.SequenceEqual(new[] { 0, 7, Wire.MaxChunks - 1 }), "NACK codec round trip");
        Check(body[0] == 42 && body[4] == 3 && body.Length == 12, "NACK little-endian layout");
        Check(!Wire.TryDecodeNack(null, out _) && !Wire.TryDecodeNack(body.Take(11).ToArray(), out _) &&
            !Wire.TryDecodeNack(body.Concat(new byte[] { 0 }).ToArray(), out _) && !Wire.TryDecodeNack(new byte[10000], out _), "Truncated oversized and trailing NACK bodies rejected");
        body[8] = 0;
        Check(!Wire.TryDecodeNack(body, out _), "Duplicate wire indices rejected");
        var random = new Random(42);
        for (int i = 0; i < 1000; i++) { var fuzz = new byte[random.Next(80)]; random.NextBytes(fuzz); Wire.TryDecodeNack(fuzz, out _); }
        Check(true, "Malformed NACK fuzz does not throw");

        a = new FrameAssembler();
        Check(a.Add(1, 0, 1, null, 0) == null && a.Add(1, 0, 1, Array.Empty<byte>(), 0) == null &&
            a.Add(1, 0, 1, new byte[901], 0) == null && a.PendingFrames == 0, "Invalid chunk payload bounds");
        var mutable = new byte[] { 1 };
        a.Add(1, 0, 2, mutable, 0); mutable[0] = 9;
        Check(a.Add(1, 1, 3, new byte[] { 2 }, 0) == null && a.Add(1, 1, 2, new byte[] { 2 }, 0)[0] == 1, "Chunk ownership and inconsistent totals");
        a = new FrameAssembler();
        for (int i = 0; i < Wire.MaxChunks; i++) a.Add(1, i, Wire.MaxChunks, new byte[900], 0);
        Check(a.PendingFrames == 0 && a.Add(1, 0, 1, new byte[] { 1 }, 1) == null, "Cumulative oversize retired permanently");
        cache = new FrameCache();
        mutable = new byte[] { 1 }; cache.Store(1, mutable, 0); mutable[0] = 9;
        var copy = cache.Serve(new MissingChunks(1, new[] { 0 }), 0)[0]; copy.Payload[0] = 8;
        Check(cache.Serve(new MissingChunks(1, new[] { 0 }), 50)[0].Payload[0] == 1, "Cache owns data and repair copies");
        Check(!cache.Store(2, new byte[Wire.MaxPacked + 1], 50) && !cache.Store(2, Array.Empty<byte>(), 50), "Cache input size bounds");
        bool rejected = false;
        try { cache.Expire(49); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Clock rollback rejected");
        rejected = false;
        try { a.Expire(double.NaN); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Nonfinite clock rejected");
    }
}
