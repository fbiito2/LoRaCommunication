#include "crypto.h"
#include "packet.h"   // PKT_HOP_OFFSET（MAC 計算需跳過的位移）
#include <string.h>

#if ENABLE_ENCRYPTION
// mbedtls 為 ESP-IDF 內建，不需額外安裝
#include "mbedtls/aes.h"
#include "mbedtls/md.h"
static uint8_t _aesKey[AES_KEY_LEN];
static uint8_t _hmacKey[HMAC_KEY_LEN];
#endif

void cryptoInit(const uint8_t* aesKey, const uint8_t* hmacKey) {
#if ENABLE_ENCRYPTION
    if (aesKey)  memcpy(_aesKey,  aesKey,  AES_KEY_LEN);
    if (hmacKey) memcpy(_hmacKey, hmacKey, HMAC_KEY_LEN);
#else
    (void)aesKey; (void)hmacKey; // Phase 1 不使用金鑰
#endif
}

// ── 時序安全比較（防時序攻擊）──────────────────────────────
static bool macEqual(const uint8_t* a, const uint8_t* b) {
    uint8_t diff = 0;
    for (int i = 0; i < MAC_LEN; i++) diff |= a[i] ^ b[i];
    return diff == 0;
}

#if ENABLE_ENCRYPTION
// ══════════════════ Phase 2：AES-128-CTR + HMAC-SHA256 ══════════════════

// 組合 AES-CTR nonce：[srcId 2B][seq 2B][0x00 * 12]
static void buildNonce(uint16_t srcId, uint16_t seq, uint8_t nonce[16]) {
    memset(nonce, 0, 16);
    nonce[0] = (srcId >> 8) & 0xFF;
    nonce[1] =  srcId       & 0xFF;
    nonce[2] = (seq   >> 8) & 0xFF;
    nonce[3] =  seq         & 0xFF;
}

static void aesCtr(uint8_t* buf, size_t len, uint16_t srcId, uint16_t seq) {
    uint8_t nonce[16];
    buildNonce(srcId, seq, nonce);

    mbedtls_aes_context ctx;
    mbedtls_aes_init(&ctx);
    mbedtls_aes_setkey_enc(&ctx, _aesKey, 128);

    uint8_t streamBlock[16] = {0};
    size_t  ncOff = 0;
    mbedtls_aes_crypt_ctr(&ctx, len, &ncOff, nonce, streamBlock, buf, buf);
    mbedtls_aes_free(&ctx);
}

void payloadEncrypt(uint8_t* payload, size_t len, uint16_t srcId, uint16_t seq) {
    aesCtr(payload, len, srcId, seq);
}
void payloadDecrypt(uint8_t* payload, size_t len, uint16_t srcId, uint16_t seq) {
    aesCtr(payload, len, srcId, seq); // CTR 模式加解密同一運算
}

void packetMac(const uint8_t* buf, size_t len, uint8_t mac[MAC_LEN]) {
    uint8_t fullMac[32];
    mbedtls_md_context_t ctx;
    const mbedtls_md_info_t* info = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);

    mbedtls_md_init(&ctx);
    mbedtls_md_setup(&ctx, info, 1); // 1 = HMAC 模式
    mbedtls_md_hmac_starts(&ctx, _hmacKey, HMAC_KEY_LEN);
    // 跳過 HOP 欄位（位於 PKT_HOP_OFFSET），分兩段餵入
    mbedtls_md_hmac_update(&ctx, buf, PKT_HOP_OFFSET);
    mbedtls_md_hmac_update(&ctx, buf + PKT_HOP_OFFSET + 1,
                           len - PKT_HOP_OFFSET - 1);
    mbedtls_md_hmac_finish(&ctx, fullMac);
    mbedtls_md_free(&ctx);

    memcpy(mac, fullMac, MAC_LEN); // 截斷取前 4 bytes
}

#else
// ══════════════════ Phase 1：不加密 + CRC32 ══════════════════

// payload 直通，不做任何變更
void payloadEncrypt(uint8_t*, size_t, uint16_t, uint16_t) {}
void payloadDecrypt(uint8_t*, size_t, uint16_t, uint16_t) {}

// CRC32（IEEE 802.3，反射式）— 可分段累加
static uint32_t crc32Step(uint32_t crc, const uint8_t* data, size_t len) {
    for (size_t i = 0; i < len; i++) {
        crc ^= data[i];
        for (int b = 0; b < 8; b++)
            crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
    }
    return crc;
}

void packetMac(const uint8_t* buf, size_t len, uint8_t mac[MAC_LEN]) {
    uint32_t crc = 0xFFFFFFFFu;
    // 跳過 HOP 欄位（位於 PKT_HOP_OFFSET），分兩段累加
    crc = crc32Step(crc, buf, PKT_HOP_OFFSET);
    crc = crc32Step(crc, buf + PKT_HOP_OFFSET + 1, len - PKT_HOP_OFFSET - 1);
    crc ^= 0xFFFFFFFFu;

    mac[0] = (crc >> 24) & 0xFF;
    mac[1] = (crc >> 16) & 0xFF;
    mac[2] = (crc >>  8) & 0xFF;
    mac[3] =  crc        & 0xFF;
}

#endif

bool packetMacVerify(const uint8_t* buf, size_t len, const uint8_t mac[MAC_LEN]) {
    uint8_t computed[MAC_LEN];
    packetMac(buf, len, computed);
    return macEqual(computed, mac);
}
