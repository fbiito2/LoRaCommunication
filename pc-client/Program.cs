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

void ReadExact(byte[] buf, int count)
{
    int got = 0;
    while (got < count)
    {
        int n = port.Read(buf, got, count - got);
        if (n <= 0) throw new EndOfStreamException();
        got += n;
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

// 讀取執行緒：解析 [len][LinkFrame]
var reader = new Thread(() =>
{
    var two = new byte[2];
    try
    {
        while (port.IsOpen)
        {
            ReadExact(two, 2);
            int len = (two[0] << 8) | two[1];
            if (len <= 0 || len > 512) continue; // 長度異常，略過（陽春重同步）
            var data = new byte[len];
            ReadExact(data, len);
            HandleFrame(data);
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
