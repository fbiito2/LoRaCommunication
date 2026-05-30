namespace LoRaPTT.Models;

/// <summary>群組（對應 DST_ID 0xFFE0~0xFFEF，最多 16 組）</summary>
public sealed class DeviceGroup
{
    /// <summary>群組索引 0~15</summary>
    public int Index { get; set; }

    /// <summary>群組名稱</summary>
    public string Name { get; set; } = "";

    /// <summary>本機是否已加入此群組（決定是否顯示該群組訊息）</summary>
    public bool Joined { get; set; }

    /// <summary>對應的 DST_ID</summary>
    public ushort DstId => Services.Protocol.DstId.GroupAddress(Index);

    /// <summary>DST_ID 的十六進位字串（如 "FFE0"）</summary>
    public string IdHex => DstId.ToString("X4");

    /// <summary>顯示名稱，無名稱時以索引回退</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"群組 {Index}" : Name;
}
