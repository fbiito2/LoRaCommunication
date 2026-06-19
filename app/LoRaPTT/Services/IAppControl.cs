namespace LoRaPTT.Services;

/// <summary>
/// App 程序控制。主要提供「完全結束」——因為背景保活(前景服務)會讓 App 滑掉也不關，
/// 需要一個能真正停服務 + 殺行程的出口，重開時才是全新狀態（解開卡死用）。
/// </summary>
public interface IAppControl
{
    /// <summary>完全結束 App：停背景前景服務、結束 Activity、殺行程。</summary>
    void ExitApp();
}

/// <summary>非 Android 平台的空實作。</summary>
public sealed class NoOpAppControl : IAppControl
{
    public void ExitApp() { }
}
