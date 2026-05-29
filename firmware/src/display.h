#pragma once
#include <M5Unified.h>

/// @brief OLED 顯示模組（SSD1306 64×48）
namespace Display {
    enum class Page { STATUS = 0, NETWORK, LORA, RELAY, PAGE_COUNT };

    void init();
    void nextPage();     // 短按按鈕切換
    void loop();         // 定期更新顯示內容，在 main loop() 呼叫

    // 狀態資料（由各模組更新）
    extern uint16_t deviceId;     // 本機 Device ID（F-001）
    extern String   deviceName;   // 本機暱稱（F-002）
    extern String   appStatus;    // APP 連線狀態："USB"/"WiFi"/"USB+WiFi"/"none"
    extern float    loraFreq;     // LoRa 頻率（MHz）
    extern uint8_t  loraSf;       // 展頻因子
    extern int16_t  lastRssi;     // 最近收到封包的 RSSI（dBm，F-034）
    extern String   wifiSsid;
    extern String   wifiIp;
    extern uint32_t rxCount;      // 收到（給自己）封包數
    extern uint32_t txCount;      // 代 APP 送出封包數
    extern uint32_t relayCount;   // 中繼轉發封包數
}
