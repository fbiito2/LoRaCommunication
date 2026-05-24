#pragma once
#include <M5Unified.h>

/// @brief OLED 顯示模組（SSD1306 64×48）
namespace Display {
    enum class Page { STATUS = 0, NETWORK, LORA, PAGE_COUNT };

    void init();
    void nextPage();     // 短按按鈕切換
    void loop();         // 定期更新顯示內容，在 main loop() 呼叫

    // 狀態資料（由各模組更新）
    extern bool    isCarryMode;
    extern int     peerCount;
    extern float   loraFreq;
    extern uint8_t loraSf;
    extern String  wifiSsid;
    extern String  wifiIp;
}
