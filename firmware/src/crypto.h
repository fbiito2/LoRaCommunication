#pragma once
#include <stdint.h>
#include <stddef.h>

// ── 金鑰長度 ───────────────────────────────────────────────
#define AES_KEY_LEN    16   // AES-128
#define HMAC_KEY_LEN   32   // HMAC-SHA256
#define HMAC_MAC_LEN    4   // 截斷取前 4 bytes

/// @brief 加密模組初始化（載入 PSK）
void cryptoInit(const uint8_t aesKey[AES_KEY_LEN],
                const uint8_t hmacKey[HMAC_KEY_LEN]);

/// @brief AES-128-CTR 加密（加解密同一函式）
/// @param nonce 16 bytes，建議組成：[SRC_ID 2B][SEQ 2B][0x00 * 12]
void aesCtr(const uint8_t* in, uint8_t* out, size_t len,
            const uint8_t nonce[16]);

/// @brief 計算 HMAC-SHA256 並截斷為 4 bytes MAC
void hmacCompute(const uint8_t* data, size_t len, uint8_t mac[HMAC_MAC_LEN]);

/// @brief 比對 MAC（時序安全比較，防時序攻擊）
bool hmacVerify(const uint8_t* data, size_t len,
                const uint8_t expectedMac[HMAC_MAC_LEN]);

/// @brief 組合 AES-CTR nonce：[srcId 2B][seq 2B][0x00 * 12]
void buildNonce(uint16_t srcId, uint16_t seq, uint8_t nonce[16]);
