using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace LoRaPTT.Services;

/// <summary>基站模式：WiFi UDP 通訊（連接 C6L WiFi AP，port 5000）</summary>
public class WiFiCommService : ICommService
{
    private const int UDP_PORT    = 5000;
    private const int RECV_BUFFER = 512;

    private readonly ILogger<WiFiCommService> _logger;
    private UdpClient?  _udp;
    private IPEndPoint? _remoteEp;
    private CancellationTokenSource? _recvCts;
    private CancellationTokenSource? _hbCts;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public bool     IsConnected { get; private set; }
    public CommMode Mode        => CommMode.WiFi;

    public event Action<byte[]>? OnDataReceived;
    public event Action<bool>?   OnConnectionChanged;

    public WiFiCommService(ILogger<WiFiCommService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 連接 C6L WiFi AP。
    /// 手機需已連上 C6L 的 WiFi（SSID: LoRaPTT），C6L IP 固定為 192.168.4.1
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) { Console.WriteLine("LPTT: 已連線，忽略重複連接"); return; }
        const string c6lIp = "192.168.4.1";
        _logger.LogInformation("WiFi UDP 連接至 {IP}:{Port}", c6lIp, UDP_PORT);

        _udp?.Close(); // 重連時清掉舊 socket
        _udp      = new UdpClient();
        _remoteEp = new IPEndPoint(IPAddress.Parse(c6lIp), UDP_PORT);

        try
        {
            // 發送空封包讓 C6L 知道手機 IP（觸發 clientKnown）
            await _udp.SendAsync(new byte[] { 0x00 }, 1, _remoteEp).WaitAsync(ct);
            Console.WriteLine("LPTT: 連線—已送出註冊封包到 192.168.4.1:5000");
        }
        catch (Exception ex)
        {
            Console.WriteLine("LPTT: 連線送出失敗: " + ex.Message);
            throw;
        }

        IsConnected = true;
        OnConnectionChanged?.Invoke(true);

        // 啟動接收迴圈
        _recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ReceiveLoopAsync(_recvCts.Token);

        // 啟動心跳偵測（定期 GET /version，連續失敗 → 判定離線，使用者可重連）
        _hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = HeartbeatLoopAsync(_hbCts.Token);

        _logger.LogInformation("WiFi UDP 連線成功");
        Console.WriteLine("LPTT: WiFi 連線完成，接收迴圈啟動");
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_udp is null || _remoteEp is null || !IsConnected)
            throw new InvalidOperationException("WiFi 未連線");

        await _udp.SendAsync(data, data.Length, _remoteEp)
                  .WaitAsync(ct);
    }

    public Task DisconnectAsync()
    {
        _recvCts?.Cancel();
        _hbCts?.Cancel();
        _udp?.Close();
        _udp        = null;
        IsConnected = false;
        OnConnectionChanged?.Invoke(false);
        return Task.CompletedTask;
    }

    // ── 心跳：定期 GET /version，連續 3 次逾時 → 判定 C6L 離線 ───────
    // UDP 無連線狀態，裝置斷電/離開 WiFi 時收不到任何錯誤；靠主動探測補上。
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        const string url = "http://192.168.4.1/version";
        int fails = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                bool ok;
                try
                {
                    using var resp = await _http.GetAsync(url, ct);
                    ok = resp.IsSuccessStatusCode;
                }
                catch { ok = false; }

                if (ok) { fails = 0; }
                else if (++fails >= 3)
                {
                    _logger.LogWarning("心跳連續逾時，判定 C6L 離線");
                    Console.WriteLine("LPTT: 心跳逾時 → 裝置離線");
                    MarkDisconnected();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* 正常結束 */ }
    }

    /// <summary>標記為離線（心跳逾時）。之後使用者可再按「連接」重連。</summary>
    private void MarkDisconnected()
    {
        if (!IsConnected) return;
        IsConnected = false;
        _recvCts?.Cancel();
        OnConnectionChanged?.Invoke(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("WiFi UDP 接收迴圈啟動");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _udp!.ReceiveAsync(ct);
                if (result.Buffer?.Length > 0)
                {
                    Console.WriteLine($"LPTT: 收到 UDP {result.Buffer.Length}B 來自 {result.RemoteEndPoint}");
                    OnDataReceived?.Invoke(result.Buffer);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常結束 */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WiFi UDP 接收迴圈發生錯誤");
            IsConnected = false;
            OnConnectionChanged?.Invoke(false);
        }
    }
}
