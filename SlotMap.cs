using System;

namespace TwinShotNet;

// 槽位分配是不依赖 Unity 的纯计算：本地原生箱子占用 + 远端 peer 数量 → 远端应占用的槽位。
// 关卡只会创建 1..N 号玩家（N = Game.playerSkins.Length），所以远端槽位必须与本地槽位互斥，
// 且开局人数必须等于最高占用槽位 + 1，否则客户端会拒绝 match 包或产生无人控制的空槽玩家 (F2)。
public static class SlotMap
{
    public const int Slots = 4;

    // 按传入顺序返回每个远端 peer 的目标槽位；0 号槽位始终保留给主机 P1。
    // 空槽不足时返回 null，调用方必须拒绝而不是错位分配。
    public static int[] AssignRemote(bool[] localOccupied, int peerCount)
    {
        if (localOccupied == null || localOccupied.Length != Slots)
            throw new ArgumentException("Invalid local slot array", nameof(localOccupied));
        if (peerCount < 0 || peerCount > Slots - 1) return null;
        var result = new int[peerCount];
        int found = 0;
        for (int slot = 1; slot < Slots && found < peerCount; slot++)
        {
            if (localOccupied[slot]) continue;
            result[found++] = slot;
        }

        return found == peerCount ? result : null;
    }

    // 最高占用槽位 + 1，即关卡必须创建的玩家数量上界。
    public static int HighestOccupied(bool[] occupied)
    {
        if (occupied == null) throw new ArgumentNullException(nameof(occupied));
        int highest = 0;
        for (int slot = 0; slot < occupied.Length; slot++)
            if (occupied[slot])
                highest = slot + 1;
        return highest;
    }
}
