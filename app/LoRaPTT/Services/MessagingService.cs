using System.Text;
using System.Text.Json;
using LoRaPTT.Models;
using LoRaPTT.Services.Protocol;
using Microsoft.Extensions.Logging;
// 避免與 MAUI 的 Microsoft.Maui.ApplicationModel.Communication.Contact 撞名
using Contact = LoRaPTT.Models.Contact;

namespace LoRaPTT.Services;

/// <summary>
/// 文字訊息服務實作。掛接 <see cref="ICommService"/>，以線路幀（<see cref="LinkFrame"/>）
/// 與 C6L 溝通：DATA = LoRa 封包（含 RSSI），CTRL = JSON 控制（握手/設定）。
///
/// SEQ 由本服務擁有（韌體不覆寫），用於 ACK 關聯與去重。
/// </summary>
public sealed class MessagingService : IMessagingService
{
    private const string AppVersion = "1.0";

    private readonly ICommService _comm;
    private readonly ILogger<MessagingService> _logger;
    private readonly DedupCache _dedup = new(128);

    private int _seq; // 以 Interlocked 遞增，取低 16 位作為封包 SEQ

    public string LocalNickname { get; set; } = "LoRaPTT";
    public ushort? LocalDeviceId { get; private set; }
    public string? FirmwareVersion { get; private set; }

    public event Action? HandshakeCompleted;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<ushort, ushort>? AckReceived;
    public event Action<Contact>? DeviceDiscovered;
    /// <summary>收到 SOS 緊急求救（F-073）。參數：發送者 ID、GPS payload（可能為空）</summary>
    public event Action<ushort, byte[]>? SosReceived;

    public MessagingService(ICommService comm, ILogger<MessagingService> logger)
    {
        _comm = comm;
        _logger = logger;
        _comm.OnDataReceived += OnDataReceived;
        _comm.OnConnectionChanged += OnConnectionChanged;
    }

    private ushort NextSeq() => (ushort)(Interlocked.Increment(ref _seq) & 0xFFFF);

    // ── 連線後送握手（F-053）──────────────────────────────────
    private void OnConnectionChanged(bool connected)
    {
        if (connected) _ = SendHelloAsync();
    }

