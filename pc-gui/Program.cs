using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LoRaPTT.Services.Protocol;

namespace LoRaPTT.WinClient;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

/// <summary>
/// LoRaPTT PC 客戶端（WinForms）。WiFi UDP 連 C6L AP，當第二個端點：
/// 顯示連上的裝置 ID、對話記錄（誰傳了什麼）、輸入框 + 發送鈕。
/// 與 App 共用協定碼（PacketCodec/LinkFrame）確保位元級一致。
/// </summary>
public sealed class MainForm : Form
{
    private readonly TextBox _ip;
    private readonly Button _connectBtn;
    private readonly Label _deviceLbl;
    private readonly TextBox _target;
    private readonly TextBox _log;
    private readonly TextBox _input;
    private readonly Button _sendBtn;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _keepAlive; // 閒置心跳：免得 C6L(5分TTL)/防火牆把連線當過期
    private System.Threading.Timer? _ackTimer;  // 握手回應逾時：沒收到 hello-ack 就標連線失敗
    private volatile bool _connected;            // 是否已收到裝置 hello-ack（真正連上）
    private readonly Dictionary<ushort, (double lat, double lon)> _lastPos = new(); // 各節點上次定位（去重）
    private int _seq;

    public MainForm()
    {
        Text = "LoRaPTT PC 客戶端";
        Width = 540;
        Height = 660;
        Font = new Font("Microsoft JhengHei UI", 10F);
        StartPosition = FormStartPosition.CenterScreen;

        // ── 頂部：IP / 連線 / 裝置 ID / 目標 ──
        var top = new Panel { Dock = DockStyle.Top, Height = 76 };

        var ipLbl = new Label { Text = "裝置 IP", AutoSize = true, Location = new Point(10, 14) };
        _ip = new TextBox { Text = "192.168.4.1", Location = new Point(70, 11), Width = 110 };
        _connectBtn = new Button { Text = "連線", Location = new Point(190, 9), Width = 70 };
        _connectBtn.Click += (_, _) => Connect();

        var tgtLbl = new Label { Text = "目標", AutoSize = true, Location = new Point(285, 14) };
        _target = new TextBox { Text = "FFFF", Location = new Point(325, 11), Width = 70 };
        var tgtHint = new Label { Text = "FFFF=廣播", AutoSize = true, Location = new Point(400, 14), ForeColor = Color.Gray };

        _deviceLbl = new Label { Text = "● 未連線", AutoSize = true, Location = new Point(10, 46), ForeColor = Color.Gray,
            Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold) };

        top.Controls.AddRange(new Control[] { ipLbl, _ip, _connectBtn, tgtLbl, _target, tgtHint, _deviceLbl });

