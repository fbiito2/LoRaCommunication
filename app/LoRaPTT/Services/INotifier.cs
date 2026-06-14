namespace LoRaPTT.Services;

/// <summary>
/// 來訊提示：背景（螢幕關／App 不在前景）收到訊息時跳通知 + 震動 + 聲音。
/// App 在前景時略過（使用者已能直接看到訊息）。Android 以高重要度通知頻道實作，
/// 其他平台為 no-op。
/// </summary>
public interface INotifier
{
    /// <param name="from">發送者顯示字串（如 0xA01B）</param>
    /// <param name="text">訊息內容</param>
    /// <param name="sos">是否為 SOS 緊急求救（用更強的震動樣式）</param>
    void NotifyMessage(string from, string text, bool sos);
}

/// <summary>非 Android 平台的空實作。</summary>
public sealed class NoOpNotifier : INotifier
{
    public void NotifyMessage(string from, string text, bool sos) { }
}
