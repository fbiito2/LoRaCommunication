#include "display.h"

namespace Display {

bool    isCarryMode = true;
int     peerCount   = 0;
float   loraFreq    = 920.0f;
uint8_t loraSf      = 7;
String  wifiSsid    = "LoRaPTT";
String  wifiIp      = "192.168.4.1";

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
        M5.Display.println("LoRaPTT");
        M5.Display.printf("Mode:%s\n", isCarryMode ? "USB" : "WiFi");
        M5.Display.printf("Peer:%d\n", peerCount);
        break;

    case Page::NETWORK:
        M5.Display.println("Network");
        M5.Display.printf("WiFi:%s\n", isCarryMode ? "OFF" : "ON");
        if (!isCarryMode) {
            M5.Display.printf("%.8s\n", wifiSsid.c_str());
            M5.Display.printf("%.13s\n", wifiIp.c_str());
        }
        break;

    case Page::LORA:
        M5.Display.println("LoRa");
        M5.Display.printf("%.0fMHz\n", loraFreq);
        M5.Display.printf("SF:%d\n", loraSf);
        M5.Display.printf("+22dBm\n");
        break;

    default: break;
    }
}

} // namespace Display