        // ── 對話記錄 ──
        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Font = new Font("Consolas", 10F),
        };

        // ── 底部：輸入 + 發送 ──
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(6) };
        _sendBtn = new Button { Text = "發送", Dock = DockStyle.Right, Width = 90 };
        _sendBtn.Click += (_, _) => Send();
        _input = new TextBox { Dock = DockStyle.Fill };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Send(); }
        };
        bottom.Controls.Add(_input);
        bottom.Controls.Add(_sendBtn);

        Controls.Add(_log);
        Controls.Add(bottom);
        Controls.Add(top);

        FormClosing += (_, _) => { _keepAlive?.Dispose(); _ackTimer?.Dispose(); _cts?.Cancel(); _udp?.Close(); };
    }

    private ushort NextSeq() => (ushort)(Interlocked.Increment(ref _seq) & 0xFFFF);

    private void AppendLog(string line)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke((Action)(() => AppendLog(line))); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
    }

    /// <summary>更新頂部連線狀態欄（任何執行緒可呼叫，會自動切回 UI 執行緒）</summary>
    private void SetStatus(string text, Color color)
    {
        if (_deviceLbl.InvokeRequired) { _deviceLbl.BeginInvoke((Action)(() => SetStatus(text, color))); return; }
        _deviceLbl.Text = text;
        _deviceLbl.ForeColor = color;
    }

    /// <summary>收到裝置 hello-ack → 確定連上</summary>
    private void SetDevice(ushort id, string? fw)
    {
        _connected = true;
        _ackTimer?.Dispose(); _ackTimer = null; // 取消逾時判定
        SetStatus($"✅ 已連線　裝置 0x{id:X4}（韌體 {fw ?? "?"}）", Color.Green);
        AppendLog($"✅ 已連線：裝置 0x{id:X4}，韌體 {fw ?? "?"}");
    }

    // ── 連線 ──
    private void Connect()
    {
        try
        {
            _cts?.Cancel();
            _udp?.Close();
            _udp = new UdpClient();
            _udp.Connect(new IPEndPoint(IPAddress.Parse(_ip.Text.Trim()), 5000));
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var udp = _udp;
            new Thread(() => RecvLoop(udp, ct)) { IsBackground = true }.Start();

            _connected = false;
            udp.Send(new byte[] { 0x00 }, 1); // 註冊封包，讓 C6L 記住本機 IP
            SendLink(LinkFrame.WrapCtrl("{\"cmd\":\"hello\",\"name\":\"PC\"}"));
            AppendLog($"→ 連線 {_ip.Text}:5000，已送握手 hello，等待裝置回應…");
            SetStatus("● 連線中…已送握手，等待裝置回應", Color.DarkOrange);

            // 握手回應逾時（5 秒沒收到 hello-ack）→ 標明連線可能失敗，讓使用者知道狀況
            _ackTimer?.Dispose();
            _ackTimer = new System.Threading.Timer(_ =>
            {
                if (!_connected)
                {
                    SetStatus("⚠ 未收到裝置回應　確認 IP 正確、且已連上 C6L 的 WiFi", Color.Red);
                    AppendLog("⚠ 5 秒內未收到裝置 hello-ack：IP 錯誤、未連上 C6L WiFi、或防火牆擋回程。可再按一次「連線」。");
                }
            }, null, 5_000, Timeout.Infinite);

            // 每 60 秒送 1 byte 心跳：閒置時也能維持在 C6L client 清單裡，
            // 並讓 Windows 防火牆的 UDP 回程映射保持開啟，免得「一段時間沒動作就收不到、要重按連線」。
            _keepAlive?.Dispose();
            _keepAlive = new System.Threading.Timer(
                _ => { try { udp.Send(new byte[] { 0x00 }, 1); } catch { /* 已斷線，忽略 */ } },
                null, 60_000, 60_000);
        }
        catch (Exception ex)
        {
            SetStatus("⚠ 連線失敗：" + ex.Message, Color.Red);
            AppendLog("連線失敗：" + ex.Message);
        }
    }

    private void SendLink(byte[] link)
    {
        try { _udp?.Send(link, link.Length); }
        catch (Exception ex) { AppendLog("送出失敗：" + ex.Message); }
    }

    // ── 接收迴圈（背景執行緒）──
    private void RecvLoop(UdpClient udp, CancellationToken ct)
    {
        var rep = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var data = udp.Receive(ref rep);
                if (data.Length > 0) HandleFrame(data);
            }
        }
        catch { /* 連線關閉 */ }
    }

    private void HandleFrame(byte[] frame)
    {
        if (frame.Length < 1) return;

        if (frame[0] == LinkFrame.Data)
        {
            if (LinkFrame.TryParseData(frame, out var packet, out var rssi)
                && PacketCodec.TryDeserialize(packet, out var pkt))
            {
                switch (pkt.Type)
                {
                    case PacketType.Text:
                        AppendLog($"0x{pkt.SrcId:X4} → 0x{pkt.DstId:X4}：{Encoding.UTF8.GetString(pkt.Payload)}  (RSSI {rssi})");
                        // 點對點文字回 ACK ×3（避單發遺失）
                        if (DstId.IsUnicast(pkt.DstId))
                            for (int k = 0; k < 3; k++)
                            {
                                var ack = new LoRaPacket
                                {
                                    DstId = pkt.SrcId,
                                    Seq = NextSeq(),
                                    Type = PacketType.Ack,
                                    Payload = new[] { (byte)(pkt.Seq >> 8), (byte)(pkt.Seq & 0xFF) },
                                };
                                SendLink(LinkFrame.WrapData(PacketCodec.Serialize(ack)));
                                Thread.Sleep(250);
                            }
                        break;
                    case PacketType.Sos:
                    {
                        // payload：[DeviceID 2B][Lat 8B double][Lon 8B double][附加文字 NB]
                        // C6L 實體按鈕求救僅 2B(無 GPS)；有定位則 18B+。格式同 App SendSosAsync。
                        var p = pkt.Payload;
                        string loc = "無定位";
                        string extra = "";
                        if (p.Length >= 18)
                        {
                            double lat = BitConverter.ToDouble(p, 2);
                            double lon = BitConverter.ToDouble(p, 10);
                            if (lat != 0 || lon != 0) loc = $"📍 {lat:F6}, {lon:F6}";
                            if (p.Length > 18) extra = "　💬 " + Encoding.UTF8.GetString(p, 18, p.Length - 18);
                        }
                        AppendLog($"🆘🆘 SOS 緊急求救！來自 0x{pkt.SrcId:X4}　{loc}{extra}  (RSSI {rssi})");
                        try { System.Media.SystemSounds.Exclamation.Play(); } catch { /* 無音效裝置忽略 */ }
                        break;
                    }
                    case PacketType.Pos:
                    {
                        // 定位廣播（F-074，每 30 秒/移動時）。payload：[DeviceID 2B][Lat 8B][Lon 8B]
                        var p = pkt.Payload;
                        if (p.Length >= 18)
                        {
                            double lat = BitConverter.ToDouble(p, 2);
                            double lon = BitConverter.ToDouble(p, 10);
                            // 座標沒變就不重印（靜止節點每 30 秒重發同座標，避免洗版）
                            if (!_lastPos.TryGetValue(pkt.SrcId, out var prev) || prev.lat != lat || prev.lon != lon)
                            {
                                _lastPos[pkt.SrcId] = (lat, lon);
                                AppendLog($"📍 0x{pkt.SrcId:X4} 定位 {lat:F6}, {lon:F6}  (RSSI {rssi})");
                            }
                        }
                        break;
                    }
                    case PacketType.Ack:
                        // 對方已收到我的點對點訊息
                        break;
                    // PING / 其他不記錄到對話框，避免洗版
                }
            }
        }
        else if (frame[0] == LinkFrame.Ctrl)
        {
            LinkFrame.TryParseCtrl(frame, out var json);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("device_id", out var idEl) && idEl.TryGetUInt16(out var id))
                {
                    string? fw = root.TryGetProperty("fw_ver", out var fwEl) ? fwEl.GetString() : null;
                    SetDevice(id, fw);
                }
            }
            catch { }
        }
    }

    // ── 發送文字 ──
    private void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        if (_udp is null) { AppendLog("尚未連線，請先按「連線」"); return; }

        ushort dst = 0xFFFF;
        var t = _target.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (t.Length == 4) ushort.TryParse(t, NumberStyles.HexNumber, null, out dst);

        var pkt = new LoRaPacket
        {
            DstId = dst,
            Seq = NextSeq(),
            Type = PacketType.Text,
            Payload = Encoding.UTF8.GetBytes(text),
        };
        var bytes = LinkFrame.WrapData(PacketCodec.Serialize(pkt));
        // 送 3 次避單發遺失（背景執行，不卡 UI）
        Task.Run(() =>
        {
            for (int k = 0; k < 3; k++) { SendLink(bytes); Thread.Sleep(150); }
        });

        AppendLog($"我 → 0x{dst:X4}：{text}");
        _input.Clear();
    }
}
