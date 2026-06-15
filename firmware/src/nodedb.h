#pragma once
#include <Arduino.h>

/// @brief 節點資料庫（NodeDB，參考 Meshtastic）
/// 從收到的任何封包自動建檔，記錄鄰居節點：ID / 最後聽到 / RSSI / 暱稱 / 座標。
/// 提供態勢感知（哪些節點還活著、訊號多強、在哪），供 OLED 與 APP 顯示。
namespace NodeDb {
    struct Node {
        uint16_t id;
        uint32_t lastMs;    // 最後聽到時間（millis）
        int16_t  rssi;      // 最後聽到的 RSSI
        char     name[16];  // 暱稱（從 PING 回覆學到，可能為空）
        double   lat, lon;  // 最後已知座標（從 POS/SOS 學到）
        bool     hasPos;
    };

    void heard(uint16_t id, int16_t rssi);            // 收到任何封包 → 更新存在/RSSI/時間
    void setName(uint16_t id, const char* name);      // 學到暱稱（PING 回覆）
    void setPos(uint16_t id, double lat, double lon); // 學到座標（POS/SOS）
    int  count();                                     // 目前已知節點數
    int  snapshot(Node* out, int maxN);               // 取「最近聽到」排序快照，回實際筆數
}
