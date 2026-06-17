namespace LoRaPTT.Models;

/// <summary>
/// 節點資料（NodeDB，F-036）。由收到的封包自動累積：
/// 最後聽到時間、RSSI、暱稱、座標（對應韌體 nodedb）。
/// </summary>
public sealed class NodeInfo
{
    /// <summary>節點 Device ID</summary>
    public ushort DeviceId { get; set; }

    /// <summary>暱稱（從 PING 回覆學到，可能為空）</summary>
    public string? Name { get; set; }

    /// <summary>最後聽到的時間</summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>最後聽到的 RSSI（dBm）</summary>
    public short Rssi { get; set; }

    /// <summary>是否已知座標</summary>
    public bool HasPosition { get; set; }

    /// <summary>緯度（HasPosition 為 true 時有效）</summary>
    public double Lat { get; set; }

    /// <summary>經度（HasPosition 為 true 時有效）</summary>
    public double Lon { get; set; }

    /// <summary>Device ID 的 4 位十六進位字串（顯示用）</summary>
    public string DeviceIdHex => DeviceId.ToString("X4");

    /// <summary>
    /// 依 DeviceId 算出的固定顯示顏色（清單圓點與地圖標記共用，同一台永遠同色）。
    /// 用黃金角(137°)散佈色相，讓不同節點顏色分得開；固定飽和/亮度，深底好辨識。
    /// </summary>
    public string Color => $"hsl({(DeviceId * 137) % 360}, 70%, 55%)";
}
