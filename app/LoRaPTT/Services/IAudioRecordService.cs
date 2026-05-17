namespace LoRaPTT.Services;

/// <summary>平台錄音抽象介面（Android / iOS 各自實作）</summary>
public interface IAudioRecordService
{
    bool IsRecording { get; }

    /// <summary>開始錄音，每累積 frameCount 幀（20ms/幀）觸發一次回呼</summary>
    Task StartAsync(Action<short[]> onPcmFrame, CancellationToken ct = default);
    Task StopAsync();
}
