#pragma once
#include <Arduino.h>
#include <RadioLib.h>
#include "packet.h"

// ── Unit C6L 接腳定義（硬體到手後以示波器確認）─────────────
#define LORA_NSS    3
#define LORA_SCK   10
#define LORA_MOSI   8
#define LORA_MISO   9   // TODO: 確認實際腳位
#define LORA_DIO1   1   // TODO: 確認實際腳位
#define LORA_NRST   2   // TODO: 確認實際腳位
#define LORA_BUSY   7   // TODO: 確認實際腳位
#define LORA_ANT_SW 4   // TODO: 確認實際腳位
#define LORA_LNA_EN 5   // TODO: 確認實際腳位

// ── LoRa 通訊參數（SF7 + BW500 → 高速，語音用）────────────
#define LORA_FREQ       920.0f  // MHz（台灣 ISM）
#define LORA_BW         500.0f  // kHz
#define LORA_SF         7
#define LORA_CR         5       // 4/5
#define LORA_SYNC_WORD  0x12    // Private network
#define LORA_TX_POWER   22      // dBm
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

private:
    // RadioLib 6.x：先建 Module，再用 Module 建 SX1262
    Module  _mod{LORA_NSS, LORA_DIO1, LORA_NRST, LORA_BUSY};
    SX1262  _radio{&_mod};
    LoRaPacketCallback _callback;
    uint16_t _myId = 0;
    bool     _dutyCycleEnabled = false;
    volatile bool _rxFlag = false;
};

extern LoRaHandler loraHandler;
