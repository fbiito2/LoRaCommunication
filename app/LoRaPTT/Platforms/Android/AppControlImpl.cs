using Android.Content;
using LoRaPTT.Services;
using Application = Android.App.Application;

namespace LoRaPTT.Platforms.Android;

/// <summary>完全結束 App 的 Android 實作：停前景服務 → 結束 Activity(移除工作) → 殺行程。</summary>
public sealed class AppControlImpl : IAppControl
{
    public void ExitApp()
    {
        var ctx = Application.Context;
        try { ctx.StopService(new Intent(ctx, typeof(PttForegroundService))); }
        catch (global::System.Exception ex) { global::System.Console.WriteLine("LPTT: 停前景服務失敗: " + ex.Message); }

        // 結束 Activity 並從最近工作移除，再殺掉行程 → 下次開啟為全新狀態
        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is global::Android.App.Activity a)
            a.FinishAndRemoveTask();
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }
}
