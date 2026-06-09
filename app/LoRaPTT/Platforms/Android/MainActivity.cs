using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Views;

namespace LoRaPTT;

// WindowSoftInputMode = AdjustResize：鍵盤彈出時縮放版面，輸入框會自動捲到鍵盤上方可見
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BindToWifiNetwork();
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
