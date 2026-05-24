#pragma once
#include <functional>

/// @brief 按鈕處理模組
namespace Button {
    using VoidCb = std::function<void()>;

    void init();
    void loop(); // 在 main loop() 呼叫

    void onShortPress(VoidCb cb);    // 短按：切換 OLED 頁面
    void onLongPress(VoidCb cb);     // 長按 3 秒：切換攜帶/基站模式
    void onVeryLongPress(VoidCb cb); // 長按 6 秒：重置 WiFi 設定
}
