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
    // 韌體只覆寫 SRC_ID / HOP / MAC；SEQ 由上層（APP）提供並擁有，
    // 以利 APP 做 ACK 關聯與去重。DST_ID / TYPE / PAYLOAD 亦保留 APP 設定值。
    pkt.srcId = _myId;
    pkt.hop   = MAX_HOP;

    // 加密 payload（Phase 1 為直通不變更）
    payloadEncrypt(pkt.payload, pkt.payloadLen, pkt.srcId, pkt.seq);

    // 計算 MAC（對 header + payload，排除尾端 MAC；內部跳過 HOP 欄位）
    uint8_t tmp[PKT_MAX_LEN];
    size_t tmpLen = packetSerialize(pkt, tmp, sizeof(tmp));
    packetMac(tmp, tmpLen - PKT_MAC_LEN, pkt.mac);

    // 最終序列化
    uint8_t buf[PKT_MAX_LEN];
    size_t  len = packetSerialize(pkt, buf, sizeof(buf));
    if (len == 0) return false;

    return sendRaw(buf, len);
}

bool LoRaHandler::sendRaw(const uint8_t* data, size_t len) {
    // 待機模式用長前導碼，確保接收端 RxDutyCycle 能偵測到
    _radio.setPreambleLength(LORA_PREAMBLE_LONG);
    int state = _radio.transmit(const_cast<uint8_t*>(data), len);
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

    // MAC 驗證（對 header + payload，不含 MAC 尾端；內部跳過 HOP 欄位）
    if (!packetMacVerify(buf, len - PKT_MAC_LEN, pkt.mac)) {
        Serial.println("[LoRa] MAC 驗證失敗，封包丟棄");
        _radio.startReceive();
        return;
    }

    int16_t rssi = (int16_t)lroundf(_radio.getRSSI());
    Serial.printf("[LoRa] 收到封包 SRC=0x%04X DST=0x%04X SEQ=%d RSSI=%d\n",
                  pkt.srcId, pkt.dstId, pkt.seq, rssi);

    // 交給 Relay 模組判斷：是否轉發，是否給自己
    bool forMe = relayHandler.process(pkt);

    if (forMe && _callback) {
        // 解密 payload 後交給上層（Phase 1 為直通不變更）
        payloadDecrypt(pkt.payload, pkt.payloadLen, pkt.srcId, pkt.seq);
        _callback(pkt, rssi);
    }

    if (_dutyCycleEnabled)
        _radio.startReceiveDutyCycle(5000, 995000);
    else
        _radio.startReceive();
}
