#pragma once
#include <Arduino.h>
#include <RadioLib.h>
#include "packet.h"

// ── Unit C6L 接腳定義（依 m5stack/meshtastic-firmware variant 實機確認）──
#define LORA_SCK   20
#define LORA_MISO  22
#define LORA_MOSI  21
#define LORA_NSS   23
#define LORA_DIO1   7
#define LORA_BUSY  19
#define LORA_NRST  RADIOLIB_NC   // RESET 未接出，由 RadioLib 軟體處理
// 注意：RF 開關由 SX1262 DIO2 內建控制（setDio2AsRfSwitch），
//       TCXO 由 DIO3 供電 3.0V；不再使用外部 ANT_SW / LNA GPIO。

// ── LoRa 通訊參數（SF7 + BW500 → 高速，語音用）────────────
#define LORA_FREQ       920.0f  // MHz（台灣 ISM）
#define LORA_BW         500.0f  // kHz
#define LORA_SF         7
#define LORA_CR         5       // 4/5
#define LORA_SYNC_WORD  0x12    // Private network
#define LORA_TX_POWER   22      // dBm
#define LORA_TCXO_V     3.0f    // DIO3 TCXO 供電電壓
// 待機模式長前導碼（覆蓋 RxDutyCycle 1 秒睡眠週期）
#define LORA_PREAMBLE_LONG  16
#define LORA_PREAMBLE_SHORT  8

/// 收到完整解密封包後的回呼（payload 已解密，可直接使用）
/// @param rssi 該封包接收信號強度（dBm，整數）
using LoRaPacketCallback = std::function<void(const LoRaPacket& pkt, int16_t rssi)>;

class LoRaHandler {
public:
    bool begin(uint16_t myId);
    bool sendPacket(LoRaPacket& pkt);              // 設定 src/seq/hop + 加密 + MAC + 發送
    bool sendRaw(const uint8_t* data, size_t len); // 原樣發送（中繼轉發用，不改封包內容）
    void setPacketCallback(LoRaPacketCallback cb);
    void setDutyCycle(bool enable);   // 電源管理切換
    void loop();
    int  lastError() const { return _lastErr; } // 最後一次 radio.begin 錯誤碼（實機除錯）

private:
    // RadioLib 6.x：先建 Module，再用 Module 建 SX1262
    Module  _mod{LORA_NSS, LORA_DIO1, LORA_NRST, LORA_BUSY};
    SX1262  _radio{&_mod};
    LoRaPacketCallback _callback;
    uint16_t _myId = 0;
    int      _lastErr = 0;
    bool     _dutyCycleEnabled = false;
    volatile bool _rxFlag = false;
};

extern LoRaHandler loraHandler;
