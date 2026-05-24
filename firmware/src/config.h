#pragma once
#include <stdint.h>
#include "crypto.h"

/// @brief NVS 設定管理（斷電不遺失）
struct DeviceConfig {
    uint16_t deviceId;                // 2-byte 裝置 ID
    char     deviceName[32];          // 顯示名稱
    char     wifiSsid[32];            // WiFi AP SSID
    char     wifiPass[32];            // WiFi AP 密碼
    float    loraFreq;                // LoRa 頻率（MHz）
    uint8_t  aesKey[AES_KEY_LEN];     // AES-128 金鑰
    uint8_t  hmacKey[HMAC_KEY_LEN];   // HMAC-SHA256 金鑰
};

/// @brief 從 NVS 載入設定，若無則使用預設值
DeviceConfig configLoad();

/// @brief 將設定寫入 NVS
void configSave(const DeviceConfig& cfg);

/// @brief 重置為出廠預設值
void configReset();
