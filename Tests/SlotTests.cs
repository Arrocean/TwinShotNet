using System;
using System.Linq;
using TwinShotNet;

static class SlotTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("Slot: " + name);
            checks++;
        }
        void Reject<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new Exception("Slot: expected " + typeof(T).Name + " for " + name);
        }

        // 主机 P1 单独在线：远端只能拿 1-3 号槽位。
        Check(SlotMap.AssignRemote(new[] { true, false, false, false }, 3).SequenceEqual(new[] { 1, 2, 3 }),
            "Single local player keeps slots 1-3 free");
        // 本地箱子占住 2 号槽位（主机 P1 + 本地 P3）：远端必须跳过它，不能覆盖本地玩家。
        Check(SlotMap.AssignRemote(new[] { true, true, false, false }, 2).SequenceEqual(new[] { 2, 3 }),
            "Local occupancy is skipped");
        Check(SlotMap.AssignRemote(new[] { true, false, true, false }, 2).SequenceEqual(new[] { 1, 3 }),
            "Gap in local slots is filled by remote peers");
        // 中间 peer 掉线后剩余 peer 必须被压回最低空槽，否则 assigned 会超过 players。
        Check(SlotMap.AssignRemote(new[] { true, false, false, false }, 1).SequenceEqual(new[] { 1 }),
            "Remaining peer is compacted after a middle disconnect");
        Check(SlotMap.AssignRemote(new[] { true, false, false, false }, 0).Length == 0, "No peers needs no slots");
        Check(SlotMap.AssignRemote(new[] { true, true, true, true }, 1) == null, "No free slot refuses assignment");
        Check(SlotMap.AssignRemote(new[] { true, false, false, false }, 4) == null, "Peer count above capacity refused");
        Check(SlotMap.AssignRemote(new[] { true, false, false, false }, -1) == null, "Negative peer count refused");
        Reject<ArgumentException>(() => SlotMap.AssignRemote(new bool[3], 1), "slot array length");
        Reject<ArgumentNullException>(() => SlotMap.HighestOccupied(null), "null occupancy");

        Check(SlotMap.HighestOccupied(new[] { true, false, false, false }) == 1, "Highest occupied slot 1");
        Check(SlotMap.HighestOccupied(new[] { true, false, true, false }) == 3, "Gap still raises the player count");
        Check(SlotMap.HighestOccupied(new[] { false, false, false, true }) == 4, "Highest slot 4");
        Check(SlotMap.HighestOccupied(new bool[4]) == 0, "Empty occupancy");
        // 不变式：只要分配成功，所有目标槽位都低于开局人数，且不与本地槽位重叠。
        var local = new[] { true, false, true, false };
        var targets = SlotMap.AssignRemote(local, 2);
        var occupied = (bool[])local.Clone();
        foreach (int slot in targets) occupied[slot] = true;
        int players = SlotMap.HighestOccupied(occupied);
        Check(players <= SlotMap.Slots && targets.All(slot => slot < players && !local[slot]),
            "Assignment invariant: assigned < players and disjoint from local");
        Console.WriteLine($"Slots: {checks} checks passed");
    }
}
