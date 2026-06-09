using System.Globalization;
using System.IO.Ports;
using System.Text;
using LoRaPTT.Services.Protocol;

// ── LoRaPTT PC 客戶端（陽春版）──────────────────────────────────
// 透過 USB Serial（CDC）接 C6L，當作第二個端點：文字收發 + 握手。
// 重用 App 的協定碼（PacketCodec / LinkFrame）確保位元級一致。
//
// 用法：
//   dotnet run --project pc-client -- <COM埠> [目標HEX] [probe]
//   例：dotnet run --project pc-client -- COM7 FFFF
//   probe 模式：連線+握手後聽 8 秒就退出（自動化驗收用，不進互動迴圈）。
// ────────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

string portName = args.FirstOrDefault(a => a.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) ?? "COM7";
bool probe = args.Any(a => a.Equals("probe", StringComparison.OrdinalIgnoreCase));
ushort dst = 0xFFFF;
foreach (var a in args)
    if (a.Length == 4 && ushort.TryParse(a, NumberStyles.HexNumber, null, out var d)) dst = d;

Console.WriteLine($"LoRaPTT PC 客戶端 — 連 {portName} @115200，預設目標 0x{dst:X4}{(probe ? "（probe 模式）" : "")}");
if (!probe)
    Console.WriteLine("指令：直接打字送出文字；/to XXXX 設目標；/b 廣播；/q 離開");

var port = new SerialPort(portName, 115200)
{
    ReadTimeout = SerialPort.InfiniteTimeout,
    WriteTimeout = 2000,
};

try { port.Open(); }
catch (Exception ex) { Console.WriteLine($"開埠失敗：{ex.Message}"); return; }

Console.WriteLine("已開埠，等待 C6L 就緒（HWCDC 開埠可能重置，約 5 秒）...");
Thread.Sleep(5000);

int seqCounter = 0;
ushort NextSeq() => (ushort)(Interlocked.Increment(ref seqCounter) & 0xFFFF);

void SendFrame(byte[] link)
{
    // USB 幀：2-byte 大端長度前綴 + LinkFrame
    var f = new byte[2 + link.Length];
    f[0] = (byte)(link.Length >> 8);
    f[1] = (byte)(link.Length & 0xFF);
    Array.Copy(link, 0, f, 2, link.Length);
    port.Write(f, 0, f.Length);
}

// 從累積緩衝抽出完整幀；遇到混入的 log 文字會自動重同步（丟 1 byte 再試）。
// 合法幀判定：長度 1~255 且首位元組為 LinkFrame.Data(0x01)/Ctrl(0x02)。
void DrainFrames(List<byte> acc)
{
    while (acc.Count >= 3)
    {
        int len = (acc[0] << 8) | acc[1];
        byte type = acc[2];
        if (len < 1 || len > 255 || (type != LinkFrame.Data && type != LinkFrame.Ctrl))
        {
            acc.RemoveAt(0); // 雜訊 → 重同步
            continue;
        }
        if (acc.Count < 2 + len) break; // 等更多資料
        var frame = acc.GetRange(2, len).ToArray();
        acc.RemoveRange(0, 2 + len);
        HandleFrame(frame);
    }
}

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
        }
    }
    else if (frame[0] == LinkFrame.Ctrl)
    {
        LinkFrame.TryParseCtrl(frame, out var json);
        Console.WriteLine($"\n← CTRL {json}");
        if (!probe) Console.Write("> ");
    }
}

// 讀取執行緒：累積位元組 → DrainFrames 抽幀（容忍混入的 log 文字）
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

// 握手（F-053）
SendFrame(LinkFrame.WrapCtrl("{\"cmd\":\"hello\",\"name\":\"PC\"}"));
Console.WriteLine("→ 已送出握手 hello");

if (probe)
{
    Thread.Sleep(8000); // 聽 8 秒收訊後退出
    port.Close();
    Console.WriteLine("probe 結束。");
    return;
}

// 互動迴圈：鍵盤輸入 → 送文字
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
    SendFrame(LinkFrame.WrapData(PacketCodec.Serialize(pkt)));
    Console.WriteLine($"→ 送出至 0x{dst:X4}：{line}");
}

port.Close();
Console.WriteLine("已關閉。");
