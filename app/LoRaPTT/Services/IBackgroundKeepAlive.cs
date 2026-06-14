namespace LoRaPTT.Services;

/// <summary>
/// 背景保活：螢幕關閉 / App 退到背景時，仍維持與 C6L 的連線並持續收訊。
/// Android 實作以「前景服務（Foreground Service）+ WifiLock + WakeLock」達成；
/// 其他平台為 no-op。由連線狀態驅動（連上 → Start、斷線 → Stop）。
/// </summary>
public interface IBackgroundKeepAlive
{
    /// <summary>開始保活（連線成功後呼叫）。</summary>
    void Start();

    /// <summary>停止保活（斷線時呼叫）。</summary>
    void Stop();
}

/// <summary>非 Android 平台的空實作。</summary>
public sealed class NoOpBackgroundKeepAlive : IBackgroundKeepAlive
{
    public void Start() { }
    public void Stop() { }
}
