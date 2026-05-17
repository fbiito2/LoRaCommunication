#pragma once
#include <Arduino.h>
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <BLE2902.h>

// ── BLE 服務與 Characteristic UUID ────────────────────────
#define BLE_DEVICE_NAME   "LoRa-C6L"
#define BLE_SERVICE_UUID  "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
#define BLE_CHAR_RX_UUID  "6E400002-B5A3-F393-E0A9-E50E24DCCA9E" // 手機 Write → C6L
#define BLE_CHAR_TX_UUID  "6E400003-B5A3-F393-E0A9-E50E24DCCA9E" // C6L Notify → 手機

// 收到手機資料的回呼型別
using BleRxCallback = std::function<void(const uint8_t* data, size_t len)>;

class BleService {
public:
    bool begin();
    bool notify(const uint8_t* data, size_t len); // 送資料到手機
    void setRxCallback(BleRxCallback cb);
    bool isConnected() const;

private:
    BLEServer*         _server    = nullptr;
    BLECharacteristic* _txChar    = nullptr; // Notify
    BLECharacteristic* _rxChar    = nullptr; // Write
    BleRxCallback      _rxCallback;
    bool               _connected = false;

    // BLE Server 回呼（連線 / 斷線事件）
    class ServerCallbacks : public BLEServerCallbacks {
    public:
        BleService* svc;
        void onConnect(BLEServer*) override;
        void onDisconnect(BLEServer*) override;
    };

    // Characteristic Write 回呼（手機寫入）
    class CharCallbacks : public BLECharacteristicCallbacks {
    public:
        BleService* svc;
        void onWrite(BLECharacteristic* c) override;
    };
};

extern BleService bleService;
