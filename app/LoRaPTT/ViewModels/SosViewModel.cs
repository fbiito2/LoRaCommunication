using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoRaPTT.Services;
using Microsoft.Extensions.Logging;

namespace LoRaPTT.ViewModels;

/// <summary>
/// SOS 緊急求救 ViewModel（F-070~073）。
/// 提供長按 3 秒觸發 SOS 發送，以及接收 SOS 的警報顯示。
/// </summary>
public partial class SosViewModel : ObservableObject
{
    /// <summary>觸發 SOS 需長按秒數</summary>
    public const int RequiredHoldSeconds = 3;

    private readonly ICommService _comm;
    private readonly IMessagingService _messaging;
    private readonly ILogger<SosViewModel> _logger;

    private CancellationTokenSource? _holdCts;
    private DateTime _holdStart;

    // ── UI 綁定屬性 ────────────────────────────────────────
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _isHolding;       // 正在長按中
    [ObservableProperty] private int _holdProgress;     // 長按進度 0~100
    [ObservableProperty] private string _gpsStatus = "未取得";
    [ObservableProperty] private double _gpsLat;
    [ObservableProperty] private double _gpsLon;
    [ObservableProperty] private string _extraText = "";

    /// <summary>收到的 SOS 警報記錄</summary>
    public ObservableCollection<SosAlert> Alerts { get; } = new();

    /// <summary>是否有未讀警報</summary>
    [ObservableProperty] private bool _hasUnreadAlert;

    /// <summary>供 Blazor 頁面在資料變動時呼叫 StateHasChanged</summary>
    public event Action? StateChanged;

    public SosViewModel(ICommService comm, IMessagingService messaging,
        ILogger<SosViewModel> logger)
    {
        _comm = comm;
        _messaging = messaging;
        _logger = logger;

        _comm.OnConnectionChanged += OnConnectionChanged;
        _messaging.SosReceived += OnSosReceived;

        IsConnected = _comm.IsConnected;
    }

    // ── GPS 位置取得 ────────────────────────────────────────
    [RelayCommand]
    private async Task RefreshGpsAsync()
    {
        GpsStatus = "定位中...";
        Notify();
        try
        {
            var loc = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10)));
            if (loc is not null)
            {
                GpsLat = loc.Latitude;
                GpsLon = loc.Longitude;
                GpsStatus = $"{loc.Latitude:F6}, {loc.Longitude:F6}";
            }
            else
            {
                GpsStatus = "無法取得位置";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPS 定位失敗");
            GpsStatus = $"定位失敗：{ex.Message}";
        }
        Notify();
    }

    // ── 長按開始 ────────────────────────────────────────────
    [RelayCommand]
    private async Task HoldStartAsync()
    {
        if (!IsConnected || IsSending) return;

        IsHolding = true;
        HoldProgress = 0;
        _holdStart = DateTime.UtcNow;
        _holdCts = new CancellationTokenSource();
        Notify();

        try
        {
            // 每 100ms 更新進度，滿 3 秒自動觸發
            while (!_holdCts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, _holdCts.Token);
                var elapsed = (DateTime.UtcNow - _holdStart).TotalSeconds;
                HoldProgress = Math.Min(100, (int)(elapsed / RequiredHoldSeconds * 100));
                Notify();

                if (elapsed >= RequiredHoldSeconds)
                {
                    // 長按達標 → 觸發 SOS
                    IsHolding = false;
                    Notify();
                    await SendSosInternalAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* 放開手指 */ }
        finally
        {
            IsHolding = false;
            HoldProgress = 0;
            Notify();
        }
    }

    // ── 長按放開（未滿 3 秒取消）────────────────────────────
    [RelayCommand]
    private void HoldEnd()
    {
        _holdCts?.Cancel();
        IsHolding = false;
        HoldProgress = 0;
        if (!IsSending)
            StatusMessage = "未達 3 秒，已取消";
        Notify();
    }

    // ── 實際發送 SOS ────────────────────────────────────────
    private async Task SendSosInternalAsync()
    {
        IsSending = true;
        StatusMessage = "!!! 正在發送 SOS 求救（3 次）...";
        Notify();

        try
        {
            await _messaging.SendSosAsync(GpsLat, GpsLon,
                string.IsNullOrWhiteSpace(ExtraText) ? null : ExtraText.Trim());
            StatusMessage = "SOS 已發送完畢（3 次廣播，HOP=15）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SOS 發送失敗");
            StatusMessage = $"SOS 發送失敗：{ex.Message}";
        }
        finally
        {
            IsSending = false;
            Notify();
        }
    }

    // ── 接收 SOS 警報（F-073）───────────────────────────────
    private void OnSosReceived(ushort srcId, byte[] payload)
    {
        var alert = new SosAlert
        {
            FromId = srcId,
            ReceivedAt = DateTimeOffset.Now,
        };

        // 解析 payload：[DeviceID 2B][Lat 8B][Lon 8B][text NB]
        if (payload.Length >= 18)
        {
            alert.Lat = BitConverter.ToDouble(payload, 2);
            alert.Lon = BitConverter.ToDouble(payload, 10);
            if (payload.Length > 18)
                alert.ExtraText = System.Text.Encoding.UTF8.GetString(payload, 18, payload.Length - 18);
        }

        RunOnUi(() =>
        {
            Alerts.Insert(0, alert); // 最新的在最上方
            HasUnreadAlert = true;
            StatusMessage = $"!!! 收到 SOS 求救！來自 0x{srcId:X4}";
        });
    }

    [RelayCommand]
    private void ClearAlerts()
    {
        Alerts.Clear();
        HasUnreadAlert = false;
        StatusMessage = "";
        Notify();
    }

    // ── 事件處理 ────────────────────────────────────────────
    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;
        StatusMessage = connected ? "已連線" : "已斷線";
        Notify();
    }

    private void RunOnUi(Action action)
    {
        if (Microsoft.Maui.ApplicationModel.MainThread.IsMainThread)
        {
            action();
            StateChanged?.Invoke();
        }
        else
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                action();
                StateChanged?.Invoke();
            });
        }
    }

    private void Notify() => StateChanged?.Invoke();
}

/// <summary>收到的 SOS 警報記錄</summary>
public sealed class SosAlert
{
    public ushort FromId { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string? ExtraText { get; set; }
    public string FromIdHex => FromId.ToString("X4");
    public bool HasGps => Lat != 0 || Lon != 0;
}
