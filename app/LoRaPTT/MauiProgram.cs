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

        // ── 通訊服務（WiFi 優先，USB Serial Phase 2 完成後啟用）──
        // TODO: Phase 2 硬體到手後改為偵測 USB OTG 自動選擇
        builder.Services.AddSingleton<ICommService, WiFiCommService>();
        // builder.Services.AddSingleton<ICommService, UsbSerialCommService>();

        // ── 文字訊息服務（封包組裝/解析、ACK、PING 探測）──────
        builder.Services.AddSingleton<IMessagingService, MessagingService>();

        // ── 通訊錄/群組持久化（F-003 / F-020~022）────────────
        builder.Services.AddSingleton<RosterStore>();

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

        return builder.Build();
    }
}
