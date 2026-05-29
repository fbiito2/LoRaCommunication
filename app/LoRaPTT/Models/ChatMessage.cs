namespace LoRaPTT.Models;

/// <summary>訊息方向</summary>
public enum MessageDirection
{
    /// <summary>本機送出</summary>
    Outgoing,
    /// <summary>對端收到</summary>
    Incoming,
}

/// <summary>訊息傳送狀態（僅點對點訊息有完整生命週期）</summary>
public enum MessageStatus
{
    /// <summary>送出中，等待 ACK（點對點）</summary>
    Sending,
    /// <summary>已送達（收到 ACK）</summary>
    Delivered,
    /// <summary>已送出（廣播/群組無 ACK，發出即完成）</summary>
    Sent,
    /// <summary>傳送失敗</summary>
    Failed,
    /// <summary>已收到（收訊方向）</summary>
    Received,
}

/// <summary>聊天訊息（一則文字訊息）</summary>
public sealed class ChatMessage
{
    /// <summary>方向</summary>
    public MessageDirection Direction { get; set; }

    /// <summary>對方裝置 ID：送出時為目標 DST_ID，收訊時為來源 SRC_ID</summary>
    public ushort PeerId { get; set; }

    /// <summary>目標位址（送出時 = DST_ID；收訊時 = 封包 DST_ID，可判斷廣播/群組）</summary>
    public ushort DstId { get; set; }

    /// <summary>文字內容</summary>
    public string Text { get; set; } = "";

    /// <summary>時間戳</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    /// <summary>傳送狀態</summary>
    public MessageStatus Status { get; set; }

    /// <summary>送出訊息的封包 SEQ（用於比對回傳的 ACK）</summary>
    public ushort Seq { get; set; }

    /// <summary>收訊 RSSI（dBm），僅收訊方向有值</summary>
    public int? Rssi { get; set; }

    /// <summary>是否為廣播或群組訊息</summary>
    public bool IsGroupOrBroadcast
        => Services.Protocol.DstId.IsBroadcast(DstId)
        || Services.Protocol.DstId.IsGroup(DstId);
}
