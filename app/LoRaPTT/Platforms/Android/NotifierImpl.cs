using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using LoRaPTT.Services;
using Application = Android.App.Application;

namespace LoRaPTT.Platforms.Android;

/// <summary>
/// Android 來訊提示：高重要度通知頻道（heads-up + 聲音 + 震動）。
/// 只在 App 不在前景時跳（前景時使用者已能看到訊息）。
/// </summary>
public sealed class NotifierImpl : INotifier
{
    private const string MsgChannel = "loraptt_msg";
    private const string SosChannel = "loraptt_sos";
    private static readonly long[] SosPattern = { 0, 500, 200, 500, 200, 500 };

    private int  _id = 2000;
    private bool _channelsReady;

    public void NotifyMessage(string from, string text, bool sos)
    {
        // 前景（在 App 裡且螢幕亮）就不跳通知，避免一邊看一邊狂響
        if (MainActivity.IsForeground) return;

        var ctx = Application.Context;
        EnsureChannels(ctx);

        var launch = ctx.PackageManager?.GetLaunchIntentForPackage(ctx.PackageName!);
        var pi = PendingIntent.GetActivity(
            ctx, 0, launch,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(ctx, sos ? SosChannel : MsgChannel)
            .SetContentTitle(sos ? "🆘 SOS 緊急求救" : $"來自 {from}")
            .SetContentText(text)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(text))
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)
            .SetAutoCancel(true)
            .SetContentIntent(pi)
            .SetPriority((int)NotificationPriority.High)
            .SetDefaults((int)NotificationDefaults.All); // pre-O：聲音+震動+燈
        if (sos)
            builder.SetVibrate(SosPattern);

        try { NotificationManagerCompat.From(ctx).Notify(_id++, builder.Build()); }
        catch (global::System.Exception ex)
        {
            global::System.Console.WriteLine("LPTT: 來訊通知失敗: " + ex.Message);
        }
    }

    private void EnsureChannels(Context ctx)
    {
        if (_channelsReady) return;
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) { _channelsReady = true; return; }

        var mgr = (NotificationManager?)ctx.GetSystemService(Context.NotificationService);
        if (mgr is null) return;

        var msg = new NotificationChannel(MsgChannel, "來訊提示", NotificationImportance.High)
        {
            Description = "收到文字訊息時提示（聲音 + 震動）",
        };
        msg.EnableVibration(true);

        var sosCh = new NotificationChannel(SosChannel, "SOS 緊急求救", NotificationImportance.High)
        {
            Description = "收到 SOS 緊急求救時強提示",
        };
        sosCh.EnableVibration(true);
        sosCh.SetVibrationPattern(SosPattern);

        mgr.CreateNotificationChannel(msg);
        mgr.CreateNotificationChannel(sosCh);
        _channelsReady = true;
    }
}
