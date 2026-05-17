using Android.Media;
using LoRaPTT.Services;

namespace LoRaPTT.Platforms.Android;

// Android 錄音實作：AudioRecord（PCM 16-bit, 8000Hz, Mono）
public class AudioRecordImpl : IAudioRecordService
{
    private const int SampleRate  = 8000;
    private const int ChannelMask = (int)ChannelIn.Mono;
    private const int Encoding    = (int)global::Android.Media.Encoding.Pcm16bit;

    private AudioRecord? _recorder;
    private bool _isRecording;

    public bool IsRecording => _isRecording;

    public async Task StartAsync(Action<short[]> onPcmFrame, CancellationToken ct = default)
    {
        int bufSize = AudioRecord.GetMinBufferSize(SampleRate,
            (ChannelIn)ChannelMask, global::Android.Media.Encoding.Pcm16bit);

        _recorder = new AudioRecord(
            AudioSource.Mic,
            SampleRate,
            (ChannelIn)ChannelMask,
            global::Android.Media.Encoding.Pcm16bit,
            bufSize);

        _recorder.StartRecording();
        _isRecording = true;

        // 每次讀取一幀（160 samples = 20ms）
        var frameBuf = new short[160];
        await Task.Run(() =>
        {
            while (!ct.IsCancellationRequested && _isRecording)
            {
                int read = _recorder.Read(frameBuf, 0, frameBuf.Length);
                if (read > 0)
                    onPcmFrame(frameBuf);
            }
        }, ct);
    }

    public Task StopAsync()
    {
        _isRecording = false;
        _recorder?.Stop();
        _recorder?.Release();
        _recorder = null;
        return Task.CompletedTask;
    }
}
