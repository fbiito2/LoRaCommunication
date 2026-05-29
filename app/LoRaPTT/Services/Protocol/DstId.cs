namespace LoRaPTT.Services.Protocol;

/// <summary>
/// DST_ID 定址規則 — 與韌體 packet.h 一致。
/// 0x0001~0xFFFE = 點對點；0xFFFF = 全體廣播；0xFFE0~0xFFEF = 群組（最多 16 組）。
/// </summary>
public static class DstId
{
    /// <summary>全體廣播位址</summary>
    public const ushort Broadcast = 0xFFFF;

    /// <summary>群組位址範圍下界</summary>
    public const ushort GroupMin = 0xFFE0;

    /// <summary>群組位址範圍上界</summary>
    public const ushort GroupMax = 0xFFEF;

    /// <summary>保留位址（不使用）</summary>
    public const ushort Reserved = 0x0000;

    /// <summary>群組數量上限</summary>
    public const int GroupCount = GroupMax - GroupMin + 1; // 16

    /// <summary>是否為全體廣播位址</summary>
    public static bool IsBroadcast(ushort dst) => dst == Broadcast;

    /// <summary>是否為群組位址</summary>
    public static bool IsGroup(ushort dst) => dst >= GroupMin && dst <= GroupMax;

    /// <summary>是否為單一裝置（點對點）位址</summary>
    public static bool IsUnicast(ushort dst) => dst >= 0x0001 && dst <= 0xFFDF;

    /// <summary>將群組索引（0~15）轉為群組位址</summary>
    public static ushort GroupAddress(int groupIndex)
    {
        if (groupIndex < 0 || groupIndex >= GroupCount)
            throw new ArgumentOutOfRangeException(nameof(groupIndex),
                $"群組索引須介於 0~{GroupCount - 1}");
        return (ushort)(GroupMin + groupIndex);
    }

    /// <summary>將群組位址轉為群組索引（0~15），非群組位址回傳 -1</summary>
    public static int ToGroupIndex(ushort dst)
        => IsGroup(dst) ? dst - GroupMin : -1;
}
