using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LoRaPTT.Services.Protocol;

// ── LoRaPTT PC 客戶端 ───────────────────────────────────────────
// 當作第二個端點：文字收發 + 握手。重用 App 協定碼確保位元級一致。
//
// 用法：
//   WiFi（建議，與手機同通道、穩定）：
//     dotnet run --project pc-client -- wifi [192.168.4.1] [FFFF] [probe]
//     先讓 PC 的 WiFi 連上該 C6L 的 AP（如 LoRaPTT_BF8C）。
//   USB（原生 HWCDC 不穩，僅備用）：
//     dotnet run --project pc-client -- COM7 [FFFF] [probe]
//
//   WiFi datagram = 一個 LinkFrame（無長度前綴）；
//   USB 幀 = [2-byte 大端長度][LinkFrame]，且 reader 會重同步容忍混入的 log。
// ────────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

bool probe = args.Any(a => a.Equals("probe", StringComparison.OrdinalIgnoreCase));
bool echo  = args.Any(a => a.Equals("echo", StringComparison.OrdinalIgnoreCase)); // 自動回聲：收到文字原字回傳
string? com = args.FirstOrDefault(a => a.StartsWith("COM", StringComparison.OrdinalIgnoreCase));
bool useWifi = com is null || args.Any(a => a.Equals("wifi", StringComparison.OrdinalIgnoreCase));
string ip = args.FirstOrDefault(a => IPAddress.TryParse(a, out _)) ?? "192.168.4.1";
ushort dst = 0xFFFF;
foreach (var a in args)
    if (a.Length == 4 && ushort.TryParse(a, NumberStyles.HexNumber, null, out var d)) dst = d;

// 非互動測試用：send:<文字> 握手後送一則；secs:<N> probe 監聽秒數（預設 8）
string? sendText = args.FirstOrDefault(a => a.StartsWith("send:", StringComparison.OrdinalIgnoreCase))?[5..];
int listenSecs = 8;
var secsArg = args.FirstOrDefault(a => a.StartsWith("secs:", StringComparison.OrdinalIgnoreCase));
if (secsArg != null && int.TryParse(secsArg[5..], out var ls)) listenSecs = ls;

// 每次啟動用隨機高起始 SEQ：否則每次執行都從 1 開始，重複的 (SrcId,Seq)
// 會被接收端去重快取吃掉、訊息不顯示（實測踩過：第二次起的廣播都被手機去重）。
int seqCounter = new Random().Next(1, 50000);
ushort NextSeq() => (ushort)(Interlocked.Increment(ref seqCounter) & 0xFFFF);

// 傳輸層注入點（WiFi/USB 分支稍後指派）；先給 no-op 佔位，讓 HandleFrame 可捕獲
Action<byte[]> sendLink = _ => { };
Action shutdown = () => { };

// ── 處理收到的 LinkFrame（兩種傳輸共用）──
void HandleFrame(byte[] frame)
{
    if (frame.Length < 1) return;
    if (frame[0] == LinkFrame.Data)
    {
        if (LinkFrame.TryParseData(frame, out var packet, out var rssi)
            && PacketCodec.TryDeserialize(packet, out var pkt))
        {
            string body = pkt.Type switch
            {
                PacketType.Text => Encoding.UTF8.GetString(pkt.Payload),
                PacketType.Sos  => "🆘 SOS",
                PacketType.Ping => "PING/回覆",
                _ => $"type=0x{(byte)pkt.Type:X2}",
            };
            Console.WriteLine($"\n← [0x{pkt.SrcId:X4}→0x{pkt.DstId:X4}] {body}  (RSSI {rssi})");
            if (!probe) Console.Write("> ");

            // 點對點文字 → 回 ACK（廣播/群組不回），讓對方顯示「已送達」
            // 送 3 次避免單一封包回程遺失（每個 ACK 封包自己的 SEQ 不同，避免被去重）
            if (pkt.Type == PacketType.Text && DstId.IsUnicast(pkt.DstId))
            {
                for (int k = 0; k < 3; k++)
                {
                    var ack = new LoRaPacket
                    {
                        DstId = pkt.SrcId,
                        Seq = NextSeq(),
                        Type = PacketType.Ack,
                        Payload = new byte[] { (byte)(pkt.Seq >> 8), (byte)(pkt.Seq & 0xFF) },
                    };
                    sendLink(LinkFrame.WrapData(PacketCodec.Serialize(ack)));
                    Thread.Sleep(300);
                }
                Console.WriteLine($"→ ACK ×3 回 0x{pkt.SrcId:X4}（acked seq {pkt.Seq}）");
            }

            // echo 模式：收到文字 → 把原字回傳給發送者（3 份同 SEQ，接收端去重成一則）
            if (echo && pkt.Type == PacketType.Text)
            {
                ushort eseq = NextSeq();
                for (int k = 0; k < 3; k++)
                {
                    var ep = new LoRaPacket { DstId = pkt.SrcId, Seq = eseq, Type = PacketType.Text, Payload = pkt.Payload };
                    sendLink(LinkFrame.WrapData(PacketCodec.Serialize(ep)));
                    Thread.Sleep(300);
                }
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] → echo「{Encoding.UTF8.GetString(pkt.Payload)}」回 0x{pkt.SrcId:X4}");
            }
        }
    }
    else if (frame[0] == LinkFrame.Ctrl)
    {
        LinkFrame.TryParseCtrl(frame, out var json);
        Console.WriteLine($"\n← CTRL {json}");
        if (!probe) Console.Write("> ");
    }
}

