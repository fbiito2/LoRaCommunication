using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Microsoft.Extensions.Logging;

namespace LoRaPTT.Services;

public class BleService : IBleService
{
    // UUID 必須與韌體 ble_service.h 一致
    private const string ServiceUuid  = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E";
    private const string CharRxUuid   = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"; // APP → C6L
    private const string CharTxUuid   = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"; // C6L → APP
    private const string DeviceName   = "LoRa-C6L";

    private readonly IBluetoothLE _ble;
    private readonly IAdapter     _adapter;
    private readonly ILogger<BleService> _logger;

    private IDevice?         _device;
    private ICharacteristic? _txChar; // Notify（接收）
    private ICharacteristic? _rxChar; // Write（發送）

    public bool IsConnected => _device?.State == Plugin.BLE.Abstractions.DeviceState.Connected;
    public event Action<byte[]>? OnDataReceived;
    public event Action<bool>?   OnConnectionChanged;

    public BleService(ILogger<BleService> logger)
    {
        _logger  = logger;
        _ble     = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;

        _adapter.DeviceDisconnected += (_, e) => OnConnectionChanged?.Invoke(false);
    }

    public async Task<bool> ScanAndConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("開始掃描 BLE 裝置：{Name}", DeviceName);

        IDevice? found = null;
        _adapter.DeviceDiscovered += (_, e) =>
        {
            if (e.Device.Name == DeviceName)
                found = e.Device;
        };

        await _adapter.StartScanningForDevicesAsync(cancellationToken: ct);

        if (found is null)
        {
            _logger.LogWarning("找不到裝置 {Name}", DeviceName);
            return false;
        }

        await _adapter.ConnectToDeviceAsync(found, cancellationToken: ct);
        _device = found;

        var service = await _device.GetServiceAsync(Guid.Parse(ServiceUuid), ct);
        _txChar = await service.GetCharacteristicAsync(Guid.Parse(CharTxUuid));
        _rxChar = await service.GetCharacteristicAsync(Guid.Parse(CharRxUuid));

        // 訂閱 Notify（C6L → APP）
        _txChar.ValueUpdated += OnTxCharValueUpdated;
        await _txChar.StartUpdatesAsync(ct);

        _logger.LogInformation("BLE 連線成功");
        OnConnectionChanged?.Invoke(true);
        return true;
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_rxChar is null || !IsConnected)
            throw new InvalidOperationException("BLE 未連線");

        await _rxChar.WriteAsync(data, ct);
    }

    public async Task DisconnectAsync()
    {
        if (_device is not null)
            await _adapter.DisconnectDeviceAsync(_device);
    }

    private void OnTxCharValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        var data = e.Characteristic.Value;
        if (data?.Length > 0)
            OnDataReceived?.Invoke(data);
    }
}
