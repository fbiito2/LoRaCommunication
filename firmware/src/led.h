#pragma once
#include <M5Unified.h>

/// @brief RGB LED 狀態指示（WS2812C）
namespace Led {
    void init();

    void setCarryMode();     // 攜帶模式已連線 → 綠色常亮
    void setBaseMode();      // 基站模式 WiFi 已啟動 → 藍色常亮
    void setBaseWaiting();   // 基站模式等待手機 → 藍色閃爍
    void setLoRaTx();        // LoRa 發送中 → 紅色閃爍
    void setLoRaRx();        // LoRa 接收中 → 黃色閃爍
    void setError();         // 錯誤 → 紅色常亮
    void setOff();

    void loop();             // 處理閃爍計時，在 main loop() 呼叫
}
