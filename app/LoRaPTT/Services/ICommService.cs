namespace LoRaPTT.Services;

/// <summary>通訊模式列舉</summary>
public enum CommMode
{
    /// <summary>攜帶模式 — USB Serial（CDC）</summary>
    UsbSerial,
    /// <summary>基站模式 — WiFi UDP</summary>
    WiFi
}

/// <summary>通訊服務介面 — USB Serial 與 WiFi 共用</summary>
public interface ICommService
{
    bool IsConnected { get; }
    CommMode Mode { get; }

    event Action<byte[]> OnDataReceived;
    event Action<bool>   OnConnectionChanged;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task SendAsync(byte[] data, CancellationToken ct = default);
}
