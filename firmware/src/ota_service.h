#pragma once
#include <Arduino.h>

/// @brief 韌體 OTA 更新服務（F-060~064）
///
/// 在 WiFi AP 上開 HTTP 伺服器（port 80），APP 以 multipart POST 上傳 .bin。
/// 採用 TCP/HTTP（非 UDP）以確保韌體影像可靠傳輸。
///   GET  /version → {"fw_ver":"x.y.z"}（F-064）
///   POST /update  → 上傳韌體，寫入非作用中的 OTA 分割區（F-060/061）
/// OLED 顯示進度 %（F-062）。
namespace Ota {
    void begin(const char* fwVersion); // 註冊路由並啟動 HTTP 伺服器
    void loop();                       // 在 main loop() 呼叫，處理 HTTP client
    bool isActive();                   // 更新進行中
    int  progress();                   // 0~100
}