    private async Task SendHelloAsync()
    {
        var json = JsonSerializer.Serialize(new
        {
            cmd = "hello",
            name = LocalNickname,
            app_ver = AppVersion,
        });
        try { await _comm.SendAsync(LinkFrame.WrapCtrl(json)); Console.WriteLine("LPTT: 已送出握手 hello"); }
        catch (Exception ex) { _logger.LogError(ex, "送出握手 hello 失敗"); Console.WriteLine("LPTT: 握手 hello 失敗: " + ex.Message); }
    }

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
            await _comm.SendAsync(LinkFrame.WrapData(PacketCodec.Serialize(pkt)), ct);
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
        await _comm.SendAsync(LinkFrame.WrapData(PacketCodec.Serialize(pkt)), ct);
        _logger.LogInformation("已送出廣播 PING 探測");
    }

    // ── 收訊 ────────────────────────────────────────────────
    private void OnDataReceived(byte[] frame)
    {
        if (frame is null || frame.Length < 1) return;
        Console.WriteLine($"LPTT: MS 收到幀 {frame.Length}B 類型=0x{frame[0]:X2}");

        switch (frame[0])
        {
            case LinkFrame.Data: HandleDataFrame(frame); break;
            case LinkFrame.Ctrl: HandleCtrlFrame(frame); break;
            default:
                _logger.LogDebug("忽略未知線路幀類型 0x{T:X2}", frame[0]);
                break;
        }
    }

    private void HandleDataFrame(byte[] frame)
    {
        if (!LinkFrame.TryParseData(frame, out var packet, out short rssi))
            return;
        if (!PacketCodec.TryDeserialize(packet, out var pkt))
        {
            _logger.LogWarning("收到無法解析的 LoRa 封包");
            return;
        }

        // 去重：洪泛網路同封包可能多路抵達
        if (_dedup.SeenOrAdd(pkt.SrcId, pkt.Seq)) return;

        switch (pkt.Type)
        {
            case PacketType.Text: HandleText(pkt, rssi); break;
            case PacketType.Ack: HandleAck(pkt); break;
            case PacketType.Ping: HandlePing(pkt); break;
            case PacketType.Sos: HandleSos(pkt); break;
            case PacketType.Voice:   // 語音另由 PTT 路徑處理
            case PacketType.Control:
            default:
                _logger.LogDebug("忽略 TYPE=0x{Type:X2} 封包", (byte)pkt.Type);
                break;
        }
    }

    private void HandleCtrlFrame(byte[] frame)
    {
        if (!LinkFrame.TryParseCtrl(frame, out var json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var statusEl)) return;
            var status = statusEl.GetString();

            if (status == "hello")
            {
                Console.WriteLine("LPTT: 收到 hello-ack: " + json);
                if (root.TryGetProperty("device_id", out var idEl) && idEl.TryGetUInt16(out var id))
                    LocalDeviceId = id;
                if (root.TryGetProperty("fw_ver", out var fwEl))
                    FirmwareVersion = fwEl.GetString();
                _logger.LogInformation("握手完成：本機 C6L ID=0x{Id:X4}，韌體 {Fw}",
                    LocalDeviceId ?? 0, FirmwareVersion);
                HandshakeCompleted?.Invoke();
            }
            else if (status == "ok")
            {
                _logger.LogInformation("C6L 設定已套用");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "無法解析 C6L 控制 JSON：{Json}", json);
        }
    }

    private void HandleText(LoRaPacket pkt, short rssi)
    {
        var msg = new ChatMessage
        {
            Direction = MessageDirection.Incoming,
            PeerId = pkt.SrcId,
            DstId = pkt.DstId,
            Text = pkt.PayloadAsText(),
            Status = MessageStatus.Received,
            Rssi = rssi,
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
        try { await _comm.SendAsync(LinkFrame.WrapData(PacketCodec.Serialize(pkt))); }
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
        // 韌體已自動回覆廣播 PING（F-004），APP 只處理收到的回覆
        if (DstId.IsBroadcast(pkt.DstId))
        {
            // 廣播 PING 請求 → 韌體處理回覆，APP 不重複回覆
            _logger.LogDebug("收到廣播 PING（韌體已自動回覆）");
            return;
        }

        // 針對我們 PING 的回覆 → payload 含 [DeviceID 2B][暱稱]
        ushort discoveredId = pkt.SrcId;
        string name = "";
        if (pkt.Payload.Length >= 2)
        {
            discoveredId = (ushort)((pkt.Payload[0] << 8) | pkt.Payload[1]);
            if (pkt.Payload.Length > 2)
                name = Encoding.UTF8.GetString(pkt.Payload, 2, pkt.Payload.Length - 2);
        }
        else
        {
            name = pkt.PayloadAsText();
        }

        var contact = new Contact
        {
            DeviceId = discoveredId,
            Name = string.IsNullOrEmpty(name) ? $"Device_{discoveredId:X4}" : name,
            LastSeen = DateTimeOffset.Now,
            Discovered = true,
        };
        DeviceDiscovered?.Invoke(contact);
    }

    // ── SOS 緊急求救（F-070~073）────────────────────────────
    private void HandleSos(LoRaPacket pkt)
    {
        _logger.LogWarning("!!! 收到 SOS 緊急求救！來自 0x{Src:X4}", pkt.SrcId);
        SosReceived?.Invoke(pkt.SrcId, pkt.Payload);
    }

    /// <summary>
    /// 發送 SOS 緊急求救（F-072 APP 觸發）。
    /// 重複發送 3 次，間隔 2 秒，HOP=15。
    /// </summary>
    /// <param name="gpsLat">GPS 緯度（手機定位，無則傳 0）</param>
    /// <param name="gpsLon">GPS 經度</param>
    /// <param name="extraText">附加文字（可選）</param>
    public async Task SendSosAsync(double gpsLat = 0, double gpsLon = 0,
        string? extraText = null, CancellationToken ct = default)
    {
        // Payload：[DeviceID 2B][Lat 8B double][Lon 8B double][文字 NB]
        using var ms = new MemoryStream();
        var id = LocalDeviceId ?? 0;
        ms.WriteByte((byte)(id >> 8));
        ms.WriteByte((byte)(id & 0xFF));
        ms.Write(BitConverter.GetBytes(gpsLat));
        ms.Write(BitConverter.GetBytes(gpsLon));
        if (!string.IsNullOrEmpty(extraText))
        {
            var textBytes = Encoding.UTF8.GetBytes(extraText);
            ms.Write(textBytes, 0, Math.Min(textBytes.Length, 200));
        }

        for (int i = 0; i < 3; i++) // 重複 3 次
        {
            var pkt = new LoRaPacket
            {
                DstId = DstId.Broadcast,
                Seq = NextSeq(),
                Type = PacketType.Sos,
                Payload = ms.ToArray(),
            };
            try
            {
                await _comm.SendAsync(LinkFrame.WrapData(PacketCodec.Serialize(pkt)), ct);
                _logger.LogWarning("SOS 發送第 {N}/3 次", i + 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SOS 發送失敗");
            }
            if (i < 2) await Task.Delay(2000, ct); // 間隔 2 秒
        }
    }
}
