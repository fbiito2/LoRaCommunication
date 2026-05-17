using System.Runtime.InteropServices;

namespace LoRaPTT.Services;

/// <summary>
/// Codec2 P/Invoke 封裝。
/// native library 必須放在 libs/codec2/{platform}/ 並設定為 MauiAsset。
/// 硬體到手、測試完 BLE+LoRa 文字版後再整合此模組。
/// </summary>
public class Codec2Service : IDisposable
{
    // Codec2 2400bps 模式
    private const int CODEC2_MODE_2400 = 0;

    // 每幀樣本數（20ms @ 8000Hz = 160 samples）
    public const int SamplesPerFrame = 160;
    // 每幀編碼後 bytes（2400bps → 6 bytes）
    public const int BytesPerFrame = 6;
    // 累積幀數（200ms = 10 幀）
    public const int FramesPerPacket = 10;

#if ANDROID || IOS
    private const string LIB = "codec2";

    [DllImport(LIB)] private static extern IntPtr codec2_create(int mode);
    [DllImport(LIB)] private static extern void   codec2_destroy(IntPtr c2);
    [DllImport(LIB)] private static extern void   codec2_encode(IntPtr c2, byte[] bits, short[] pcm);
    [DllImport(LIB)] private static extern void   codec2_decode(IntPtr c2, short[] pcm, byte[] bits);
#endif

    private IntPtr _encoder = IntPtr.Zero;
    private IntPtr _decoder = IntPtr.Zero;

    public bool IsAvailable { get; private set; }

    public void Init()
    {
#if ANDROID || IOS
        try
        {
            _encoder = codec2_create(CODEC2_MODE_2400);
            _decoder = codec2_create(CODEC2_MODE_2400);
            IsAvailable = (_encoder != IntPtr.Zero && _decoder != IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            IsAvailable = false;
        }
#else
        IsAvailable = false; // Windows 模擬器不支援
#endif
    }

    /// <summary>PCM 樣本（160個 short）→ 6 bytes Codec2</summary>
    public byte[] Encode(short[] pcmFrame)
    {
#if ANDROID || IOS
        var bits = new byte[BytesPerFrame];
        codec2_encode(_encoder, bits, pcmFrame);
        return bits;
#else
        return new byte[BytesPerFrame];
#endif
    }

    /// <summary>6 bytes Codec2 → PCM 樣本（160個 short）</summary>
    public short[] Decode(byte[] bits)
    {
#if ANDROID || IOS
        var pcm = new short[SamplesPerFrame];
        codec2_decode(_decoder, pcm, bits);
        return pcm;
#else
        return new short[SamplesPerFrame];
#endif
    }

    public void Dispose()
    {
#if ANDROID || IOS
        if (_encoder != IntPtr.Zero) { codec2_destroy(_encoder); _encoder = IntPtr.Zero; }
        if (_decoder != IntPtr.Zero) { codec2_destroy(_decoder); _decoder = IntPtr.Zero; }
#endif
    }
}
