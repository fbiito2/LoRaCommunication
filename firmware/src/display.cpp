#include "display.h"

namespace Display {

uint16_t deviceId   = 0;
String   deviceName = "LoRaPTT";
String   appStatus  = "none";
float    loraFreq   = 920.0f;
uint8_t  loraSf     = 7;
int16_t  lastRssi   = 0;
String   wifiSsid   = "LoRaPTT";
String   wifiIp     = "192.168.4.1";
uint32_t rxCount    = 0;
uint32_t txCount    = 0;
uint32_t relayCount = 0;

static Page      _page    = Page::STATUS;
static uint32_t  _lastMs  = 0;
static const uint32_t UPDATE_MS = 1000; // 每秒更新顯示

void init() {
    // M5Unified 已在 M5.begin() 初始化 OLED
    M5.Display.setRotation(0);
    M5.Display.setTextSize(1);
    M5.Display.fillScreen(TFT_BLACK);
    Serial.println("[Display] OLED 初始化完成");
}

void nextPage() {
    int next = ((int)_page + 1) % (int)Page::PAGE_COUNT;
    _page = (Page)next;
    _lastMs = 0; // 強制立即更新
}

void loop() {
    uint32_t now = millis();
    if (now - _lastMs < UPDATE_MS) return;
    _lastMs = now;

    M5.Display.fillScreen(TFT_BLACK);
    M5.Display.setCursor(0, 0);

    switch (_page) {
    case Page::STATUS:
        M5.Display.printf("ID:%04X\n", deviceId);
        M5.Display.printf("%.8s\n", deviceName.c_str());
        M5.Display.printf("APP:%s\n", appStatus.c_str());
        break;

    case Page::NETWORK:
        M5.Display.println("Network");
        M5.Display.printf("%.10s\n", wifiSsid.c_str());
        M5.Display.printf("%.13s\n", wifiIp.c_str());
        break;

    case Page::LORA:
        M5.Display.println("LoRa");
        M5.Display.printf("%.0fMHz SF%d\n", loraFreq, loraSf);
        M5.Display.printf("RSSI:%d\n", lastRssi);
        break;

    case Page::RELAY:
        M5.Display.println("Relay");
        M5.Display.printf("Fwd:%lu\n", (unsigned long)relayCount);
        M5.Display.printf("Rx:%lu Tx:%lu\n",
                          (unsigned long)rxCount, (unsigned long)txCount);
        break;

    default: break;
    }
}

} // namespace Display
