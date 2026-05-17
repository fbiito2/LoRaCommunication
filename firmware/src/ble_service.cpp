#include "ble_service.h"

BleService bleService;

// ── Server 連線 / 斷線事件 ─────────────────────────────────
void BleService::ServerCallbacks::onConnect(BLEServer*) {
    svc->_connected = true;
    Serial.println("[BLE] 手機已連線");
}

void BleService::ServerCallbacks::onDisconnect(BLEServer* srv) {
    svc->_connected = false;
    Serial.println("[BLE] 手機已斷線，重新開始廣播...");
    srv->startAdvertising();
}

// ── Characteristic Write 事件（手機 → C6L）─────────────────
void BleService::CharCallbacks::onWrite(BLECharacteristic* c) {
    uint8_t* data = c->getData();
    size_t   len  = c->getLength();
    if (len > 0 && svc->_rxCallback) {
        svc->_rxCallback(data, len);
    }
}

// ── 初始化 BLE GATT Server ────────────────────────────────
bool BleService::begin() {
    BLEDevice::init(BLE_DEVICE_NAME);

    _server = BLEDevice::createServer();
    auto* srvCb = new ServerCallbacks();
    srvCb->svc = this;
    _server->setCallbacks(srvCb);

    BLEService* svc = _server->createService(BLE_SERVICE_UUID);

    // TX Characteristic（C6L → 手機，Notify）
    _txChar = svc->createCharacteristic(
        BLE_CHAR_TX_UUID,
        BLECharacteristic::PROPERTY_NOTIFY
    );
    _txChar->addDescriptor(new BLE2902());

    // RX Characteristic（手機 → C6L，Write）
    _rxChar = svc->createCharacteristic(
        BLE_CHAR_RX_UUID,
        BLECharacteristic::PROPERTY_WRITE |
        BLECharacteristic::PROPERTY_WRITE_NR
    );
    auto* charCb = new CharCallbacks();
    charCb->svc = this;
    _rxChar->setCallbacks(charCb);

    svc->start();
    _server->getAdvertising()->start();

    Serial.printf("[BLE] 廣播中，裝置名稱: %s\n", BLE_DEVICE_NAME);
    return true;
}

// ── 傳送 Notify 到手機 ────────────────────────────────────
bool BleService::notify(const uint8_t* data, size_t len) {
    if (!_connected) return false;
    _txChar->setValue(const_cast<uint8_t*>(data), len);
    _txChar->notify();
    return true;
}

void BleService::setRxCallback(BleRxCallback cb) {
    _rxCallback = cb;
}

bool BleService::isConnected() const {
    return _connected;
}
