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

    /// <summary>
    /// 送出文字訊息。
    /// 點對點（unicast）狀態為 Sending（待 ACK）；廣播/群組為 Sent（無 ACK）。
    /// </summary>
    Task<ChatMessage> SendTextAsync(ushort dstId, string text, CancellationToken ct = default);

    /// <summary>送出廣播 PING 探測，附近裝置會回覆 ID + 暱稱</summary>
    Task SendPingAsync(CancellationToken ct = default);
}
