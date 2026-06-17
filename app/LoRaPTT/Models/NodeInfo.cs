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
    /// 避開「自己」青點（#00e5ff ≈ 186°）的色帶，否則該色帶的節點會跟自己撞色。
    /// </summary>
    public string Color
    {
        get
        {
            // 混雜雜湊：放大相近 ID 的差異，避免 0xD400/0xD078 這種相鄰 ID 算出近似色相。
            uint h = (uint)DeviceId;
            h = (h ^ (h >> 7)) * 2654435761u;
            // 把色相映到「扣掉自己青色帶後」的 320° 範圍，再整段跳過該帶（非平移，免得擠在一起）
            int hue = (int)(h % 320);
            if (hue >= 166) hue += 40; // 跳過自己青點(#00e5ff≈186°)的 [166,206] 色帶
            return $"hsl({hue}, 70%, 55%)";
        }
    }
}
