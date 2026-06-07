#include "config.h"
#include <Preferences.h>
#include <string.h>

static const char* NVS_NS = "loraptt"; // NVS 命名空間

// 出廠預設金鑰（實際部署請換成自己的金鑰）
static const uint8_t DEFAULT_AES_KEY[AES_KEY_LEN]  =
    { 0x4C,0x6F,0x52,0x61,0x50,0x54,0x54,0x32,
      0x30,0x32,0x36,0x4B,0x45,0x59,0x21,0x21 };
static const uint8_t DEFAULT_HMAC_KEY[HMAC_KEY_LEN] =
    { 0x48,0x4D,0x41,0x43,0x4C,0x6F,0x52,0x61,
      0x50,0x54,0x54,0x4B,0x65,0x79,0x32,0x30,
      0x32,0x36,0x21,0x21,0x48,0x4D,0x41,0x43,
      0x4C,0x6F,0x52,0x61,0x31,0x32,0x33,0x34 };

DeviceConfig configLoad() {
    Preferences prefs;
    prefs.begin(NVS_NS, true); // 唯讀

    DeviceConfig cfg{}; // 值初始化為全零，避免 NVS 無 key 時讀到未初始化的垃圾值（如 0xA5）
    cfg.deviceId = prefs.getUShort("deviceId", 0xA001);
    prefs.getString("name",      cfg.deviceName, sizeof(cfg.deviceName));
    prefs.getString("wifiSsid",  cfg.wifiSsid,   sizeof(cfg.wifiSsid));
    prefs.getString("wifiPass",  cfg.wifiPass,   sizeof(cfg.wifiPass));
    cfg.loraFreq = prefs.getFloat("loraFreq", 920.0f);

    if (strlen(cfg.deviceName) == 0) strncpy(cfg.deviceName, "LoRaPTT",  31);
    if (strlen(cfg.wifiSsid)   == 0) strncpy(cfg.wifiSsid,   "LoRaPTT",  31);
    if (strlen(cfg.wifiPass)   == 0) strncpy(cfg.wifiPass,   "loraptt2026", 31);

    // 載入金鑰，若未設定則使用預設值
    size_t aesLen = prefs.getBytes("aesKey",  cfg.aesKey,  AES_KEY_LEN);
    if (aesLen != AES_KEY_LEN) memcpy(cfg.aesKey, DEFAULT_AES_KEY, AES_KEY_LEN);

    size_t hmacLen = prefs.getBytes("hmacKey", cfg.hmacKey, HMAC_KEY_LEN);
    if (hmacLen != HMAC_KEY_LEN) memcpy(cfg.hmacKey, DEFAULT_HMAC_KEY, HMAC_KEY_LEN);

    prefs.end();
    return cfg;
}

void configSave(const DeviceConfig& cfg) {
    Preferences prefs;
    prefs.begin(NVS_NS, false); // 讀寫
    prefs.putUShort("deviceId", cfg.deviceId);
    prefs.putString("name",     cfg.deviceName);
    prefs.putString("wifiSsid", cfg.wifiSsid);
    prefs.putString("wifiPass", cfg.wifiPass);
    prefs.putFloat ("loraFreq", cfg.loraFreq);
    prefs.putBytes ("aesKey",   cfg.aesKey,  AES_KEY_LEN);
    prefs.putBytes ("hmacKey",  cfg.hmacKey, HMAC_KEY_LEN);
    prefs.end();
    Serial.println("[Config] 設定已儲存到 NVS");
}

void configReset() {
    Preferences prefs;
    prefs.begin(NVS_NS, false);
    prefs.clear();
    prefs.end();
    Serial.println("[Config] 已重置為出廠預設值");
}
