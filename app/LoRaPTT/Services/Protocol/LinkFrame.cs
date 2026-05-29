using System.Text;

namespace LoRaPTT.Services.Protocol;

/// <summary>
/// 手機 ↔ C6L 線路幀 — 與韌體 main.cpp 的 LINK_* 一致。
/// 傳輸層（USB 2-byte 長度前綴 / WiFi 一個 datagram）負責邊界，本層只管型別與內容。
///
///   LINK_DATA：phone→C6L = [01][LoRa封包]；C6L→phone = [01][RSSI int16 BE][LoRa封包]
///   LINK_CTRL：控制/設定 JSON（雙向，UTF-8）
/// </summary>
public static class LinkFrame
{
    public const byte Data = 0x01;
    public const byte Ctrl = 0x02;

    /// <summary>包裝資料幀（phone→C6L）：[01][packet]</summary>
    public static byte[] WrapData(byte[] packet)
    {
        var f = new byte[1 + packet.Length];
        f[0] = Data;
        Array.Copy(packet, 0, f, 1, packet.Length);
        return f;
    }

    /// <summary>包裝控制幀：[02][json]</summary>
    public static byte[] WrapCtrl(string json)
    {
        var j = Encoding.UTF8.GetBytes(json);
        var f = new byte[1 + j.Length];
        f[0] = Ctrl;
        Array.Copy(j, 0, f, 1, j.Length);
        return f;
    }

    /// <summary>解析來自 C6L 的資料幀：[01][RSSI int16 BE][packet]</summary>
    /// <returns>成功取出 packet 與 rssi 則回傳 true</returns>
    public static bool TryParseData(byte[] frame, out byte[] packet, out short rssi)
    {
        packet = Array.Empty<byte>();
        rssi = 0;
        if (frame is null || frame.Length < 3 || frame[0] != Data)
            return false;
        rssi = (short)((frame[1] << 8) | frame[2]);
        packet = frame[3..];
        return true;
    }

    /// <summary>解析控制幀：[02][json]，回傳 JSON 字串</summary>
    public static bool TryParseCtrl(byte[] frame, out string json)
    {
        json = "";
        if (frame is null || frame.Length < 1 || frame[0] != Ctrl)
            return false;
        json = Encoding.UTF8.GetString(frame, 1, frame.Length - 1);
        return true;
    }
}
