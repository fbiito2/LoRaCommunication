#if ANDROID
using System.IO;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace LoRaPTT.Services;

/// <summary>
/// F-054 USB Serial（CDC）傳輸層 — Android USB OTG，手機 USB-C 直連 C6L。
///
/// 幀格式與韌體 <c>usb_serial_service</c> 一致：傳輸層加 2-byte 大端長度前綴
/// （phone→C6L = [len][LinkFrame]；C6L→phone 同樣 [len][LinkFrame]）。
/// 收訊用重同步解析，容忍開機/握手期間混入的 debug log（韌體已在 USB 有 APP 時抑制）。
///
/// ⚠ 風險：C6L 原生 HWCDC 對「會開埠的 host」實測不穩（見 SPEC F-054）。本實作完成後
/// 仍需實機驗證 OTG 下是否穩定。
/// </summary>
public sealed class UsbSerialImpl : IUsbCommService
{
    private const string ACTION_USB_PERMISSION = "com.companyname.loraptt.USB_PERMISSION";
    private const int ESP32_VID = 0x303A; // Espressif（ESP32-C6 原生 USB）

    private readonly ILogger<UsbSerialImpl> _logger;

    private UsbManager? _manager;
    private UsbDeviceConnection? _conn;
    private UsbEndpoint? _epIn;
    private UsbEndpoint? _epOut;
    private CancellationTokenSource? _readCts;

    public bool     IsConnected { get; private set; }
    public CommMode Mode        => CommMode.UsbSerial;

    public event Action<byte[]>? OnDataReceived;
    public event Action<bool>?   OnConnectionChanged;

    public UsbSerialImpl(ILogger<UsbSerialImpl> logger) => _logger = logger;

    private static Context Ctx => AndroidApp.Context;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _manager = (UsbManager?)Ctx.GetSystemService(Context.UsbService)
                   ?? throw new InvalidOperationException("無法取得 UsbManager");

        var device = _manager.DeviceList?.Values.FirstOrDefault(d => d.VendorId == ESP32_VID)
                     ?? _manager.DeviceList?.Values.FirstOrDefault(HasCdcData)
                     ?? throw new InvalidOperationException("找不到 USB 裝置（確認 C6L 已 USB-C 接上、手機支援 OTG）");

        if (!_manager.HasPermission(device) && !await RequestPermissionAsync(device))
            throw new InvalidOperationException("USB 權限被拒絕");

        OpenDevice(device);

