using System;
using System.Buffers.Binary;

namespace TwinShotNet;

public static class PacketValidation
{
    // Body excludes the packet type. Validate before applying any session state.
    public static bool IsValid(byte type, byte[] body)
    {
        if (body == null) return false;
        switch (type)
        {
            case 1:
                return body.Length == 1 && body[0] >= 2 && body[0] <= 4;
            case 2:
                return body.Length == 1;
            case 4:
                if (body.Length < 9 || body.Length > 8 + Wire.ChunkSize) return false;
                int sequence = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0, 4));
                int index = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4, 2));
                int total = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6, 2));
                return sequence >= 0 && total >= 1 && total <= Wire.MaxChunks && index < total;
            case 5:
                return body.Length == 2 && body[0] <= 3 && body[1] <= 1;
            case 6:
                if (body.Length != 12) return false;
                for (int i = 0; i < 12; i += 3)
                    if (body[i] > 1 || body[i + 1] > 3 || body[i + 2] > 1) return false;
                return true;
            case 7:
                return Wire.TryDecodeNack(body, out _);
            case 8:
                if (body.Length < 9) return false;
                int level = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1, 4));
                int players = body[5];
                // 主题必须是具体主题：RandomDeluxe/RandomClassic 无法参与 LevelId 构造 (F1)。
                if (body[0] > Wire.MaxConcreteTheme || level < 1 || level > 100 || players < 2 || players > 4 ||
                    body[6] < 2 || body[6] > players || body.Length != 7 + players) return false;
                for (int i = 7; i < body.Length; i++) if (body[i] > 3) return false;
                return true;
            case 9:
                return body.Length == 0;
            case 10:
                return body.Length == 4 && BinaryPrimitives.ReadInt32LittleEndian(body) >= 0;
            default:
                return false;
        }
    }
}
