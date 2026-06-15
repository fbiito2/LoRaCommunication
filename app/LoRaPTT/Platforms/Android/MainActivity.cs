using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Views;
using AndroidX.Core.View;

namespace LoRaPTT;

// WindowSoftInputMode = AdjustResize：鍵盤彈出時縮放版面，輸入框會自動捲到鍵盤上方可見
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>App 是否在前景（介於 OnResume~OnPause）。背景來訊才跳通知用。</summary>
    public static bool IsForeground { get; private set; }

    protected override void OnResume() { base.OnResume(); IsForeground = true; }
    protected override void OnPause()  { base.OnPause();  IsForeground = false; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // 邊到邊 + 手動處理 IME（鍵盤）inset：鍵盤彈出時把內容底部往上推，輸入框不被蓋住。
        // （Android 15 下 windowSoftInputMode=adjustResize / visualViewport 都失效，這才是正解）
        Window?.SetDecorFitsSystemWindows(false);
        var content = FindViewById(Android.Resource.Id.Content);
        if (content != null)
            ViewCompat.SetOnApplyWindowInsetsListener(content, new ImeInsetsListener());
        BindToWifiNetwork();
        RequestBackgroundExemptions();
    }

    // 背景保活的前置授權：①電池最佳化豁免（Samsung Doze 會殺背景行程）②通知權限
    // （Android 13+ 前景服務通知才看得到；就算不准,服務仍會跑、收訊不受影響）。
    private void RequestBackgroundExemptions()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                var pm = (PowerManager?)GetSystemService(PowerService);
                if (pm != null && PackageName != null && !pm.IsIgnoringBatteryOptimizations(PackageName))
                {
                    var i = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
                    i.SetData(global::Android.Net.Uri.Parse("package:" + PackageName));
                    i.AddFlags(ActivityFlags.NewTask);
                    StartActivity(i);
                }
            }

            // 執行期權限：通知（Android 13+）+ 麥克風（PTT 語音 F-052）
            var need = new List<string>();
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted)
                need.Add("android.permission.POST_NOTIFICATIONS");
            if (CheckSelfPermission("android.permission.RECORD_AUDIO") != Permission.Granted)
                need.Add("android.permission.RECORD_AUDIO");
            if (need.Count > 0)
                RequestPermissions(need.ToArray(), 100);
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine("LPTT: RequestBackgroundExemptions 失敗: " + ex.Message);
        }
    }

    // 把鍵盤高度套成內容底部 padding；鍵盤收起時為 0（同時保留導覽列高度避免內容被遮）
    private sealed class ImeInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(Android.Views.View v, WindowInsetsCompat insets)
        {
            var ime  = insets.GetInsets(WindowInsetsCompat.Type.Ime());
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            // 上=狀態列、左右=系統列、底=max(鍵盤, 導覽列)；確保所有內容都在安全區內、不被裁切
            v.SetPadding(bars.Left, bars.Top, bars.Right, System.Math.Max(ime.Bottom, bars.Bottom));
            return WindowInsetsCompat.Consumed; // 已處理，避免子視圖重複套用
        }
    }

    // 將整個 process 的 socket 綁定到 WiFi 網路。
    // 否則 Android 對「沒有對外網際網路的 WiFi」（C6L 的 AP）會把 UDP/TCP
    // 流量送往行動網路，導致連不到 192.168.4.1。
    private void BindToWifiNetwork()
    {
        try
        {
            var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
            if (cm is null) return;
            var req = new NetworkRequest.Builder()
                .AddTransportType(TransportType.Wifi)!
                .RemoveCapability(NetCapability.Internet)! // C6L AP 無對外網路
                .Build();
            cm.RequestNetwork(req, new WifiBindCallback(cm));
            System.Console.WriteLine("LPTT: 已要求綁定 WiFi 網路");
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine("LPTT: BindToWifiNetwork 失敗: " + ex.Message);
        }
    }

    private sealed class WifiBindCallback : ConnectivityManager.NetworkCallback
    {
        private readonly ConnectivityManager _cm;
        public WifiBindCallback(ConnectivityManager cm) => _cm = cm;

        public override void OnAvailable(Network network)
        {
            bool ok = _cm.BindProcessToNetwork(network);
            System.Console.WriteLine($"LPTT: WiFi 網路就緒，綁定 process = {ok}");
        }
    }
}
