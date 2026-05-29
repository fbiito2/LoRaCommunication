namespace LoRaPTT.Services.Protocol;

/// <summary>
/// LoRa 封包（對應韌體 struct LoRaPacket）。
///
/// 注意：手機 → C6L 方向，SRC_ID / HOP / MAC 由韌體覆寫；
/// DST_ID / SEQ / TYPE / PAYLOAD 由 APP 提供並保留（SEQ 由 APP 擁有，
/// 以利 ACK 關聯與去重）。C6L → 手機 方向，所有欄位均為有效值。
/// </summary>
public sealed class LoRaPacket
{
    /// <summary>原始發送者 ID（C6L→手機 有效；手機→C6L 由韌體覆寫）</summary>
    public ushort SrcId { get; set; }

    /// <summary>目標接收者 ID（見 <see cref="DstId"/>）</summary>
    public ushort DstId { get; set; }

    /// <summary>剩餘跳數（手機→C6L 由韌體覆寫為 MAX_HOP）</summary>
    public byte Hop { get; set; }

    /// <summary>遞增封包序號（手機→C6L 由韌體覆寫）</summary>
    public ushort Seq { get; set; }

    /// <summary>封包類型</summary>
    public PacketType Type { get; set; }

    /// <summary>資料內容（文字為 UTF-8 bytes；Phase 1 不加密）</summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>建立一個準備送往 C6L 的封包（SRC/SEQ/HOP 留佔位 0）</summary>
    public static LoRaPacket ForSend(ushort dstId, PacketType type, byte[] payload)
        => new() { DstId = dstId, Type = type, Payload = payload };

    /// <summary>取得 payload 的 UTF-8 文字表示</summary>
    public string PayloadAsText()
        => System.Text.Encoding.UTF8.GetString(Payload);
}
