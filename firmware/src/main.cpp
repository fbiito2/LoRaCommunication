#include <Arduino.h>
#include "ble_service.h"
#include "lora_handler.h"

void setup() {
    Serial.begin(115200);
    delay(500);
    Serial.println("=== LoRa PTT Bridge 啟動 ===");

    // 初始化 BLE GATT Server
    bleService.begin();

    // 手機傳來資料 → 直接 LoRa 發送
    bleService.setRxCallback([](const uint8_t* data, size_t len) {
        Serial.printf("[橋接] BLE → LoRa，%d bytes\n", len);
        loraHandler.send(data, len);
    });

    // 初始化 LoRa（SX1262）
    loraHandler.begin();

    // 收到 LoRa 封包 → BLE Notify 給手機
    loraHandler.setRxCallback([](const uint8_t* data, size_t len) {
        Serial.printf("[橋接] LoRa → BLE，%d bytes\n", len);
        bleService.notify(data, len);
    });

    Serial.println("=== 橋接就緒，等待連線 ===");
}

void loop() {
    // LoRa 中斷處理（必須在 loop 中輪詢）
    loraHandler.loop();

    // 短暫讓出 CPU（避免 watchdog 觸發）
    delay(1);
}
