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
bool     gpsFix     = false;
int      gpsSats    = 0;
double   gpsLat     = 0;
double   gpsLon     = 0;

static Page      _page    = Page::STATUS;
static uint32_t  _lastMs  = 0;
static const uint32_t UPDATE_MS = 1000; // 每秒更新顯示
static uint32_t  _holdUntil = 0;        // 警示畫面（SOS）強制保留到此時間點

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
    if ((int32_t)(_holdUntil - now) > 0) return; // 警示畫面保留中，暫停一般頁面重畫
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

    case Page::GPS:
        // 64×48：每行約 10 字。座標各佔一行（6 位小數剛好放得下）。
        M5.Display.printf("GPS %s\n", gpsFix ? "FIX" : "--");
        M5.Display.printf("sat:%d\n", gpsSats);
        if (gpsFix) {
            M5.Display.printf("%.6f\n", gpsLat);
            M5.Display.printf("%.6f\n", gpsLon);
        } else {
            M5.Display.println("searching");
        }
        break;

    default: break;
    }
}

void showSosSent() {
    M5.Display.fillScreen(TFT_BLACK);
    M5.Display.setCursor(0, 8);
    M5.Display.setTextSize(2);
    M5.Display.println("SOS");
    M5.Display.setTextSize(1);
    M5.Display.println("SENT!");
    M5.Display.printf("ID:%04X\n", deviceId);
    _holdUntil = millis() + 3000; // 強制顯示 3 秒不被覆蓋
}

void showSosReceived(uint16_t fromId) {
    M5.Display.fillScreen(TFT_BLACK);
    M5.Display.setCursor(0, 0);
    M5.Display.setTextSize(1);
    M5.Display.println("!!! SOS !!!");
    M5.Display.printf("FROM:%04X\n", fromId);
    M5.Display.println("EMERGENCY!");
    _holdUntil = millis() + 5000; // 強制顯示 5 秒不被覆蓋（接收端 SOS 警示）
}

// ── POST 自我測試畫面（64×48 OLED）─────────────────────────

void showPostStep(int step, int total, const char* name) {
    M5.Display.fillScreen(TFT_BLACK);
    M5.Display.setTextSize(1);
    M5.Display.setCursor(0, 0);
    M5.Display.printf("POST %d/%d\n", step, total);
    M5.Display.println();
    M5.Display.println(name);
    M5.Display.println("testing..");
}

void showPostPass(int step, int total, const char* name) {
    M5.Display.fillScreen(TFT_BLACK);
    M5.Display.setTextSize(1);
    M5.Display.setCursor(0, 0);
    M5.Display.printf("POST %d/%d\n", step, total);
    M5.Display.println();
    M5.Display.printf("%s\n", name);
    M5.Display.println("  PASS");
}

void showPostFail(const char* name, int errCode) {
    M5.Display.fillScreen(TFT_BLACK);
    M5.Display.setCursor(0, 0);
    M5.Display.setTextSize(2);
    M5.Display.println("FAIL");       // 大字醒目（16px 高）
    M5.Display.setTextSize(1);
    M5.Display.println(name);
    M5.Display.printf("err:%d\n", errCode);
    M5.Display.println("BTN=retry");
}

void showPostDone(uint16_t devId, const char* fwVer) {
    M5.Display.fillScreen(TFT_BLACK);
    M5.Display.setTextSize(1);
    M5.Display.setCursor(0, 0);
    M5.Display.println("ALL PASS");
    M5.Display.println();
    M5.Display.printf("ID:%04X\n", devId);
    M5.Display.printf("FW:%s\n", fwVer);
    M5.Display.println("Ready!");
}

} // namespace Display
