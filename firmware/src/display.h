#pragma once
#include <M5Unified.h>

/// @brief OLED 顯示模組（SSD1306 64×48）
namespace Display {
    enum class Page { STATUS = 0, NETWORK, LORA, RELAY, GPS, PAGE_COUNT };

    void init();
    void nextPage();     // 短按按鈕切換
    void loop();         // 定期更新顯示內容，在 main loop() 呼叫
    void wake();         // 喚醒螢幕並重置閒置計時（按鍵/收發訊息時呼叫）
    bool isAwake();      // 螢幕是否亮著（按鈕「睡著時第一下只喚醒不換頁」用）
    void showSosSent();              // SOS 發送確認畫面（F-071）
    void showSosReceived(uint16_t fromId); // 收到 SOS 警報畫面（F-073）

    // POST 自我測試顯示
    void showPostStep(int step, int total, const char* name);  // 顯示「測試中...」
    void showPostPass(int step, int total, const char* name);  // 步驟通過
    void showPostFail(const char* name, int errCode);          // 步驟失敗（含重試提示）
    void showPostDone(uint16_t deviceId, const char* fwVer);   // 全部通過

    // 狀態資料（由各模組更新）
    extern uint16_t deviceId;     // 本機 Device ID（F-001）
    extern String   deviceName;   // 本機暱稱（F-002）
    extern String   appStatus;    // APP 連線狀態："USB"/"WiFi"/"USB+WiFi"/"none"
    extern String   fwVer;        // 韌體版本（顯示於第一頁，F-064）
    extern bool     wifiApOn;     // WiFi AP 是否開啟（F-041 顯示用）
    extern float    loraFreq;     // LoRa 頻率（MHz）
    extern uint8_t  loraSf;       // 展頻因子
    extern int16_t  lastRssi;     // 最近收到封包的 RSSI（dBm，F-034）
    extern String   wifiSsid;
    extern String   wifiIp;
    extern uint32_t rxCount;      // 收到（給自己）封包數
    extern uint32_t txCount;      // 代 APP 送出封包數
    extern uint32_t relayCount;   // 中繼轉發封包數
    // GPS（選配，Grove UART）
    extern bool     gpsFix;       // 是否已定位
    extern int      gpsSats;      // 衛星數
    extern double   gpsLat;       // 緯度
    extern double   gpsLon;       // 經度
}
