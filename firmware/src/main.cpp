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

// ── 韌體版本（F-064 版本查詢）──────────────────────────────
#define FW_VERSION "0.3.0"

// ── 線路幀類型（手機 ↔ C6L，USB/WiFi 共用）─────────────────
// 傳輸層各自負責邊界（USB 2-byte 長度前綴 / WiFi 一個 datagram）。
//   LINK_DATA：phone→C6L = [01][LoRa封包]；C6L→phone = [01][RSSI int16 BE][LoRa封包]
//   LINK_CTRL：控制/設定 JSON（雙向）
#define LINK_DATA 0x01
#define LINK_CTRL 0x02

// ── 傳輸層登錄（雙傳輸層多工：USB + WiFi 可同時運作）────────
struct Transport {
    ICommInterface* comm;
    bool            hasApp; // 是否已收到 APP 握手（F-053）
};
static Transport _transports[2];
static int       _transportCount = 0;
static DeviceConfig _cfg;

static bool anyAppConnected() {
    for (int i = 0; i < _transportCount; i++)
        if (_transports[i].hasApp) return true;
    return false;
}

// 送出 CTRL（JSON）給指定傳輸層
static void sendCtrl(int idx, const char* json, size_t jsonLen) {
    if (idx < 0 || idx >= _transportCount) return;
    uint8_t buf[256];
    if (jsonLen + 1 > sizeof(buf)) return;
    buf[0] = LINK_CTRL;
    memcpy(buf + 1, json, jsonLen);
    _transports[idx].comm->send(buf, jsonLen + 1);
}

// 推送 DATA 幀給「所有已握手」的傳輸層
static void broadcastToApps(const uint8_t* frame, size_t len) {
    for (int i = 0; i < _transportCount; i++)
        if (_transports[i].hasApp)
            _transports[i].comm->send(frame, len);
}

// ── 收到 LoRa 封包（給自己/廣播/群組，已解密）→ 推給手機 ────
static void onLoRaReceived(const LoRaPacket& pkt, int16_t rssi) {
    PowerMgr::onActivity();
    Led::setLoRaRx();
    Display::lastRssi = rssi;
    Display::rxCount++;

    // F-043：收到文字訊息短響一聲（語音幀過於頻繁，不蜂鳴）
    if (pkt.type == PKT_TYPE_TEXT) M5.Speaker.tone(2000, 80);

    // C6L→phone DATA：[01][RSSI hi][RSSI lo][LoRa封包]
    uint8_t buf[3 + PKT_MAX_LEN];
    buf[0] = LINK_DATA;
    buf[1] = (uint8_t)((rssi >> 8) & 0xFF);
    buf[2] = (uint8_t)(rssi & 0xFF);
    size_t plen = packetSerialize(pkt, buf + 3, sizeof(buf) - 3);
    if (plen > 0) broadcastToApps(buf, 3 + plen);

    Led::setCarryMode();
}

// ── CTRL（JSON 控制/設定）處理 ──────────────────────────────
static void handleCtrl(int idx, const char* json, size_t len) {
    JsonDocument doc;
    if (deserializeJson(doc, json, len) != DeserializationError::Ok) return;
    const char* cmd = doc["cmd"];
    if (!cmd) return;

    if (strcmp(cmd, "hello") == 0) {
        // F-053 握手：標記此傳輸層「有 APP」，回 hello-ack（含 device_id / fw_ver）
        _transports[idx].hasApp = true;
        if (doc["name"].is<const char*>())
            Serial.printf("[Main] APP 握手於傳輸層 %d，暱稱: %s\n",
                          idx, doc["name"].as<const char*>());
        // 握手成功的 LED 提示：USB=綠、WiFi=藍
        if (idx == 0) Led::setCarryMode(); else Led::setBaseMode();

        JsonDocument ack;
        ack["status"]    = "hello";
        ack["device_id"] = _cfg.deviceId;
        ack["name"]      = _cfg.deviceName;
        ack["fw_ver"]    = FW_VERSION;
        char out[160];
        size_t n = serializeJson(ack, out, sizeof(out));
        sendCtrl(idx, out, n);
        return;
    }

    if (strcmp(cmd, "set_config") == 0) {
        // F-051 設定指令
        if (doc["wifi_ssid"].is<const char*>())
            strncpy(_cfg.wifiSsid, doc["wifi_ssid"], 31);
        if (doc["wifi_pass"].is<const char*>())
            strncpy(_cfg.wifiPass, doc["wifi_pass"], 31);
        if (doc["device_name"].is<const char*>())
            strncpy(_cfg.deviceName, doc["device_name"], 31);
        if (doc["lora_freq"].is<float>())
            _cfg.loraFreq = doc["lora_freq"];
        configSave(_cfg);
        const char* resp = "{\"status\":\"ok\"}";
        sendCtrl(idx, resp, strlen(resp));
        return;
    }
}

