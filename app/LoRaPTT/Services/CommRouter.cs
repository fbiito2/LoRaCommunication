namespace LoRaPTT.Services;

/// <summary>
/// 傳輸層路由（F-054）。對上層（MessagingService）呈現單一 <see cref="ICommService"/>，
/// 內部視情況走 USB 或 WiFi：
///   ConnectAsync 先試 USB（若有 Android 實作且偵測到 C6L）；失敗或無 USB → 落回 WiFi。
/// 事件僅由「目前作用中的傳輸層」往上轉發。
/// </summary>
public sealed class CommRouter : ICommService
{
    private readonly WiFiCommService _wifi;
    private readonly IUsbCommService? _usb;
    private readonly IBackgroundKeepAlive _keepAlive;
    private ICommService _active;

    public CommRouter(WiFiCommService wifi, IEnumerable<IUsbCommService> usb, IBackgroundKeepAlive keepAlive)
    {
        _wifi = wifi;
        _usb = usb.FirstOrDefault();   // 非 Android 平台為空
        _keepAlive = keepAlive;
        _active = wifi;

        Hook(_wifi);
        if (_usb is not null) Hook(_usb);
    }

    private void Hook(ICommService s)
    {
        s.OnDataReceived      += d => { if (ReferenceEquals(s, _active)) OnDataReceived?.Invoke(d); };
        s.OnConnectionChanged += c =>
        {
            if (!ReferenceEquals(s, _active)) return;
            // 連線狀態驅動背景保活：連上→啟動前景服務維持收訊；斷線→停止
            if (c) _keepAlive.Start(); else _keepAlive.Stop();
            OnConnectionChanged?.Invoke(c);
        };
    }

    public bool     IsConnected => _active.IsConnected;
    public CommMode Mode        => _active.Mode;

    public event Action<byte[]>? OnDataReceived;
    public event Action<bool>?   OnConnectionChanged;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // 先試 USB（偵測到 C6L 才會成功）；失敗就落回 WiFi
        if (_usb is not null)
        {
            try
            {
                _active = _usb;
                await _usb.ConnectAsync(ct);
                return;
            }
            catch
            {
                // USB 無裝置/權限被拒/不穩 → 改走 WiFi
            }
        }
        _active = _wifi;
        await _wifi.ConnectAsync(ct);
    }

    public Task DisconnectAsync() => _active.DisconnectAsync();

    public Task SendAsync(byte[] data, CancellationToken ct = default) => _active.SendAsync(data, ct);
}
