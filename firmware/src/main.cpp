#include <Arduino.h>
#include <ArduinoJson.h>
#include "config.h"
#include "crypto.h"
#include "comm_interface.h"
#include "usb_serial_service.h"
#include "wifi_service.h"
#include "lora_handler.h"
#include "relay.h"
#include "power_mgr.h"
#include "display.h"
#include "button.h"
#include "led.h"

// ── 全域狀態 ──────────────────────────────────────────────
static bool           _isCarryMode = true;  // true=USB，false=WiFi
static ICommInterface* _comm       = nullptr;
static DeviceConfig    _cfg;

// ── 模式偵測：DTR 訊號判斷是否有手機連上 USB-C ─────────────
static bool detectCarryMode() {
    delay(500); // 等待 USB 穩定
    // ESP32-C6 HWCDC：operator bool() 為 true 代表 USB host 已連接
    return (bool)Serial;
}

// ── 收到手機資料 → LoRa 發送 ──────────────────────────────
// 手機送來的是完整 LoRa 封包（APP 已填好 DST_ID / TYPE / PAYLOAD），
// 韌體僅覆寫 SRC_ID / SEQ / HOP 並重算 MAC（於 sendPacket 內完成）。
// 手機端 SRC/SEQ/HOP/MAC 為佔位值，會被忽略。
static void onCommReceived(const uint8_t* data, size_t len) {
    PowerMgr::onActivity(); // 有活動 → 切換到通話模式

    LoRaPacket pkt;
    if (!packetDeserialize(data, len, pkt)) {
        Serial.println("[Main] 手機封包解析失敗，丟棄");
        return;
    }

    Led::setLoRaTx();
    loraHandler.sendPacket(pkt); // 內部填寫 SRC/SEQ/HOP + MAC
    Led::setCarryMode();         // 恢復狀態 LED
}

// ── 收到 LoRa 封包（已解密）→ 推給手機 ─────────────────────
static void onLoRaReceived(const LoRaPacket& pkt) {
    PowerMgr::onActivity();
    Led::setLoRaRx();

    uint8_t buf[PKT_MAX_LEN];
    size_t  len = packetSerialize(pkt, buf, sizeof(buf));
    if (len > 0) _comm->send(buf, len);

    Led::setCarryMode();
}

// ── JSON 設定指令處理（從手機 USB Serial 收到）──────────────
static void handleConfigCmd(const char* json, size_t len) {
    JsonDocument doc;
    if (deserializeJson(doc, json, len) != DeserializationError::Ok) return;

    const char* cmd = doc["cmd"];
    if (!cmd) return;

    if (strcmp(cmd, "set_config") == 0) {
        if (doc["wifi_ssid"].is<const char*>())
            strncpy(_cfg.wifiSsid, doc["wifi_ssid"], 31);
        if (doc["wifi_pass"].is<const char*>())
            strncpy(_cfg.wifiPass, doc["wifi_pass"], 31);
        if (doc["device_name"].is<const char*>())
            strncpy(_cfg.deviceName, doc["device_name"], 31);
        if (doc["lora_freq"].is<float>())
            _cfg.loraFreq = doc["lora_freq"];
        configSave(_cfg);
        // 回應手機
        const char* resp = "{\"status\":\"ok\"}";
        _comm->send((const uint8_t*)resp, strlen(resp));
    }
}

void setup() {
    Serial.begin(115200);
    delay(500);
    Serial.println("=== LoRa PTT Bridge 啟動 ===");

    // 載入設定
    _cfg = configLoad();
    Serial.printf("[Main] 裝置 ID: 0x%04X，名稱: %s\n",
                  _cfg.deviceId, _cfg.deviceName);

    // 初始化加密模組
    cryptoInit(_cfg.aesKey, _cfg.hmacKey);

    // 偵測使用模式（USB DTR = 攜帶模式，否則 WiFi 基站模式）
    _isCarryMode = detectCarryMode();

    if (_isCarryMode) {
        Serial.println("[Main] 攜帶模式：USB Serial CDC");
        usbSerialService.begin();
        _comm = &usbSerialService;
        Led::setCarryMode();
        Display::isCarryMode = true;
    } else {
        Serial.println("[Main] 基站模式：WiFi AP + UDP");
        wifiService.setSsid(_cfg.wifiSsid);
        wifiService.setPassword(_cfg.wifiPass);
        wifiService.begin();
        _comm = &wifiService;
        Led::setBaseWaiting();
        Display::isCarryMode = false;
        Display::wifiSsid    = _cfg.wifiSsid;
        Display::wifiIp      = WiFi.softAPIP().toString();
    }

    // 手機 → C6L 資料 → LoRa 發送
    _comm->onReceive([](const uint8_t* d, size_t l) {
        onCommReceived(d, l);
    });

    // 初始化 LoRa
    loraHandler.begin(_cfg.deviceId);
    loraHandler.setPacketCallback(onLoRaReceived);

    // Relay 初始化：轉發時用 sendRaw 原樣送出，保留原始 SRC/SEQ/MAC（去重依據）
    relayHandler.init(_cfg.deviceId, [](const uint8_t* d, size_t l) {
        return loraHandler.sendRaw(d, l);
    });

    // 電源管理（RxDutyCycle 省電）
    PowerMgr::init([](bool en) { loraHandler.setDutyCycle(en); });

    // HMI 初始化
    Display::init();
    Button::init();
    Button::onShortPress([]() { Display::nextPage(); });
    Button::onLongPress([]() {
        Serial.println("[Main] 長按 3 秒：切換模式（重開機生效）");
        M5.Speaker.tone(1000, 200);
    });
    Button::onVeryLongPress([]() {
        Serial.println("[Main] 長按 6 秒：重置 WiFi 設定");
        configReset();
        M5.Speaker.tone(500, 500);
    });

    Serial.println("=== 橋接就緒 ===");
}

void loop() {
    // 各模組輪詢
    if (_isCarryMode) usbSerialService.loop();
    else              wifiService.loop();

    loraHandler.loop();
    PowerMgr::loop();
    Display::loop();
    Button::loop();
    Led::loop();

    delay(1);
}
