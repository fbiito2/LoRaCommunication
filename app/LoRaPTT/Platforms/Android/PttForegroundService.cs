using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using AndroidX.Core.App;

namespace LoRaPTT.Platforms.Android;

/// <summary>
/// 前景服務：讓 App 在螢幕關閉 / 退背景時不被 Doze 凍結或回收，並持有
/// WifiLock（FULL_HIGH_PERF，螢幕關不關 WiFi）+ partial WakeLock（CPU 不睡，
/// 接收迴圈能處理封包）。收訊迴圈本身仍跑在 App process（WiFiCommService），
/// 這個服務只負責「讓那個 process 活著、WiFi 不關」。
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class PttForegroundService : Service
{
    private const string ChannelId = "loraptt_bg";
    private const int    NotifId   = 1001;

    private WifiManager.WifiLock? _wifiLock;
    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartForeground(NotifId, BuildNotification());
        AcquireLocks();
        return StartCommandResult.Sticky; // 被系統殺掉時自動重啟
    }

    public override void OnDestroy()
    {
        ReleaseLocks();
        base.OnDestroy();
    }

    private void AcquireLocks()
    {
        try
        {
            var wifi = (WifiManager?)GetSystemService(WifiService);
            // 螢幕關閉時維持 WiFi 不被關（避免 UDP 斷）。新版 Android 此鎖多為輔助，
            // 真正讓 process 不被凍結/WiFi 不關的是「前景服務 + 未被 Doze」。
            _wifiLock = wifi?.CreateWifiLock("loraptt:wifi");
            if (_wifiLock is not null) { _wifiLock.SetReferenceCounted(false); _wifiLock.Acquire(); }

            var pm = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "loraptt:cpu");
            _wakeLock?.Acquire();
            global::System.Console.WriteLine("LPTT: 前景服務啟動,已持有 WifiLock + WakeLock");
        }
        catch (global::System.Exception ex)
        {
            global::System.Console.WriteLine("LPTT: AcquireLocks 失敗: " + ex.Message);
        }
    }

    private void ReleaseLocks()
    {
        try { if (_wifiLock?.IsHeld == true) _wifiLock.Release(); } catch { /* 已釋放 */ }
        try { if (_wakeLock?.IsHeld == true) _wakeLock.Release(); } catch { /* 已釋放 */ }
    }

    private Notification BuildNotification()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var mgr = (NotificationManager?)GetSystemService(NotificationService);
            var ch = new NotificationChannel(ChannelId, "LoRaPTT 背景連線", NotificationImportance.Low)
            {
                Description = "維持與 C6L 的連線,讓背景也能收到訊息",
            };
            mgr?.CreateNotificationChannel(ch);
        }

        // 點通知回到 App
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        var pi = PendingIntent.GetActivity(
            this, 0, launch,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("LoRaPTT 執行中")
            .SetContentText("背景維持連線,可持續收訊")
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)
            .SetOngoing(true)
            .SetContentIntent(pi)
            .SetPriority((int)NotificationPriority.Low)
            .Build();
    }
}
