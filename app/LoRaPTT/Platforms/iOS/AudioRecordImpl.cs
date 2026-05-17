using AVFoundation;
using LoRaPTT.Services;

namespace LoRaPTT.Platforms.iOS;

// iOS 錄音存根：AVAudioEngine（8000Hz, Mono, PCM Int16）
// TODO: Phase 4 硬體到手後完整實作
public class AudioRecordImpl : IAudioRecordService
{
    private AVAudioEngine? _engine;
    private bool _isRecording;

    public bool IsRecording => _isRecording;

    public Task StartAsync(Action<short[]> onPcmFrame, CancellationToken ct = default)
    {
        // TODO: 設定 AVAudioSession category = PlayAndRecord
        // TODO: 安裝 tap on inputNode，轉換格式為 8000Hz Int16，呼叫 onPcmFrame
        throw new NotImplementedException("iOS 錄音尚未實作，Phase 4 完成");
    }

    public Task StopAsync()
    {
        _engine?.Stop();
        _isRecording = false;
        return Task.CompletedTask;
    }
}
