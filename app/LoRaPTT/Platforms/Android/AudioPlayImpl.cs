using Android.Media;
using LoRaPTT.Services;

namespace LoRaPTT.Platforms.Android;

// Android 播放實作：AudioTrack（PCM 16-bit, 8000Hz, Mono）
public class AudioPlayImpl : IAudioPlayService
{
    private const int SampleRate = 8000;

    private AudioTrack? _track;

    public Task InitAsync()
    {
        int bufSize = AudioTrack.GetMinBufferSize(
            SampleRate,
            ChannelOut.Mono,
            global::Android.Media.Encoding.Pcm16bit);

        _track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)
                .SetContentType(AudioContentType.Speech)
                .Build())
            .SetAudioFormat(new AudioFormat.Builder()
                .SetEncoding(global::Android.Media.Encoding.Pcm16bit)
                .SetSampleRate(SampleRate)
                .SetChannelMask(ChannelOut.Mono)
                .Build())
            .SetBufferSizeInBytes(bufSize)
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();

        _track.Play();
        return Task.CompletedTask;
    }

    public Task PlayPcmAsync(short[] samples)
    {
        _track?.Write(samples, 0, samples.Length);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _track?.Stop();
        _track?.Release();
        _track = null;
        return Task.CompletedTask;
    }
}
