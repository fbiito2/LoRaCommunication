using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoRaPTT.Services;
using Microsoft.Extensions.Logging;

namespace LoRaPTT.ViewModels;

/// <summary>
/// 設定頁 ViewModel（F-051）。
/// 讀取目前設定，修改後透過 USB/WiFi 發送 set_config JSON 給 C6L 寫入 NVS。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ICommService _comm;
    private readonly IMessagingService _messaging;
    private readonly RosterStore _store;
    private readonly ILogger<SettingsViewModel> _logger;

    // ── UI 綁定屬性 ────────────────────────────────────────
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    // ── C6L 裝置資訊（握手取得）──────────────────────────────
    [ObservableProperty] private string _deviceIdHex = "----";
    [ObservableProperty] private string _firmwareVersion = "未知";

    // ── 可編輯設定 ──────────────────────────────────────────
    [ObservableProperty] private string _nickname = "LoRaPTT";
    [ObservableProperty] private string _wifiSsid = "LoRaPTT";
    [ObservableProperty] private string _wifiPassword = "loraptt2026";
    [ObservableProperty] private string _loraFreqMhz = "920.0";

    /// <summary>供 Blazor 頁面在資料變動時呼叫 StateHasChanged</summary>
    public event Action? StateChanged;

    public SettingsViewModel(ICommService comm, IMessagingService messaging,
        RosterStore store, ILogger<SettingsViewModel> logger)
    {
        _comm = comm;
        _messaging = messaging;
        _store = store;
        _logger = logger;

        _comm.OnConnectionChanged += OnConnectionChanged;
        _messaging.HandshakeCompleted += OnHandshakeCompleted;

        // 載入已存的暱稱
        Nickname = _store.LoadNickname("LoRaPTT");

        // 若已連線，同步初始狀態
        IsConnected = _comm.IsConnected;
        SyncDeviceInfo();
    }

    // ── 連線 ────────────────────────────────────────────────
    [RelayCommand]
    private async Task ConnectAsync()
    {
        StatusMessage = "連線中...";
        Notify();
        try { await _comm.ConnectAsync(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "連線失敗");
            StatusMessage = $"連線失敗：{ex.Message}";
            Notify();
        }
    }

    // ── 儲存暱稱（本機持久化，不送 C6L）─────────────────────
    [RelayCommand]
    private void SaveNickname()
    {
        var name = Nickname?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            StatusMessage = "暱稱不得為空";
            Notify();
            return;
        }
        _messaging.LocalNickname = name;
        _store.SaveNickname(name);
        StatusMessage = "暱稱已儲存";
        Notify();
    }

    // ── 推送設定到 C6L（F-051 set_config）───────────────────
    [RelayCommand]
    private async Task ApplyToDeviceAsync()
    {
        if (!IsConnected)
        {
            StatusMessage = "請先連線 C6L";
            Notify();
            return;
        }

        if (!float.TryParse(LoraFreqMhz, out var freq) || freq < 900 || freq > 930)
        {
            StatusMessage = "頻率格式錯誤（920.0~925.0 MHz）";
            Notify();
            return;
        }

        IsBusy = true;
        StatusMessage = "正在推送設定到 C6L...";
        Notify();

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                cmd = "set_config",
                device_name = Nickname?.Trim() ?? "LoRaPTT",
                wifi_ssid = WifiSsid?.Trim() ?? "LoRaPTT",
                wifi_pass = WifiPassword ?? "loraptt2026",
                lora_freq = freq,
            });
            await _comm.SendAsync(Services.Protocol.LinkFrame.WrapCtrl(json));
            StatusMessage = "設定已送出，C6L 下次開機生效";
            _logger.LogInformation("已推送 set_config：{Json}", json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "推送設定失敗");
            StatusMessage = $"推送失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    // ── 事件處理 ────────────────────────────────────────────
    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;
        StatusMessage = connected ? "已連線，握手中..." : "已斷線";
        SyncDeviceInfo();
        Notify();
    }

    private void OnHandshakeCompleted()
    {
        SyncDeviceInfo();
        StatusMessage = $"握手完成（本機 0x{DeviceIdHex}）";
        Notify();
    }

    private void SyncDeviceInfo()
    {
        var id = _messaging.LocalDeviceId;
        DeviceIdHex = id.HasValue ? id.Value.ToString("X4") : "----";
        FirmwareVersion = _messaging.FirmwareVersion ?? "未知";
    }

    private void Notify() => StateChanged?.Invoke();
}
