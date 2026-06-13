#pragma once
#include <Arduino.h>

/// @brief GPS 讀取（M5Stack GPS Unit U032 / AT6668，UART NMEA）
///
/// 接在 C6L Grove PORT.A：GPS TXD→GPIO4(C6L RX)、GPS RXD→GPIO5(C6L TX)、5V、GND。
/// 用 Serial1 讀 NMEA，解析 GGA 取得 fix/衛星數/經緯度。
/// 鮑率自動偵測（9600↔38400 輪替），避免燒錯版本。
namespace Gps {
    void begin();              // 初始化 Serial1（GPIO4=RX, GPIO5=TX）
    void loop();               // 在主迴圈呼叫：讀取並解析 NMEA、必要時切換鮑率

    bool        hasFix();      // 是否已定位（GGA fix quality > 0）
    int         sats();        // 使用中衛星數
    double      lat();         // 緯度（十進位度，南為負）
    double      lon();         // 經度（十進位度，西為負）
    uint32_t    rxBytes();     // 累計收到位元組（診斷：>0=線通且鮑率對）
    uint32_t    baud();        // 目前鮑率
    uint32_t    rxPin();       // 目前鎖定/嘗試的 RX 腳位（4 或 5）
    uint32_t    sentences();   // 累計解析到的有效 GGA 句數
}
