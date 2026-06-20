using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    private readonly Label _posLbl;   // 裝置狀態下方就地顯示各節點定位（不洗版）
    private readonly TextBox _target;
    private readonly TextBox _groupInput;          // 群組 ID 輸入（加入/退出）
    private readonly Label _groupsLbl;             // 顯示已加入的群組
    private readonly HashSet<ushort> _groups = new(); // 已加入的群組 ID（FFE0~FFEF）
    private readonly TextBox _log;
    private readonly TextBox _input;
    private readonly Button _sendBtn;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _keepAlive; // 閒置心跳：免得 C6L(5分TTL)/防火牆把連線當過期
    private System.Threading.Timer? _ackTimer;  // 握手回應逾時：沒收到 hello-ack 就標連線失敗
    private volatile bool _connected;            // 是否已收到裝置 hello-ack（真正連上）
    private System.Threading.Timer? _gpsTimer;   // 定時抓「直連那台」的 /version 顯示其 GPS
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private int _seq;

    public MainForm()
    {
        Text = "LoRaPTT PC 客戶端";
        Width = 540;
        Height = 660;
        Font = new Font("Microsoft JhengHei UI", 10F);
        StartPosition = FormStartPosition.CenterScreen;

        // ── 頂部：IP / 連線 / 裝置 ID / 目標 / 群組 ──
        var top = new Panel { Dock = DockStyle.Top, Height = 150 };

        var ipLbl = new Label { Text = "裝置 IP", AutoSize = true, Location = new Point(10, 14) };
        _ip = new TextBox { Text = "192.168.4.1", Location = new Point(70, 11), Width = 110 };
        _connectBtn = new Button { Text = "連線", Location = new Point(190, 9), Width = 70 };
        _connectBtn.Click += (_, _) => Connect();

        var tgtLbl = new Label { Text = "目標", AutoSize = true, Location = new Point(285, 14) };
        _target = new TextBox { Text = "FFFF", Location = new Point(325, 11), Width = 70 };
        var tgtHint = new Label { Text = "FFFF=廣播", AutoSize = true, Location = new Point(400, 14), ForeColor = Color.Gray };

        _deviceLbl = new Label { Text = "● 未連線", AutoSize = true, Location = new Point(10, 46), ForeColor = Color.Gray,
            Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold) };

        // 定位列：在裝置狀態下方就地顯示各節點座標（更新而非洗版）
        _posLbl = new Label { Text = "", AutoSize = true, Location = new Point(10, 68), ForeColor = Color.FromArgb(0, 120, 0),
            Font = new Font("Microsoft JhengHei UI", 9F) };

        // 群組：加入/退出（FFE0~FFEF）。要發群組訊息就把「目標」打成該群組 ID。
        var grpLbl = new Label { Text = "群組", AutoSize = true, Location = new Point(10, 96) };
        _groupInput = new TextBox { Text = "", Location = new Point(55, 93), Width = 60 };
        var joinBtn = new Button { Text = "加入", Location = new Point(120, 91), Width = 55 };
        joinBtn.Click += (_, _) => JoinGroup();
        var leaveBtn = new Button { Text = "退出", Location = new Point(180, 91), Width = 55 };
        leaveBtn.Click += (_, _) => LeaveGroup();
        _groupsLbl = new Label { Text = "已加入: (無)", AutoSize = true, Location = new Point(245, 96), ForeColor = Color.Gray };

        top.Controls.AddRange(new Control[] { ipLbl, _ip, _connectBtn, tgtLbl, _target, tgtHint, _deviceLbl, _posLbl,
            grpLbl, _groupInput, joinBtn, leaveBtn, _groupsLbl });

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

        FormClosing += (_, _) => { _keepAlive?.Dispose(); _ackTimer?.Dispose(); _gpsTimer?.Dispose(); _cts?.Cancel(); _udp?.Close(); };
    }

    private ushort NextSeq() => (ushort)(Interlocked.Increment(ref _seq) & 0xFFFF);

    private void AppendLog(string line)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke((Action)(() => AppendLog(line))); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
    }

    // ── 群組加入/退出（F-021）。發群組訊息把「目標」打成群組 ID 即可。──
    private static bool TryParseGroupId(string s, out ushort id)
    {
        id = 0;
        var t = s?.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(t)
            && ushort.TryParse(t, NumberStyles.HexNumber, null, out id)
            && DstId.IsGroup(id);
    }
    private void JoinGroup()
    {
        if (!TryParseGroupId(_groupInput.Text, out var id)) { AppendLog("群組 ID 須為 FFE0~FFEF"); return; }
        _groups.Add(id);
        _groupInput.Clear();
        UpdateGroupsLabel();
        AppendLog($"已加入群組 0x{id:X4}");
    }
    private void LeaveGroup()
    {
        if (!TryParseGroupId(_groupInput.Text, out var id)) { AppendLog("輸入要退出的群組 ID（FFE0~FFEF）"); return; }
        if (_groups.Remove(id)) { _groupInput.Clear(); UpdateGroupsLabel(); AppendLog($"已退出群組 0x{id:X4}"); }
        else AppendLog($"未加入群組 0x{id:X4}");
    }
    private void UpdateGroupsLabel()
    {
        if (_groupsLbl.InvokeRequired) { _groupsLbl.BeginInvoke((Action)UpdateGroupsLabel); return; }
        _groupsLbl.Text = _groups.Count == 0 ? "已加入: (無)" : "已加入: " + string.Join(", ", _groups.Select(g => $"0x{g:X4}"));
    }

    /// <summary>就地更新 GPS 定位列（只顯示握手那台自己的 GPS）。</summary>
    private void SetGps(string text, Color color)
    {
        if (_posLbl.InvokeRequired) { _posLbl.BeginInvoke((Action)(() => SetGps(text, color))); return; }
        _posLbl.Text = text;
        _posLbl.ForeColor = color;
    }

    /// <summary>定時抓「連線裝置」的 /version，顯示它自己的 GPS 狀況（免一直按 C6L 按鈕看 OLED）。
    /// 自己的定位走 LoRa POS 收不回來（split-horizon），故改用 HTTP 遙測。</summary>
    private async void PollGps()
    {
        var ip = _ip.Text.Trim();
        try
        {
            using var resp = await _http.GetAsync($"http://{ip}/version");
            if (!resp.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            bool fix   = r.TryGetProperty("gps_fix", out var f) && f.GetBoolean();
            int  sats  = r.TryGetProperty("gps_sats", out var s) ? s.GetInt32() : 0;
            double lat = r.TryGetProperty("gps_lat", out var la) ? la.GetDouble() : 0;
            double lon = r.TryGetProperty("gps_lon", out var lo) ? lo.GetDouble() : 0;
            long bytes = r.TryGetProperty("gps_bytes", out var b) ? b.GetInt64() : 0;
            int  baud  = r.TryGetProperty("gps_baud", out var bd) ? bd.GetInt32() : 0;
            int  rx    = r.TryGetProperty("gps_rx", out var rxp) ? rxp.GetInt32() : 0;

            // 握手那台自己的 GPS。無定位時帶診斷(bytes 有增=模組有送;baud 一直跳=沒鎖定)
            if (fix)
                SetGps($"GPS 定位 {lat:F6}, {lon:F6}（衛星 {sats}）", Color.FromArgb(0, 120, 0));
            else
                SetGps($"GPS 無定位　衛星 {sats}　收 {bytes}B baud {baud} rx{rx}", Color.DarkOrange);
        }
        catch { /* 抓不到(未連/逾時) → 不更新，保留上次 */ }
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

            // 每 4 秒抓連線裝置的 /version → 就地顯示其 GPS（免一直按 C6L 按鈕看 OLED）
            _gpsTimer?.Dispose();
            _gpsTimer = new System.Threading.Timer(_ => PollGps(), null, 1_000, 4_000);
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
                        // 群組訊息只顯示「已加入該群」的(F-022);點對點/廣播照常
                        if (DstId.IsGroup(pkt.DstId) && !_groups.Contains(pkt.DstId)) break;
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
