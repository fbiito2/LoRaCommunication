#pragma once
#include <Arduino.h>

// ── 封包格式常數 ───────────────────────────────────────────
#define PKT_HEADER_LEN  8    // SRC(2)+DST(2)+HOP(1)+SEQ(2)+TYPE(1)
#define PKT_MAC_LEN     4    // HMAC-SHA256 截斷 4 bytes
#define PKT_MAX_PAYLOAD 247  // 255 - header(8) - mac(4)
#define PKT_MAX_LEN     255  // LoRa 最大封包

// DST_ID 廣播位址
#define DST_BROADCAST   0xFFFF

// 封包類型（TYPE 欄位）
#define PKT_TYPE_TEXT   0x01  // 文字訊息
#define PKT_TYPE_VOICE  0x02  // Codec2 語音幀
#define PKT_TYPE_CTRL   0x03  // 控制/心跳
#define PKT_TYPE_SENSOR 0x04  // 感測器資料

// 最大跳數（HOP 初始值）
#define MAX_HOP         3

/// @brief LoRa 封包結構
struct LoRaPacket {
    uint16_t srcId;           // 原始發送者 ID
    uint16_t dstId;           // 目標接收者 ID（0xFFFF = 廣播）
    uint8_t  hop;             // 剩餘跳數，歸 0 丟棄
    uint16_t seq;             // 遞增封包序號（防重放 + AES nonce）
    uint8_t  type;            // 封包類型
    uint8_t  payload[PKT_MAX_PAYLOAD]; // 加密後的資料
    size_t   payloadLen;      // payload 實際長度（不含 MAC）
    uint8_t  mac[PKT_MAC_LEN]; // HMAC 截斷
};

/// @brief 將 LoRaPacket 序列化為位元組陣列（送 LoRa 發送用）
/// @return 序列化後的總長度
size_t packetSerialize(const LoRaPacket& pkt, uint8_t* out, size_t outLen);

/// @brief 將位元組陣列解析為 LoRaPacket
bool packetDeserialize(const uint8_t* in, size_t inLen, LoRaPacket& pkt);
