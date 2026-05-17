#include "lora_handler.h"

LoRaHandler loraHandler;

// RadioLib 中斷旗標（必須是 IRAM_ATTR）
static volatile bool _isrFlag = false;
static void IRAM_ATTR _loraIsr() { _isrFlag = true; }

bool LoRaHandler::begin() {
    // ANT_SW 與 LNA_EN：Unit C6L 需主動 enable（規格書要求）
    pinMode(LORA_ANT_SW, OUTPUT);
    pinMode(LORA_LNA_EN, OUTPUT);
    digitalWrite(LORA_ANT_SW, HIGH);
    digitalWrite(LORA_LNA_EN, HIGH);

    // SX1262 硬體重置（規格：拉低 100ms 再拉高）
    pinMode(LORA_NRST, OUTPUT);
    digitalWrite(LORA_NRST, LOW);
    delay(100);
    digitalWrite(LORA_NRST, HIGH);
    delay(10);

    // RadioLib 初始化
    int state = _radio.begin(
        LORA_FREQ, LORA_BW, LORA_SF, LORA_CR,
        LORA_SYNC_WORD, LORA_TX_POWER
    );
    if (state != RADIOLIB_ERR_NONE) {
        Serial.printf("[LoRa] 初始化失敗，錯誤碼: %d\n", state);
        return false;
    }

    // 設定中斷接收
    _radio.setDio1Action(_loraIsr);
    _radio.startReceive();

    Serial.println("[LoRa] 初始化成功");
    return true;
}

bool LoRaHandler::send(const uint8_t* data, size_t len) {
    int state = _radio.transmit(const_cast<uint8_t*>(data), len);
    if (state != RADIOLIB_ERR_NONE) {
        Serial.printf("[LoRa] 傳送失敗，錯誤碼: %d\n", state);
        _radio.startReceive(); // 回到接收模式
        return false;
    }
    _radio.startReceive();
    return true;
}

void LoRaHandler::setRxCallback(LoRaRxCallback cb) {
    _rxCallback = cb;
}

void LoRaHandler::loop() {
    if (!_isrFlag) return;
    _isrFlag = false;

    size_t len = _radio.getPacketLength();
    if (len == 0) {
        _radio.startReceive();
        return;
    }

    uint8_t buf[256];
    int state = _radio.readData(buf, len);
    if (state == RADIOLIB_ERR_NONE && _rxCallback) {
        Serial.printf("[LoRa] 收到 %d bytes，RSSI: %.1f dBm\n",
                      len, _radio.getRSSI());
        _rxCallback(buf, len);
    }

    _radio.startReceive();
}
