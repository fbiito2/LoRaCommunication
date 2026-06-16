using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoRaPTT.Models;
using LoRaPTT.Services;
using LoRaPTT.Services.Protocol;
using Microsoft.Extensions.Logging;
using Contact = LoRaPTT.Models.Contact;

namespace LoRaPTT.ViewModels;

/// <summary>
/// PTT 語音 ViewModel（F-052）。按住說話：錄音 → Codec2 encode → 廣播 TYPE_VOICE；
/// 同時通知 C6L 全網切 SF7/BW500 高速模式。放開或滿 30 秒自動結束、切回長距。
/// 收訊：訂閱 MessagingService 的語音幀 → Codec2 decode → 播放（半雙工：自己說話時不播）。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int MaxPttSeconds = 30; // 單次 PTT 上限（duty-cycle 合規 + 半雙工公平）

    private readonly ICommService        _comm;
    private readonly IMessagingService   _messaging;
    private readonly ChatViewModel       _chat;   // 共用文字頁的對象選擇/聯絡人（單例）
    private readonly IAudioRecordService _record;
    private readonly IAudioPlayService   _play;
    private readonly Codec2Service        _codec2;
    private readonly ILogger<MainViewModel> _logger;

    private CancellationTokenSource? _pttCts;
    private System.Threading.Timer?  _limitTimer; // 30 秒上限計時
    private bool _playReady;                       // AudioTrack 是否已初始化

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isPttActive;
    [ObservableProperty] private string _statusMessage = "未連線";
    [ObservableProperty] private string _modeLabel     = "";

    // ── 發送對象（與文字頁共用同一份；選一次兩頁一致）──────────
    public string TargetHex { get => _chat.TargetHex; set => _chat.TargetHex = value; }
    public string TargetDisplayName => _chat.TargetDisplayName;
    public bool   IsBroadcastTarget => _chat.IsBroadcastTarget;
    public ObservableCollection<Contact> Contacts => _chat.Contacts;
    public ObservableCollection<DeviceGroup> Groups => _chat.Groups;

    public MainViewModel(
        ICommService comm,
        IMessagingService messaging,
        ChatViewModel chat,
        IAudioRecordService record,
        IAudioPlayService play,
        Codec2Service codec2,
        ILogger<MainViewModel> logger)
    {
        _comm      = comm;
        _messaging = messaging;
        _chat      = chat;
        _record    = record;
        _play      = play;
        _codec2    = codec2;
        _logger    = logger;

        try { _codec2.Init(); } catch (Exception ex) { _logger.LogError(ex, "Codec2 初始化失敗（native lib 未載入）"); }

        _messaging.VoiceReceived  += OnVoiceReceived;
        _comm.OnConnectionChanged += OnConnectionChanged;

        // 連線是共用的單例：本 VM 是 transient（每次進 PTT 頁都新建），會錯過先前在文字頁
        // 連上的事件 → 直接讀目前狀態，避免「文字頁已連、語音頁卻要再連一次」。
        IsConnected = _comm.IsConnected;
        if (IsConnected)
        {
            ModeLabel     = _comm.Mode == CommMode.WiFi ? "WiFi 模式" : "USB 模式";
            StatusMessage = $"已連線（{ModeLabel}）";
        }
    }

    // ── 連線 ───────────────────────────────────────────────
    [RelayCommand]
    private async Task ConnectAsync()
    {
        StatusMessage = "連線中...";
        ModeLabel     = _comm.Mode == CommMode.WiFi ? "WiFi 模式" : "USB 模式";
        try { await _comm.ConnectAsync(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "連線失敗");
            StatusMessage = $"連線失敗：{ex.Message}";
        }
    }

    // ── PTT 按下 ───────────────────────────────────────────
    [RelayCommand]
    private async Task PttPressAsync()
    {
        if (!IsConnected || IsPttActive) return;
        if (!_codec2.IsAvailable) { StatusMessage = "語音不可用（Codec2 未載入）"; return; }

        IsPttActive   = true;
        var dstId     = ParseTarget();   // 廣播或指定對象（與文字頁同一選擇）
        StatusMessage = "傳送中...";
        _pttCts = new CancellationTokenSource();

        // 先讓全網切到語音模式（SF7/BW500），稍候再開始串流，給各節點切換時間
        try { await _messaging.SendVoiceModeAsync(true, _pttCts.Token); } catch (Exception ex) { _logger.LogError(ex, "送 voice_start 失敗"); }
        try { await Task.Delay(250, _pttCts.Token); } catch (OperationCanceledException) { return; }

        // 30 秒上限：到點自動放開
        _limitTimer = new System.Threading.Timer(
            _ => { StatusMessage = $"已達 {MaxPttSeconds} 秒上限"; _ = PttReleaseAsync(); },
            null, MaxPttSeconds * 1000, Timeout.Infinite);

        var packet = new List<byte>(Codec2Service.BytesPerFrame * Codec2Service.FramesPerPacket);
        try
        {
            await _record.StartAsync(async pcmFrame =>
            {
                byte[] encoded;
                try { encoded = _codec2.Encode(pcmFrame); }
                catch (Exception ex) { _logger.LogError(ex, "Codec2 encode 失敗"); return; }
                packet.AddRange(encoded);

                // 累積 10 幀（200ms）送一包廣播
                if (packet.Count >= Codec2Service.BytesPerFrame * Codec2Service.FramesPerPacket)
                {
                    var frames = packet.ToArray();
                    packet.Clear();
                    try { await _messaging.SendVoiceAsync(frames, dstId, _pttCts.Token); }
                    catch (Exception ex) { _logger.LogError(ex, "送語音封包失敗"); }
                }
            }, _pttCts.Token);
        }
        catch (Exception ex) { _logger.LogError(ex, "啟動錄音失敗"); await PttReleaseAsync(); }
    }

    // ── PTT 放開 ───────────────────────────────────────────
    [RelayCommand]
    private async Task PttReleaseAsync()
    {
        if (!IsPttActive) return;
        IsPttActive = false;
        _limitTimer?.Dispose(); _limitTimer = null;
        _pttCts?.Cancel();

        try { await _record.StopAsync(); } catch (Exception ex) { _logger.LogError(ex, "停止錄音失敗"); }
        // 通知全網切回長距模式
        try { await _messaging.SendVoiceModeAsync(false); } catch (Exception ex) { _logger.LogError(ex, "送 voice_end 失敗"); }

        if (StatusMessage.StartsWith("傳送中")) StatusMessage = IsConnected ? "已連線" : "未連線";
    }

    // ── 收到語音幀 → 解碼 → 播放（半雙工：自己說話時不播）──────
    private async void OnVoiceReceived(byte[] codec2Frames)
    {
        if (IsPttActive) return; // 自己正在講 → 不播（也避免回授）
        try
        {
            if (!_playReady) { await _play.InitAsync(); _playReady = true; }
            for (int i = 0; i + Codec2Service.BytesPerFrame <= codec2Frames.Length; i += Codec2Service.BytesPerFrame)
            {
                var frame = new byte[Codec2Service.BytesPerFrame];
                Array.Copy(codec2Frames, i, frame, 0, Codec2Service.BytesPerFrame);
                var pcm = _codec2.Decode(frame);
                await _play.PlayPcmAsync(pcm);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "語音解碼/播放失敗"); }
    }

    private void OnConnectionChanged(bool connected)
    {
        IsConnected   = connected;
        StatusMessage = connected ? $"已連線（{ModeLabel}）" : "已斷線";
    }

    /// <summary>把目前的對象（TargetHex）解析為 dstId；無效則回廣播。</summary>
    private ushort ParseTarget()
    {
        var t = TargetHex?.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(t)
            && ushort.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            && v != DstId.Reserved)
            return v;
        return DstId.Broadcast;
    }
}