        IsConnected = true;
        OnConnectionChanged?.Invoke(true);

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        new Thread(() => ReadLoop(_readCts.Token)) { IsBackground = true }.Start();
        _logger.LogInformation("USB Serial 連線成功（VID=0x{Vid:X4}）", device.VendorId);
    }

    private static bool HasCdcData(UsbDevice d)
    {
        for (int i = 0; i < d.InterfaceCount; i++)
            if (d.GetInterface(i).InterfaceClass == UsbClass.CdcData) return true;
        return false;
    }

    private void OpenDevice(UsbDevice device)
    {
        _conn = _manager!.OpenDevice(device) ?? throw new InvalidOperationException("OpenDevice 失敗");

        UsbInterface? dataIf = null;
        int commIfId = -1;
        for (int i = 0; i < device.InterfaceCount; i++)
        {
            var ifc = device.GetInterface(i);
            if (ifc.InterfaceClass == UsbClass.CdcData) dataIf = ifc;
            else if (ifc.InterfaceClass == UsbClass.Comm) commIfId = ifc.Id;
        }
        // 找不到標準 CDC 資料介面 → 取第一個含 bulk 端點的介面
        if (dataIf is null)
            for (int i = 0; i < device.InterfaceCount && dataIf is null; i++)
            {
                var ifc = device.GetInterface(i);
                if (HasBulk(ifc)) dataIf = ifc;
            }
        if (dataIf is null) throw new InvalidOperationException("找不到 CDC 資料介面");

        _conn.ClaimInterface(dataIf, true);
        for (int j = 0; j < dataIf.EndpointCount; j++)
        {
            var ep = dataIf.GetEndpoint(j);
            if (ep.Direction == UsbAddressing.In) _epIn = ep;
            else _epOut = ep;
        }
        if (_epIn is null || _epOut is null) throw new InvalidOperationException("找不到 USB bulk 端點");

        // SET_CONTROL_LINE_STATE（DTR|RTS=1）→ 讓韌體偵測到 host 連上（HWCDC operator bool）
        try
        {
            int index = commIfId >= 0 ? commIfId : 0;
            _conn.ControlTransfer((UsbAddressing)0x21, 0x22, 0x03, index, null, 0, 200);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SET_CONTROL_LINE_STATE 失敗（部分裝置可略）"); }
    }

    private static bool HasBulk(UsbInterface ifc)
    {
        for (int j = 0; j < ifc.EndpointCount; j++)
            if (ifc.GetEndpoint(j).Type == UsbAddressing.XferBulk) return true;
        return false;
    }

    // ── USB 權限請求（PendingIntent + 廣播回呼）──
    private async Task<bool> RequestPermissionAsync(UsbDevice device)
    {
        var tcs = new TaskCompletionSource<bool>();
        var receiver = new PermissionReceiver(tcs);
        var filter = new IntentFilter(ACTION_USB_PERMISSION);

        // 跨版本安全註冊（Android 13+ 需指定 exported flag；此為 app 內部廣播 → NotExported）
        AndroidX.Core.Content.ContextCompat.RegisterReceiver(
            Ctx, receiver, filter, AndroidX.Core.Content.ContextCompat.ReceiverNotExported);

        var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
            ? PendingIntentFlags.Mutable
            : PendingIntentFlags.UpdateCurrent;
        var pi = PendingIntent.GetBroadcast(Ctx, 0, new Intent(ACTION_USB_PERMISSION), flags);
        _manager!.RequestPermission(device, pi);

        var ok = await tcs.Task;
        try { Ctx.UnregisterReceiver(receiver); } catch { }
        return ok;
    }

    private sealed class PermissionReceiver : BroadcastReceiver
    {
        private readonly TaskCompletionSource<bool> _tcs;
        public PermissionReceiver(TaskCompletionSource<bool> tcs) => _tcs = tcs;
        public override void OnReceive(Context? context, Intent? intent)
            => _tcs.TrySetResult(intent?.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false) ?? false);
    }

    // ── 收訊：bulk read → 累積 → 重同步抽幀 [len][LinkFrame] ──
    private void ReadLoop(CancellationToken ct)
    {
        var acc = new List<byte>(1024);
        var buf = new byte[512];
        try
        {
            while (!ct.IsCancellationRequested && _conn is not null && _epIn is not null)
            {
                int n = _conn.BulkTransfer(_epIn, buf, buf.Length, 200);
                if (n <= 0) continue;
                for (int i = 0; i < n; i++) acc.Add(buf[i]);
                DrainFrames(acc);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "USB 讀取迴圈結束"); }
        finally
        {
            if (IsConnected) { IsConnected = false; OnConnectionChanged?.Invoke(false); }
        }
    }

    private void DrainFrames(List<byte> acc)
    {
        while (acc.Count >= 3)
        {
            int len = (acc[0] << 8) | acc[1];
            byte type = acc[2];
            // 合法幀：長度 1~255 且 LinkFrame 型別 0x01(Data)/0x02(Ctrl)；否則丟 1 byte 重同步
            if (len < 1 || len > 255 || (type != 0x01 && type != 0x02)) { acc.RemoveAt(0); continue; }
            if (acc.Count < 2 + len) break;
            var frame = acc.GetRange(2, len).ToArray();
            acc.RemoveRange(0, 2 + len);
            OnDataReceived?.Invoke(frame);
        }
    }

    // ── 送訊：加 2-byte 大端長度前綴 → bulk write ──
    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (!IsConnected || _conn is null || _epOut is null)
            throw new InvalidOperationException("USB 未連線");

        var f = new byte[2 + data.Length];
        f[0] = (byte)(data.Length >> 8);
        f[1] = (byte)(data.Length & 0xFF);
        Array.Copy(data, 0, f, 2, data.Length);

        int sent = _conn.BulkTransfer(_epOut, f, f.Length, 500);
        if (sent < 0) throw new IOException("USB 寫入失敗");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _readCts?.Cancel();
        try { _conn?.Close(); } catch { /* ignore */ }
        _conn = null; _epIn = null; _epOut = null;
        if (IsConnected) { IsConnected = false; OnConnectionChanged?.Invoke(false); }
        return Task.CompletedTask;
    }
}
#endif
