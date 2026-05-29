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

// 最大跳數（HOP 初始值）
#define MAX_HOP         3

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
