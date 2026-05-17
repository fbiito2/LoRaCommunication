using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoRaPTT.Services;
using Microsoft.Extensions.Logging;

namespace LoRaPTT.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IBleService          _ble;
    private readonly IAudioRecordService  _record;
    private readonly IAudioPlayService    _play;
    private readonly Codec2Service        _codec2;
    private readonly ILogger<MainViewModel> _logger;

    private CancellationTokenSource? _pttCts;

    // ── 狀態屬性（UI 綁定）──────────────────────────────────
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isPttActive;
    [ObservableProperty] private string _statusMessage = "未連線";

    // 接收 buffer（累積 10 幀再解碼）
    private readonly List<byte> _rxBuffer = new();

    public MainViewModel(
        IBleService ble,
        IAudioRecordService record,
        IAudioPlayService play,
        Codec2Service codec2,
        ILogger<MainViewModel> logger)
    {
        _ble    = ble;
        _record = record;
        _play   = play;
        _codec2 = codec2;
        _logger = logger;

        _ble.OnDataReceived      += OnBleDataReceived;
        _ble.OnConnectionChanged += OnConnectionChanged;
    }

    // ── 連線 ──────────────────────────────────────────────
    [RelayCommand]
    private async Task ConnectAsync()
    {
        StatusMessage = "掃描中...";
        var ok = await _ble.ScanAndConnectAsync();
        StatusMessage = ok ? "已連線" : "連線失敗";
    }

    // ── PTT 按下（開始錄音+編碼+傳送）────────────────────
    [RelayCommand]
    private async Task PttPressAsync()
    {
        if (!IsConnected || IsPttActive) return;
        IsPttActive = true;
        StatusMessage = "傳送中...";

        _pttCts = new CancellationTokenSource();
        var packet = new List<byte>(); // 累積 10 幀（200ms）

        await _record.StartAsync(async pcmFrame =>
        {
            var encoded = _codec2.Encode(pcmFrame);
            packet.AddRange(encoded);

            if (packet.Count >= Codec2Service.BytesPerFrame * Codec2Service.FramesPerPacket)
            {
                await _ble.SendAsync(packet.ToArray());
                packet.Clear();
            }
        }, _pttCts.Token);
    }

    // ── PTT 放開（停止錄音）───────────────────────────────
    [RelayCommand]
    private async Task PttReleaseAsync()
    {
        if (!IsPttActive) return;
        _pttCts?.Cancel();
        await _record.StopAsync();
        IsPttActive = false;
        StatusMessage = "已連線";
    }

    // ── 接收 BLE 資料 → Codec2 解碼 → 播放 ──────────────
    private void OnBleDataReceived(byte[] data)
    {
        _rxBuffer.AddRange(data);

        // 每 6 bytes 解碼一幀
        while (_rxBuffer.Count >= Codec2Service.BytesPerFrame)
        {
            var frame = _rxBuffer.GetRange(0, Codec2Service.BytesPerFrame).ToArray();
            _rxBuffer.RemoveRange(0, Codec2Service.BytesPerFrame);

            var pcm = _codec2.Decode(frame);
            _ = _play.PlayPcmAsync(pcm);
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        IsConnected   = connected;
        StatusMessage = connected ? "已連線" : "已斷線";
    }
}
