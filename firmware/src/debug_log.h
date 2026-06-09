#pragma once

// USB CDC 同時承載「資料幀」與「除錯 log」。當 USB 傳輸層已有 APP 握手
// （CDC 作為資料通道）時，設為 true 以抑制除錯輸出，避免 log 文字插斷
// 二進位資料幀導致對端解析錯亂。WiFi 連線不受影響（資料走 UDP，獨立通道）。
extern volatile bool g_usbDataMode;
