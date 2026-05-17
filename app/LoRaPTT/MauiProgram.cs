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

		// ── 服務注入 ──────────────────────────────────────────
		builder.Services.AddSingleton<IBleService, BleService>();
		builder.Services.AddSingleton<Codec2Service>();

#if ANDROID
		builder.Services.AddSingleton<IAudioRecordService, Platforms.Android.AudioRecordImpl>();
		builder.Services.AddSingleton<IAudioPlayService,   Platforms.Android.AudioPlayImpl>();
#elif IOS
		builder.Services.AddSingleton<IAudioRecordService, Platforms.iOS.AudioRecordImpl>();
		builder.Services.AddSingleton<IAudioPlayService,   Platforms.iOS.AudioPlayImpl>();
#endif

		builder.Services.AddTransient<MainViewModel>();

		return builder.Build();
	}
}
