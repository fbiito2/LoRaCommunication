using Microsoft.Extensions.Logging;
using LoRaPTT.Services;
using LoRaPTT.ViewModels;

namespace LoRaPTT;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // ── 通訊服務（F-054：CommRouter 自動選 USB / WiFi）──
        // 連線時先試 USB（偵測到 C6L 才成功），失敗落回 WiFi。上層只見單一 ICommService。
        builder.Services.AddSingleton<WiFiCommService>();
#if ANDROID
        builder.Services.AddSingleton<IUsbCommService, UsbSerialImpl>();
#endif
        builder.Services.AddSingleton<ICommService, CommRouter>();

        // ── 文字訊息服務（封包組裝/解析、ACK、PING 探測）──────
        builder.Services.AddSingleton<IMessagingService, MessagingService>();

        // ── 通訊錄/群組持久化（F-003 / F-020~022）────────────
        builder.Services.AddSingleton<RosterStore>();

        // ── 韌體 OTA 上傳（F-060~064）────────────────────────
        builder.Services.AddSingleton<OtaService>();

        // ── Codec2 編解碼 ────────────────────────────────────
        builder.Services.AddSingleton<Codec2Service>();

        // ── 平台音訊 ─────────────────────────────────────────
#if ANDROID
        builder.Services.AddSingleton<IAudioRecordService, Platforms.Android.AudioRecordImpl>();
        builder.Services.AddSingleton<IAudioPlayService,   Platforms.Android.AudioPlayImpl>();
#elif IOS
        builder.Services.AddSingleton<IAudioRecordService, Platforms.iOS.AudioRecordImpl>();
        builder.Services.AddSingleton<IAudioPlayService,   Platforms.iOS.AudioPlayImpl>();
#endif

        builder.Services.AddTransient<MainViewModel>();
        builder.Services.AddSingleton<ViewModels.ChatViewModel>();
        builder.Services.AddSingleton<ViewModels.SettingsViewModel>();
        builder.Services.AddSingleton<ViewModels.SosViewModel>();

        return builder.Build();
    }
}
