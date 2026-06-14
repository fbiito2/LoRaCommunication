using Android.Content;
using Android.OS;
using LoRaPTT.Services;
using Application = Android.App.Application;

namespace LoRaPTT.Platforms.Android;

/// <summary>啟動 / 停止 <see cref="PttForegroundService"/> 的 Android 實作。</summary>
public sealed class BackgroundKeepAliveImpl : IBackgroundKeepAlive
{
    public void Start()
    {
        var ctx = Application.Context;
        var intent = new Intent(ctx, typeof(PttForegroundService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            ctx.StartForegroundService(intent); // O+ 必須用此啟動前景服務
        else
            ctx.StartService(intent);
    }

    public void Stop()
    {
        var ctx = Application.Context;
        ctx.StopService(new Intent(ctx, typeof(PttForegroundService)));
    }
}