// ── 收到手機線路幀（USB 或 WiFi）──────────────────────────
// DATA：手機送來完整 LoRa 封包（DST/TYPE/SEQ/PAYLOAD），韌體覆寫 SRC/HOP/MAC 後發送。
static void onLinkReceived(int idx, const uint8_t* data, size_t len) {
    if (len < 1) return;
    uint8_t type = data[0];

    if (type == LINK_DATA) {
        PowerMgr::onActivity();
        LoRaPacket pkt;
        if (!packetDeserialize(data + 1, len - 1, pkt)) {
            Serial.println("[Main] 手機封包解析失敗，丟棄");
            return;
        }
        Led::setLoRaTx();
        loraHandler.sendPacket(pkt);
        Display::txCount++;
        Led::setCarryMode();
    } else if (type == LINK_CTRL) {
        handleCtrl(idx, (const char*)(data + 1), len - 1);
    }
    // 其他類型（含 WiFiCommService 的註冊用空封包）忽略
}

void setup() {
    Serial.begin(115200);
    delay(500);
    Serial.println("=== LoRa PTT Bridge 啟動 ===");

    _cfg = configLoad();
    Serial.printf("[Main] 裝置 ID: 0x%04X，名稱: %s，韌體: %s\n",
                  _cfg.deviceId, _cfg.deviceName, FW_VERSION);

    cryptoInit(_cfg.aesKey, _cfg.hmacKey);

    // ── 雙傳輸層同時啟動（無模式切換）──────────────────────
    // USB Serial CDC（手機 USB-C 直連時可用；純供電則不會送握手）
    usbSerialService.begin();
    _transports[0] = { &usbSerialService, false };

    // WiFi AP（預設常開）。SSID 預設帶 Device ID（F-050）
    if (strcmp(_cfg.wifiSsid, "LoRaPTT") == 0) {
        static char ssidBuf[32];
        snprintf(ssidBuf, sizeof(ssidBuf), "LoRaPTT_%04X", _cfg.deviceId);
        wifiService.setSsid(ssidBuf);
    } else {
        wifiService.setSsid(_cfg.wifiSsid);
    }
    wifiService.setPassword(_cfg.wifiPass);
    wifiService.begin();
    _transports[1] = { &wifiService, false };
    _transportCount = 2;

    // 兩傳輸層各自掛 onReceive，帶入索引
    usbSerialService.onReceive([](const uint8_t* d, size_t l) { onLinkReceived(0, d, l); });
    wifiService.onReceive(   [](const uint8_t* d, size_t l) { onLinkReceived(1, d, l); });

    // Display 裝置與網路資訊
    Display::deviceId   = _cfg.deviceId;
    Display::deviceName = _cfg.deviceName;
    Display::wifiSsid   = WiFi.softAPSSID();
    Display::wifiIp     = WiFi.softAPIP().toString();

    // LoRa
    loraHandler.begin(_cfg.deviceId);
    loraHandler.setPacketCallback(onLoRaReceived);

    // Relay：轉發用 sendRaw 原樣送出，保留原始 SRC/SEQ/MAC（去重依據）
    relayHandler.init(_cfg.deviceId, [](const uint8_t* d, size_t l) {
        return loraHandler.sendRaw(d, l);
    });

    PowerMgr::init([](bool en) { loraHandler.setDutyCycle(en); });

    // HMI
    Display::init();
    Led::setBaseWaiting(); // WiFi AP 已開、尚無 APP 握手 → 藍色閃爍
    Button::init();
    Button::onShortPress([]() { Display::nextPage(); });
    Button::onLongPress([]() {
        // F-041：長按 3 秒 → 開/關 WiFi AP（省電）
        bool en = !wifiService.isApEnabled();
        wifiService.setApEnabled(en);
        if (en) { wifiService.onReceive([](const uint8_t* d, size_t l) { onLinkReceived(1, d, l); });
                  Display::wifiSsid = WiFi.softAPSSID();
                  Display::wifiIp   = WiFi.softAPIP().toString(); }
        else    { _transports[1].hasApp = false; }
        M5.Speaker.tone(en ? 1500 : 800, 200);
    });
    Button::onVeryLongPress([]() {
        Serial.println("[Main] 長按 6 秒：重置設定");
        configReset();
        M5.Speaker.tone(500, 500);
    });

    Serial.println("=== 橋接就緒（USB + WiFi 雙傳輸層）===");
}

void loop() {
    usbSerialService.loop();
    wifiService.loop();

    // 更新 OLED 統計與 APP 連線狀態
    bool u = _transports[0].hasApp, w = _transports[1].hasApp;
    Display::appStatus  = (u && w) ? "USB+WiFi" : u ? "USB" : w ? "WiFi" : "none";
    Display::relayCount = relayHandler.forwardCount();

    loraHandler.loop();
    PowerMgr::loop();
    Display::loop();
    Button::loop();
    Led::loop();

    delay(1);
}
