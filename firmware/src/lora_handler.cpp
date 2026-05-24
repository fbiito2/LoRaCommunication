#include "lora_handler.h"
#include "crypto.h"
#include "relay.h"
#include <string.h>

LoRaHandler loraHandler;

static volatile bool _isrFlag = false;
static void IRAM_ATTR _loraIsr() { _isrFlag = true; }

bool LoRaHandler::begin(uint16_t myId) {
    _myId = myId;

    // ANT_SW 與 LNA_EN：Unit C6L 需主動 enable
    pinMode(LORA_ANT_SW, OUTPUT);
    pinMode(LORA_LNA_EN, OUTPUT);
    digitalWrite(LORA_ANT_SW, HIGH);
    digitalWrite(LORA_LNA_EN, HIGH);

    // SX1262 硬體重置（規格書：拉低 100ms 再拉高）
    pinMode(LORA_NRST, OUTPUT);
    digitalWrite(LORA_NRST, LOW);
    delay(100);
    digitalWrite(LORA_NRST, HIGH);
    delay(10);

    int state = _radio.begin(LORA_FREQ, LORA_BW, LORA_SF, LORA_CR,
                             LORA_SYNC_WORD, LORA_TX_POWER);
    if (state != RADIOLIB_ERR_NONE) {
        Serial.printf("[LoRa] 初始化失敗，錯誤碼: %d\n", state);
        return false;
    }

    _radio.setDio1Action(_loraIsr);
    _radio.startReceive();
    Serial.printf("[LoRa] 初始化成功，裝置 ID: 0x%04X\n", _myId);
    return true;
}

bool LoRaHandler::sendPacket(LoRaPacket& pkt) {
    pkt.srcId = _myId;
    pkt.seq   = _txSeq++;
    pkt.hop   = MAX_HOP;

    // 組合 nonce 並加密 payload
    uint8_t nonce[16];
    buildNonce(pkt.srcId, pkt.seq, nonce);
    aesCtr(pkt.payload, pkt.payload, pkt.payloadLen, nonce);

    // 計算 MAC（對 header + 加密後 payload 計算）
    uint8_t tmp[PKT_MAX_LEN];
    size_t tmpLen = packetSerialize(pkt, tmp, sizeof(tmp));
    // 排除 MAC 尾端再計算（MAC 位於最後 4 bytes）
    hmacCompute(tmp, tmpLen - PKT_MAC_LEN, pkt.mac);

    // 最終序列化
    uint8_t buf[PKT_MAX_LEN];
    size_t  len = packetSerialize(pkt, buf, sizeof(buf));
    if (len == 0) return false;

    // 待機模式用長前導碼，確保接收端 RxDutyCycle 能偵測到
    _radio.setPreambleLength(LORA_PREAMBLE_LONG);
    int state = _radio.transmit(buf, len);
    _radio.setPreambleLength(LORA_PREAMBLE_SHORT);
    _radio.startReceive();

    if (state != RADIOLIB_ERR_NONE) {
        Serial.printf("[LoRa] 發送失敗，錯誤碼: %d\n", state);
        return false;
    }
    return true;
}

void LoRaHandler::setPacketCallback(LoRaPacketCallback cb) {
    _callback = cb;
}

void LoRaHandler::setDutyCycle(bool enable) {
    _dutyCycleEnabled = enable;
    if (enable) {
        // RxDutyCycle：偵聽 5ms，睡眠 995ms（1 秒週期）
        _radio.startReceiveDutyCycle(5000, 995000);
        Serial.println("[LoRa] 啟用 RxDutyCycle（省電模式）");
    } else {
        _radio.startReceive();
        Serial.println("[LoRa] 切換為連續接收（通話模式）");
    }
}

void LoRaHandler::loop() {
    if (!_isrFlag) return;
    _isrFlag = false;

    size_t len = _radio.getPacketLength();
    if (len < PKT_HEADER_LEN + PKT_MAC_LEN) {
        _radio.startReceive();
        return;
    }

    uint8_t buf[PKT_MAX_LEN];
    if (_radio.readData(buf, len) != RADIOLIB_ERR_NONE) {
        _radio.startReceive();
        return;
    }

    LoRaPacket pkt;
    if (!packetDeserialize(buf, len, pkt)) {
        _radio.startReceive();
        return;
    }

    // HMAC 驗證（對 header + 加密 payload，不含 MAC 尾端）
    if (!hmacVerify(buf, len - PKT_MAC_LEN, pkt.mac)) {
        Serial.println("[LoRa] MAC 驗證失敗，封包丟棄");
        _radio.startReceive();
        return;
    }

    Serial.printf("[LoRa] 收到封包 SRC=0x%04X DST=0x%04X SEQ=%d RSSI=%.1f\n",
                  pkt.srcId, pkt.dstId, pkt.seq, _radio.getRSSI());

    // 交給 Relay 模組判斷：是否轉發，是否給自己
    bool forMe = relayHandler.process(pkt);

    if (forMe && _callback) {
        // 解密 payload 後交給上層
        uint8_t nonce[16];
        buildNonce(pkt.srcId, pkt.seq, nonce);
        aesCtr(pkt.payload, pkt.payload, pkt.payloadLen, nonce);
        _callback(pkt);
    }

    if (_dutyCycleEnabled)
        _radio.startReceiveDutyCycle(5000, 995000);
    else
        _radio.startReceive();
}
