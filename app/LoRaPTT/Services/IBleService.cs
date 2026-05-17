namespace LoRaPTT.Services;

/// <summary>BLE 通訊抽象介面</summary>
public interface IBleService
{
    bool IsConnected { get; }

    event Action<byte[]> OnDataReceived; // LoRa 資料到達（C6L Notify）
    event Action<bool> OnConnectionChanged;

    Task<bool> ScanAndConnectAsync(CancellationToken ct = default);
    Task SendAsync(byte[] data, CancellationToken ct = default);
    Task DisconnectAsync();
}
