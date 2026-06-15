#pragma once
#include <Arduino.h>

// ── 封包格式常數 ───────────────────────────────────────────
#define PKT_HEADER_LEN  8    // SRC(2)+DST(2)+HOP(1)+SEQ(2)+TYPE(1)
#define PKT_MAC_LEN     4    // MAC 長度（Phase1 CRC32 / Phase2 HMAC 截斷）
#define PKT_MAX_PAYLOAD 243  // 255 - header(8) - mac(4)
#define PKT_MAX_LEN     255  // LoRa 最大封包

// HOP 欄位在序列化緩衝區中的位移（MAC 計算時跳過，
// 讓中繼節點遞減 HOP 後不破壞 MAC，無需重算或持有金鑰）
#define PKT_HOP_OFFSET  4

// ── DST_ID 定址規則 ────────────────────────────────────────
// 0x0001~0xFFFE = 點對點；0xFFFF = 全體廣播；0xFFE0~0xFFEF = 群組（最多 16 組）
#define DST_BROADCAST   0xFFFF
#define DST_GROUP_MIN   0xFFE0
#define DST_GROUP_MAX   0xFFEF

static inline bool dstIsBroadcast(uint16_t dst) { return dst == DST_BROADCAST; }
static inline bool dstIsGroup(uint16_t dst) {
    return dst >= DST_GROUP_MIN && dst <= DST_GROUP_MAX;
}

// ── 封包類型（TYPE 欄位）───────────────────────────────────
#define PKT_TYPE_TEXT   0x01  // 文字訊息
#define PKT_TYPE_VOICE  0x02  // Codec2 語音幀
#define PKT_TYPE_CTRL   0x03  // 控制/心跳
#define PKT_TYPE_ACK    0x04  // ACK 確認（點對點）
#define PKT_TYPE_PING   0x05  // PING/探測（廣播發現裝置）
#define PKT_TYPE_SOS    0x06  // SOS 緊急求救
#define PKT_TYPE_POS    0x07  // 定位定時廣播（GPS 座標）

// ── PKT_TYPE_CTRL 的子類型（payload[0]）─────────────────────
// 語音 PTT 全網 LoRa 模式切換（F-052）：PTT 期間切 SF7/BW500 高速、結束切回 SF9/BW125
#define CTRL_PTT_START  0x01  // 開始通話：收到→切語音模式(SF7/BW500)
#define CTRL_PTT_END    0x02  // 結束通話：收到→切回長距模式(SF9/BW125)

// 各類型封包的 HOP 初始值
#define MAX_HOP_TEXT    5     // 一般文字/群組/廣播
#define MAX_HOP_ACK     5     // ACK 確認
#define MAX_HOP_PING    5     // PING 探測
#define MAX_HOP_VOICE   3     // 語音（連續封包量大，避免壅塞）
#define MAX_HOP_SOS     15    // SOS 緊急求救（盡可能傳遠）
#define MAX_HOP_POS     1     // 定位廣播（HOP=1，限制洪泛流量）

// 向後相容：舊程式碼用 MAX_HOP 的地方等同文字預設值
#define MAX_HOP         MAX_HOP_TEXT

/// @brief LoRa 封包結構
struct LoRaPacket {
    uint16_t srcId;           // 原始發送者 ID
    uint16_t dstId;           // 目標接收者 ID（見 DST_ID 定址規則）
    uint8_t  hop;             // 剩餘跳數，歸 0 丟棄
    uint16_t seq;             // 遞增封包序號（防重放 + 去重依據）
    uint8_t  type;            // 封包類型
    uint8_t  payload[PKT_MAX_PAYLOAD]; // 資料（Phase2 為加密後）
    size_t   payloadLen;      // payload 實際長度（不含 MAC）
    uint8_t  mac[PKT_MAC_LEN]; // 完整性驗證碼
};

/// @brief 將 LoRaPacket 序列化為位元組陣列（送 LoRa 發送用）
/// @return 序列化後的總長度
size_t packetSerialize(const LoRaPacket& pkt, uint8_t* out, size_t outLen);

/// @brief 將位元組陣列解析為 LoRaPacket
bool packetDeserialize(const uint8_t* in, size_t inLen, LoRaPacket& pkt);