// USB 模式專用：從累積緩衝抽幀（容忍混入 log，重同步）
void DrainFrames(List<byte> acc)
{
    while (acc.Count >= 3)
    {
        int len = (acc[0] << 8) | acc[1];
        byte t = acc[2];
        if (len < 1 || len > 255 || (t != LinkFrame.Data && t != LinkFrame.Ctrl))
        {
            acc.RemoveAt(0);
            continue;
        }
        if (acc.Count < 2 + len) break;
        var frame = acc.GetRange(2, len).ToArray();
        acc.RemoveRange(0, 2 + len);
        HandleFrame(frame);
    }
}

if (useWifi)
{
    Console.WriteLine($"LoRaPTT PC 客戶端 — WiFi UDP {ip}:5000，目標 0x{dst:X4}{(probe ? "（probe）" : "")}");
    Console.WriteLine("（請先確認 PC 的 WiFi 已連上該 C6L 的 AP）");
    var udp = new UdpClient();
    udp.Connect(new IPEndPoint(IPAddress.Parse(ip), 5000));
    sendLink = link => udp.Send(link, link.Length);
    shutdown = () => udp.Close();

    var reader = new Thread(() =>
    {
        var rep = new IPEndPoint(IPAddress.Any, 0);
        try { while (true) { var data = udp.Receive(ref rep); if (data.Length > 0) HandleFrame(data); } }
        catch (Exception ex) { Console.WriteLine("\n接收結束：" + ex.Message); }
    })
    { IsBackground = true };
    reader.Start();

    udp.Send(new byte[] { 0x00 }, 1); // 註冊封包：讓 C6L 記住本機 IP/Port
}
else
{
    Console.WriteLine($"LoRaPTT PC 客戶端 — USB {com} @115200（HWCDC 不穩，建議改 WiFi），目標 0x{dst:X4}");
    var port = new SerialPort(com!, 115200) { ReadTimeout = SerialPort.InfiniteTimeout, WriteTimeout = 2000 };
    port.Open();
    Console.WriteLine("已開埠，等待 C6L 就緒（約 5 秒）...");
    Thread.Sleep(5000);
    sendLink = link =>
    {
        var f = new byte[2 + link.Length];
        f[0] = (byte)(link.Length >> 8);
        f[1] = (byte)(link.Length & 0xFF);
        Array.Copy(link, 0, f, 2, link.Length);
        port.Write(f, 0, f.Length);
    };
    shutdown = () => port.Close();

    var reader = new Thread(() =>
    {
        var acc = new List<byte>(1024);
        var tmp = new byte[256];
        try
        {
            while (port.IsOpen)
            {
                int n = port.Read(tmp, 0, tmp.Length);
                if (n <= 0) continue;
                for (int i = 0; i < n; i++) acc.Add(tmp[i]);
                DrainFrames(acc);
            }
        }
        catch (Exception ex) { Console.WriteLine("\n讀取結束：" + ex.Message); }
    })
    { IsBackground = true };
    reader.Start();
}

// 握手（F-053）
sendLink(LinkFrame.WrapCtrl("{\"cmd\":\"hello\",\"name\":\"PC\"}"));
Console.WriteLine("→ 已送出握手 hello");

if (probe)
{
    if (sendText != null)
    {
        // 與 App 一致：3 份用「同一個 SEQ」重送（提升到達率），接收端依 SRC+SEQ
        // 去重成一則。若各份用不同 SEQ，接收端不會去重 → 同一訊息顯示多次。
        ushort seq = NextSeq();
        for (int k = 0; k < 3; k++)
        {
            var p = new LoRaPacket { DstId = dst, Seq = seq, Type = PacketType.Text, Payload = Encoding.UTF8.GetBytes(sendText) };
            sendLink(LinkFrame.WrapData(PacketCodec.Serialize(p)));
            Console.WriteLine($"→ 送出至 0x{dst:X4}：{sendText} (#{k + 1}, seq={seq})");
            Thread.Sleep(800);
        }
    }
    Thread.Sleep(listenSecs * 1000);
    shutdown();
    Console.WriteLine("probe 結束。");
    return;
}

Console.WriteLine("指令：直接打字送出文字；/to XXXX 設目標；/b 廣播；/q 離開");
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null || line == "/q") break;
    if (line == "/b") { dst = 0xFFFF; Console.WriteLine("目標 = 廣播 FFFF"); continue; }
    if (line.StartsWith("/to ", StringComparison.OrdinalIgnoreCase))
    {
        if (ushort.TryParse(line[4..].Trim(), NumberStyles.HexNumber, null, out var nd))
        { dst = nd; Console.WriteLine($"目標 = 0x{dst:X4}"); }
        else Console.WriteLine("ID 格式錯誤（4 位十六進位）");
        continue;
    }
    if (line.Length == 0) continue;

    var pkt = new LoRaPacket
    {
        DstId = dst,
        Seq = NextSeq(),
        Type = PacketType.Text,
        Payload = Encoding.UTF8.GetBytes(line),
    };
    sendLink(LinkFrame.WrapData(PacketCodec.Serialize(pkt)));
    Console.WriteLine($"→ 送出至 0x{dst:X4}：{line}");
}
shutdown();
Console.WriteLine("已關閉。");
