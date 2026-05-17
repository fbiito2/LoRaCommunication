#pragma once
#include <Arduino.h>
#include <RadioLib.h>

// ── Unit C6L 接腳定義 ──────────────────────────────────────
// 硬體到手後用示波器或官方範例確認實際腳位
#define LORA_NSS  3
#define LORA_SCK  10
#define LORA_MOSI 8
#define LORA_MISO 9   // TODO: 確認實際腳位
#define LORA_DIO1 1   // TODO: 確認實際腳位
#define LORA_NRST 2   // TODO: 確認實際腳位
#define LORA_BUSY 7   // TODO: 確認實際腳位
#define LORA_ANT_SW 4 // TODO: 確認實際腳位
#define LORA_LNA_EN 5 // TODO: 確認實際腳位

// ── LoRa 通訊參數 ──────────────────────────────────────────
#define LORA_FREQ       920.0  // MHz（台灣 ISM）
#define LORA_BW         500.0  // kHz（高速模式）
#define LORA_SF         7      // Spreading Factor（速率優先）
#define LORA_CR         5      // Coding Rate 4/5
#define LORA_SYNC_WORD  0x12   // Private network
#define LORA_TX_POWER   22     // dBm

// BLE Notify 回呼型別（收到 LoRa 封包後呼叫）
using LoRaRxCallback = std::function<void(const uint8_t* data, size_t len)>;

class LoRaHandler {
public:
    bool begin();
    bool send(const uint8_t* data, size_t len);
    void setRxCallback(LoRaRxCallback cb);
    void loop(); // 在 main loop() 中呼叫，處理收發

private:
    void _onReceive(int packetSize);

    SX1262       _radio{LORA_NSS, LORA_DIO1, LORA_NRST, LORA_BUSY};
    LoRaRxCallback _rxCallback;
    volatile bool  _rxFlag = false;
};

extern LoRaHandler loraHandler;
