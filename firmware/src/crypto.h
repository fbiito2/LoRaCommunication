#pragma once
#include <stdint.h>
#include <stddef.h>

// ── Phase 切換旗標 ─────────────────────────────────────────
// Phase 1（預設）：不加密，MAC 用 CRC32 做完整性檢查
// Phase 2：於 build_flags 定義 ENABLE_ENCRYPTION=1，啟用 AES-128-CTR + HMAC-SHA256
#ifndef ENABLE_ENCRYPTION
#define ENABLE_ENCRYPTION 0
#endif

// ── 長度常數 ───────────────────────────────────────────────
#define MAC_LEN       4    // CRC32 與 HMAC 截斷皆為 4 bytes
#define AES_KEY_LEN   16   // AES-128（Phase 2）
#define HMAC_KEY_LEN  32   // HMAC-SHA256（Phase 2）

/// @brief 加密模組初始化
/// @param aesKey  Phase 2 使用；Phase 1 可傳 nullptr
/// @param hmacKey Phase 2 使用；Phase 1 可傳 nullptr
void cryptoInit(const uint8_t* aesKey, const uint8_t* hmacKey);

/// @brief 加密 payload（就地）
///        Phase 1：直通不變更；Phase 2：AES-128-CTR（nonce = srcId+seq）
void payloadEncrypt(uint8_t* payload, size_t len, uint16_t srcId, uint16_t seq);

/// @brief 解密 payload（與加密同邏輯，CTR 模式可逆）
void payloadDecrypt(uint8_t* payload, size_t len, uint16_t srcId, uint16_t seq);

/// @brief 計算封包 MAC（計算時跳過 HOP 欄位，使中繼改 HOP 不破壞 MAC）
///        Phase 1：CRC32；Phase 2：HMAC-SHA256 截斷 4 bytes
/// @param buf 序列化的 header + payload（不含尾端 MAC）
void packetMac(const uint8_t* buf, size_t len, uint8_t mac[MAC_LEN]);

/// @brief 驗證封包 MAC（時序安全比較）
bool packetMacVerify(const uint8_t* buf, size_t len, const uint8_t mac[MAC_LEN]);
