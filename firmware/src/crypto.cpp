#include "crypto.h"
#include <string.h>
// mbedtls 為 ESP-IDF 內建，不需額外安裝
#include "mbedtls/aes.h"
#include "mbedtls/md.h"

static uint8_t _aesKey[AES_KEY_LEN];
static uint8_t _hmacKey[HMAC_KEY_LEN];

void cryptoInit(const uint8_t aesKey[AES_KEY_LEN],
                const uint8_t hmacKey[HMAC_KEY_LEN]) {
    memcpy(_aesKey,  aesKey,  AES_KEY_LEN);
    memcpy(_hmacKey, hmacKey, HMAC_KEY_LEN);
}

void buildNonce(uint16_t srcId, uint16_t seq, uint8_t nonce[16]) {
    memset(nonce, 0, 16);
    nonce[0] = (srcId >> 8) & 0xFF;
    nonce[1] =  srcId       & 0xFF;
    nonce[2] = (seq   >> 8) & 0xFF;
    nonce[3] =  seq         & 0xFF;
}

void aesCtr(const uint8_t* in, uint8_t* out, size_t len,
            const uint8_t nonce[16]) {
    mbedtls_aes_context ctx;
    mbedtls_aes_init(&ctx);
    mbedtls_aes_setkey_enc(&ctx, _aesKey, 128);

    uint8_t streamBlock[16] = {0};
    uint8_t ncCopy[16];
    memcpy(ncCopy, nonce, 16);
    size_t ncOff = 0;

    mbedtls_aes_crypt_ctr(&ctx, len, &ncOff, ncCopy, streamBlock,
                          in, out);
    mbedtls_aes_free(&ctx);
}

void hmacCompute(const uint8_t* data, size_t len, uint8_t mac[HMAC_MAC_LEN]) {
    uint8_t fullMac[32];
    mbedtls_md_context_t ctx;
    const mbedtls_md_info_t* info = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);

    mbedtls_md_init(&ctx);
    mbedtls_md_setup(&ctx, info, 1); // 1 = HMAC 模式
    mbedtls_md_hmac_starts(&ctx, _hmacKey, HMAC_KEY_LEN);
    mbedtls_md_hmac_update(&ctx, data, len);
    mbedtls_md_hmac_finish(&ctx, fullMac);
    mbedtls_md_free(&ctx);

    // 只取前 4 bytes
    memcpy(mac, fullMac, HMAC_MAC_LEN);
}

bool hmacVerify(const uint8_t* data, size_t len,
                const uint8_t expectedMac[HMAC_MAC_LEN]) {
    uint8_t computed[HMAC_MAC_LEN];
    hmacCompute(data, len, computed);

    // 時序安全比較（防時序攻擊）
    uint8_t diff = 0;
    for (int i = 0; i < HMAC_MAC_LEN; i++)
        diff |= computed[i] ^ expectedMac[i];
    return diff == 0;
}
