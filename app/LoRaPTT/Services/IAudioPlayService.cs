namespace LoRaPTT.Services;

/// <summary>平台播放抽象介面（Android / iOS 各自實作）</summary>
public interface IAudioPlayService
{
    Task InitAsync();
    /// <summary>將解碼後的 PCM 樣本送入播放佇列</summary>
    Task PlayPcmAsync(short[] samples);
    Task StopAsync();
}
