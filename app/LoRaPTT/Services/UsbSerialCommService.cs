using Microsoft.Extensions.Logging;

namespace LoRaPTT.Services;

/// <summary>
/// 攜帶模式：USB Serial CDC 通訊（手機 USB-C 直連 C6L）
/// Android 使用 UsbSerialForAndroid 套件；iOS 受限，以 WiFi 為主
/// </summary>
public class UsbSerialCommService : ICommService
{
    private readonly ILogger<UsbSerialCommService> _logger;

    public bool     IsConnected { get; private set; }
    public CommMode Mode        => CommMode.UsbSerial;

    public event Action<byte[]>? OnDataReceived;
    public event Action<bool>?   OnConnectionChanged;

    public UsbSerialCommService(ILogger<UsbSerialCommService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 掃描 USB 裝置並連線到 C6L（Android USB OTG）
    /// 實際 USB Serial 操作委派給 Android 平台實作
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("USB Serial 嘗試連線...");

        // Platform-specific 實作由 Platforms/Android/UsbSerialImpl.cs 完成
        // 此處透過 MessagingCenter / 平台介面橋接
        // TODO: Phase 2 硬體到手後整合 UsbSerialForAndroid NuGet
        await Task.Delay(100, ct);

        _logger.LogWarning("USB Serial 尚未完整實作，硬體到手後完成");
        throw new NotImplementedException("USB Serial 需要 Android 平台實作，Phase 2 完成");
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("USB Serial 未連線");
        // TODO: 寫入 USB Serial port（含 2-byte 長度前綴，對應韌體幀格式）
        throw new NotImplementedException();
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        OnConnectionChanged?.Invoke(false);
        return Task.CompletedTask;
    }
}
