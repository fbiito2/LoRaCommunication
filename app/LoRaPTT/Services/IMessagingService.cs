using LoRaPTT.Models;
// 避免與 MAUI 的 Microsoft.Maui.ApplicationModel.Communication.Contact 撞名
using Contact = LoRaPTT.Models.Contact;

namespace LoRaPTT.Services;

/// <summary>
/// 文字訊息服務 — 建立於 <see cref="ICommService"/> 之上，
/// 負責封包組裝/解析、去重、ACK（點對點）、PING 探測發現。
/// </summary>
public interface IMessagingService
{
    /// <summary>本機暱稱（握手與回覆 PING 探測時帶出）</summary>
    string LocalNickname { get; set; }

    /// <summary>本機所連 C6L 的 Device ID（握手回應取得，未握手為 null）</summary>
    ushort? LocalDeviceId { get; }

    /// <summary>本機所連 C6L 的韌體版本（握手回應取得）</summary>
    string? FirmwareVersion { get; }

    /// <summary>握手完成（收到 C6L hello-ack）</summary>
    event Action HandshakeCompleted;

    /// <summary>收到文字訊息（點對點/廣播/群組皆會觸發）</summary>
    event Action<ChatMessage> MessageReceived;

    /// <summary>收到 ACK：(回覆者裝置 ID, 被確認的 SEQ)</summary>
    event Action<ushort, ushort> AckReceived;

    /// <summary>透過 PING 探測發現裝置</summary>
    event Action<Contact> DeviceDiscovered;

    /// <summary>收到 SOS 緊急求救（F-073）。參數：發送者 ID、GPS + 附加 payload</summary>
    event Action<ushort, byte[]> SosReceived;

    /// <summary>收到任何節點的封包（供 NodeDB 累積，F-036）。參數：(來源 ID, RSSI)</summary>
    event Action<ushort, short> NodeHeard;

    /// <summary>收到定位廣播（F-074）。參數：(來源 ID, 緯度, 經度)</summary>
    event Action<ushort, double, double> PositionReceived;

    /// <summary>收到語音幀（F-052 PTT）。參數為 Codec2 位元組（多幀串接，每幀 6 bytes）。</summary>
    event Action<byte[]> VoiceReceived;

    /// <summary>
    /// 送出文字訊息。
    /// 點對點（unicast）狀態為 Sending（待 ACK）；廣播/群組為 Sent（無 ACK）。
    /// </summary>
    Task<ChatMessage> SendTextAsync(ushort dstId, string text, CancellationToken ct = default);

    /// <summary>送出廣播 PING 探測，附近裝置會回覆 ID + 暱稱</summary>
    Task SendPingAsync(CancellationToken ct = default);

    /// <summary>發送 SOS 緊急求救（F-072），重複 3 次，HOP=15</summary>
    Task SendSosAsync(double gpsLat = 0, double gpsLon = 0,
        string? extraText = null, CancellationToken ct = default);

    /// <summary>送出語音幀（F-052）。廣播 TYPE_VOICE，無 ACK、不重送（串流容忍丟包）。</summary>
    Task SendVoiceAsync(byte[] codec2Frames, CancellationToken ct = default);

    /// <summary>通知 C6L 進入/結束語音模式（F-052），觸發全網 LoRa SF7/BW500 切換。</summary>
    Task SendVoiceModeAsync(bool start, CancellationToken ct = default);
}
