namespace LoRaPTT.Models;

/// <summary>聯絡人（一台對端 C6L 裝置）</summary>
public sealed class Contact
{
    /// <summary>裝置 ID（2 bytes，0x0001~0xFFFE）</summary>
    public ushort DeviceId { get; set; }

    /// <summary>顯示暱稱</summary>
    public string Name { get; set; } = "";

    /// <summary>最近一次收到此聯絡人訊息的時間</summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>最近一次收到訊息的 RSSI（dBm），null 表示未知</summary>
    public int? LastRssi { get; set; }

    /// <summary>來源：true=廣播探測自動發現，false=手動輸入</summary>
    public bool Discovered { get; set; }

    /// <summary>ID 的 4 位十六進位顯示（如 "A001"）</summary>
    public string IdHex => DeviceId.ToString("X4");

    /// <summary>顯示用名稱，無暱稱時回退為 ID</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"0x{IdHex}" : Name;
}
