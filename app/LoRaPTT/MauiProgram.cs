using Microsoft.Extensions.DependencyInjection;
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
        // 背景保活：前景服務 + WifiLock + WakeLock（螢幕關閉也持續收訊）
        builder.Services.AddSingleton<IBackgroundKeepAlive, Platforms.Android.BackgroundKeepAliveImpl>();
        // 來訊提示：高重要度通知頻道（聲音 + 震動）
        builder.Services.AddSingleton<INotifier, Platforms.Android.NotifierImpl>();
        // App 程序控制（完全結束）
        builder.Services.AddSingleton<IAppControl, Platforms.Android.AppControlImpl>();
#else
        builder.Services.AddSingleton<IBackgroundKeepAlive, NoOpBackgroundKeepAlive>();
        builder.Services.AddSingleton<INotifier, NoOpNotifier>();
        builder.Services.AddSingleton<IAppControl, NoOpAppControl>();
#endif
        builder.Services.AddSingleton<ICommService, CommRouter>();

        // ── 文字訊息服務（封包組裝/解析、ACK、PING 探測）──────
        builder.Services.AddSingleton<IMessagingService, MessagingService>();

        // ── 節點資料庫（NodeDB，F-036）：從收到的封包自動建檔 ────
        builder.Services.AddSingleton<NodeRegistry>();

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
        builder.Services.AddSingleton<ViewModels.NodesViewModel>();

        var app = builder.Build();
        // 強制建立 NodeRegistry，使其從啟動就訂閱封包事件、持續累積節點（F-036）；
        // 否則它只在進入節點頁時才被建立，會漏掉之前收到的封包。
        app.Services.GetRequiredService<NodeRegistry>();
        return app;
    }
}
