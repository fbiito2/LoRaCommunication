namespace LoRaPTT.Services.Protocol;

/// <summary>封包類型（TYPE 欄位）— 與韌體 packet.h 一致</summary>
public enum PacketType : byte
{
    /// <summary>文字訊息</summary>
    Text = 0x01,
    /// <summary>Codec2 語音幀</summary>
    Voice = 0x02,
    /// <summary>控制/心跳</summary>
    Control = 0x03,
    /// <summary>ACK 確認（點對點）</summary>
    Ack = 0x04,
    /// <summary>PING/探測（廣播發現裝置）</summary>
    Ping = 0x05,
    /// <summary>SOS 緊急求救（F-070）</summary>
    Sos = 0x06,
    /// <summary>定位廣播（F-074，GPS 座標）</summary>
    Pos = 0x07,
}
