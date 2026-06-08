#pragma once
#include <cstdint>
#include <functional>

/// @brief 按鈕處理模組
namespace Button {
    using VoidCb = std::function<void()>;

    void init();
    void loop(); // 在 main loop() 呼叫

    void onShortPress(VoidCb cb);    // 短按：切換 OLED 頁面
    void onLongPress(VoidCb cb);     // 長按 3 秒：開/關 WiFi AP
    void onVeryLongPress(VoidCb cb); // 長按 6 秒：重置設定
    void onTriplePress(VoidCb cb);   // 連按 3 下（1 秒內）：SOS 求救（F-071）

    // 偵錯用：邊緣事件計數器
    struct DebugInfo {
        uint32_t wasPressedCnt;   // M5.BtnA.wasPressed() 觸發次數
        uint32_t wasReleasedCnt;  // M5.BtnA.wasReleased() 觸發次數
        uint32_t shortCbCnt;      // shortCb 實際執行次數
        uint32_t tripleCbCnt;     // tripleCb 實際執行次數
        int      tapCount;        // 目前累積連擊數
        bool     pressing;        // 目前是否按住中
    };
    DebugInfo getDebugInfo();
}
