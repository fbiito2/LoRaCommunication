using System.Text;
using LoRaPTT.Models;
using LoRaPTT.Services.Protocol;
using Microsoft.Extensions.Logging;
// 避免與 MAUI 的 Microsoft.Maui.ApplicationModel.Communication.Contact 撞名
using Contact = LoRaPTT.Models.Contact;

namespace LoRaPTT.Services;

/// <summary>
/// 文字訊息服務實作。掛接 <see cref="ICommService.OnDataReceived"/>，
/// 解析來自本機 C6L 的封包並依 TYPE 分流處理。
///
/// SEQ 由本服務擁有（韌體不覆寫），用於 ACK 關聯與去重。
/// </summary>
public sealed class MessagingService : IMessagingService
{
    private readonly ICommService _comm;
    private readonly ILogger<MessagingService> _logger;
    private readonly DedupCache _dedup = new(128);

    private int _seq; // 以 Interlocked 遞增，取低 16 位作為封包 SEQ

    public string LocalNickname { get; set; } = "LoRaPTT";

    public event Action<ChatMessage>? MessageReceived;
    public event Action<ushort, ushort>? AckReceived;
    public event Action<Contact>? DeviceDiscovered;

    public MessagingService(ICommService comm, ILogger<MessagingService> logger)
    {
        _comm = comm;
        _logger = logger;
        _comm.OnDataReceived += OnDataReceived;
    }

    private ushort NextSeq() => (ushort)(Interlocked.Increment(ref _seq) & 0xFFFF);

    // ── 送出 ────────────────────────────────────────────────
    public async Task<ChatMessage> SendTextAsync(ushort dstId, string text,
        CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        if (payload.Length > PacketCodec.MaxPayload)
            throw new ArgumentException(
                $"訊息過長（{payload.Length} bytes，上限 {PacketCodec.MaxPayload}）", nameof(text));

        ushort seq = NextSeq();
        var pkt = new LoRaPacket
        {
            DstId = dstId,
            Seq = seq,
            Type = PacketType.Text,
            Payload = payload,
        };

        var msg = new ChatMessage
        {
            Direction = MessageDirection.Outgoing,
            PeerId = dstId,
            DstId = dstId,
            Text = text,
            Seq = seq,
            Status = DstId.IsUnicast(dstId) ? MessageStatus.Sending : MessageStatus.Sent,
        };

        try
        {
            await _comm.SendAsync(PacketCodec.Serialize(pkt), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "送出文字訊息失敗 DST=0x{Dst:X4}", dstId);
            msg.Status = MessageStatus.Failed;
        }
        return msg;
    }

    public async Task SendPingAsync(CancellationToken ct = default)
    {
        var pkt = new LoRaPacket
        {
            DstId = DstId.Broadcast,
            Seq = NextSeq(),
            Type = PacketType.Ping,
            Payload = Array.Empty<byte>(),
        };
        await _comm.SendAsync(PacketCodec.Serialize(pkt), ct);
        _logger.LogInformation("已送出廣播 PING 探測");
    }

    // ── 收訊 ────────────────────────────────────────────────
    private void OnDataReceived(byte[] data)
    {
        if (!PacketCodec.TryDeserialize(data, out var pkt))
        {
            _logger.LogWarning("收到無法解析的封包（{Len} bytes）", data?.Length ?? 0);
            return;
        }

        // 去重：洪泛網路同封包可能多路抵達
        if (_dedup.SeenOrAdd(pkt.SrcId, pkt.Seq))
            return;

        switch (pkt.Type)
        {
            case PacketType.Text: HandleText(pkt); break;
            case PacketType.Ack: HandleAck(pkt); break;
            case PacketType.Ping: HandlePing(pkt); break;
            case PacketType.Voice:   // 語音另由 PTT 路徑處理
            case PacketType.Control: // 控制/心跳暫不處理
            default:
                _logger.LogDebug("忽略 TYPE=0x{Type:X2} 封包", (byte)pkt.Type);
                break;
        }
    }

    private void HandleText(LoRaPacket pkt)
    {
        var msg = new ChatMessage
        {
            Direction = MessageDirection.Incoming,
            PeerId = pkt.SrcId,
            DstId = pkt.DstId,
            Text = pkt.PayloadAsText(),
            Status = MessageStatus.Received,
        };
        MessageReceived?.Invoke(msg);

        // 點對點訊息需回 ACK；廣播/群組不回
        if (DstId.IsUnicast(pkt.DstId))
            _ = SendAckAsync(pkt.SrcId, pkt.Seq);
    }

    private async Task SendAckAsync(ushort dst, ushort ackedSeq)
    {
        var payload = new[] { (byte)(ackedSeq >> 8), (byte)(ackedSeq & 0xFF) };
        var pkt = new LoRaPacket
        {
            DstId = dst,
            Seq = NextSeq(),
            Type = PacketType.Ack,
            Payload = payload,
        };
        try { await _comm.SendAsync(PacketCodec.Serialize(pkt)); }
        catch (Exception ex) { _logger.LogError(ex, "回送 ACK 失敗 DST=0x{Dst:X4}", dst); }
    }

    private void HandleAck(LoRaPacket pkt)
    {
        if (pkt.Payload.Length < 2)
        {
            _logger.LogWarning("ACK payload 過短，忽略");
            return;
        }
        ushort ackedSeq = (ushort)((pkt.Payload[0] << 8) | pkt.Payload[1]);
        AckReceived?.Invoke(pkt.SrcId, ackedSeq);
    }

    private void HandlePing(LoRaPacket pkt)
    {
        if (DstId.IsBroadcast(pkt.DstId))
        {
            // 收到他人的 PING 請求 → 回覆本機暱稱給請求者
            _ = SendPingReplyAsync(pkt.SrcId);
        }
        else
        {
            // 這是針對我們 PING 的回覆 → payload 為對方暱稱，發現裝置
            var contact = new Contact
            {
                DeviceId = pkt.SrcId,
                Name = pkt.PayloadAsText(),
                LastSeen = DateTimeOffset.Now,
                Discovered = true,
            };
            DeviceDiscovered?.Invoke(contact);
        }
    }

    private async Task SendPingReplyAsync(ushort dst)
    {
        var pkt = new LoRaPacket
        {
            DstId = dst,
            Seq = NextSeq(),
            Type = PacketType.Ping,
            Payload = Encoding.UTF8.GetBytes(LocalNickname),
        };
        try { await _comm.SendAsync(PacketCodec.Serialize(pkt)); }
        catch (Exception ex) { _logger.LogError(ex, "回覆 PING 失敗 DST=0x{Dst:X4}", dst); }
    }
}
