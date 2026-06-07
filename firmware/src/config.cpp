#include "config.h"
#include <Preferences.h>
#include <esp_mac.h>
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

    // 預設 Device ID 由 MAC 的 NIC 專屬後兩碼衍生（每顆晶片唯一；避免用到所有
    // Espressif 共用的 OUI 前綴而撞號）
    uint8_t mac[6] = {0};
    esp_read_mac(mac, ESP_MAC_WIFI_STA);
    uint16_t macId = (uint16_t)((mac[4] << 8) | mac[5]);
    if (macId == 0x0000) macId = 0x0001;
    if (macId >= 0xFFE0) macId &= 0x7FFF; // 避開群組/廣播保留範圍
    cfg.deviceId = prefs.getUShort("deviceId", macId);
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
