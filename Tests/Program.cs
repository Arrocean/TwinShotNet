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
        Console.WriteLine($"{checks} checks passed");
    }
}
