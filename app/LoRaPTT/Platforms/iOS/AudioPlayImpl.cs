using AVFoundation;
using LoRaPTT.Services;

namespace LoRaPTT.Platforms.iOS;

// iOS 播放存根：AVAudioEngine（8000Hz, Mono, PCM Int16）
// TODO: Phase 4 硬體到手後完整實作
public class AudioPlayImpl : IAudioPlayService
{
    public Task InitAsync()
    {
        // TODO: 設定 AVAudioSession, AVAudioEngine playerNode
        throw new NotImplementedException("iOS 播放尚未實作，Phase 4 完成");
    }

    public Task PlayPcmAsync(short[] samples)
    {
        // TODO: 送 samples 到 AVAudioPlayerNode buffer
        throw new NotImplementedException();
    }

    public Task StopAsync() => Task.CompletedTask;
}
