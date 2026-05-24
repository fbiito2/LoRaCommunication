using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoRaPTT.Services;
using Microsoft.Extensions.Logging;

namespace LoRaPTT.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ICommService         _comm;
    private readonly IAudioRecordService  _record;
    private readonly IAudioPlayService    _play;
    private readonly Codec2Service        _codec2;
    private readonly ILogger<MainViewModel> _logger;

    private CancellationTokenSource? _pttCts;

    // ── UI 綁定屬性 ────────────────────────────────────────
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isPttActive;
    [ObservableProperty] private string _statusMessage = "未連線";
    [ObservableProperty] private string _modeLabel     = "";

    // 接收 Codec2 bytes buffer（累積 6 bytes 解碼一幀）
    private readonly List<byte> _rxBuffer = new();

    public MainViewModel(
        ICommService comm,
        IAudioRecordService record,
        IAudioPlayService play,
        Codec2Service codec2,
        ILogger<MainViewModel> logger)
    {
        _comm   = comm;
        _record = record;
        _play   = play;
        _codec2 = codec2;
        _logger = logger;

        _comm.OnDataReceived      += OnCommDataReceived;
        _comm.OnConnectionChanged += OnConnectionChanged;
    }

    // ── 連線（自動根據目前模式：WiFi 或 USB）────────────────
    [RelayCommand]
    private async Task ConnectAsync()
    {
        StatusMessage = "連線中...";
        ModeLabel     = _comm.Mode == CommMode.WiFi ? "WiFi 模式" : "USB 模式";
        try
        {
            await _comm.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "連線失敗");
            StatusMessage = $"連線失敗：{ex.Message}";
        }
    }

    // ── PTT 按下（開始錄音 → Codec2 encode → 傳送）──────────
    [RelayCommand]
    private async Task PttPressAsync()
    {
        if (!IsConnected || IsPttActive) return;
        IsPttActive   = true;
        StatusMessage = "傳送中...";

        _pttCts = new CancellationTokenSource();
        var packet = new List<byte>();

        await _record.StartAsync(async pcmFrame =>
        {
            var encoded = _codec2.Encode(pcmFrame);
            packet.AddRange(encoded);

            // 累積 10 幀（200ms）送一包
            if (packet.Count >= Codec2Service.BytesPerFrame * Codec2Service.FramesPerPacket)
            {
                try { await _comm.SendAsync(packet.ToArray()); }
                catch (Exception ex) { _logger.LogError(ex, "傳送語音封包失敗"); }
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
        IsPttActive   = false;
        StatusMessage = "已連線";
    }

    // ── 接收資料 → Codec2 解碼 → 播放 ────────────────────
    private void OnCommDataReceived(byte[] data)
    {
        // 只取 payload 部分（跳過封包 header 8B + MAC 4B）
        // 暫時簡化處理：直接視為 Codec2 bytes
        _rxBuffer.AddRange(data);

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
        StatusMessage = connected ? $"已連線（{ModeLabel}）" : "已斷線";
    }
}
