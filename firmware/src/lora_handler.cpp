#include "lora_handler.h"
#include "crypto.h"
#include "relay.h"
#include <SPI.h>
#include <Wire.h>
#include <string.h>

LoRaHandler loraHandler;

static volatile bool _isrFlag = false;
static void IRAM_ATTR _loraIsr() { _isrFlag = true; }

// ── PI4IOE5V6408 I2C IO 擴充晶片（Unit C6L：控制 SX1262 RESET/RF 開關）──
// 依 m5stack/meshtastic-firmware variant.cpp。位址 0x43，I2C SDA=10 / SCL=8。
//   P7 = SX1262 NRST、P6 = RF 天線開關、P5 = LNA、P0/P1 = 按鈕
#define EXP_ADDR        0x43
#define EXP_I2C_SDA     10
#define EXP_I2C_SCL     8
#define EXP_REG_DIR     0x03  // IO 方向（1=輸出）
#define EXP_REG_OUT     0x05  // 輸出設定
#define EXP_REG_HIZ     0x07  // 高阻抗模式
#define EXP_REG_PULLEN  0x0B  // 上下拉致能
#define EXP_REG_PULLSEL 0x0D  // 上下拉選擇
#define EXP_REG_INDEF   0x09  // 輸入預設
#define EXP_REG_IRQMASK 0x11  // 中斷遮罩
#define EXP_REG_IRQ     0x13  // IRQ 狀態
#define EXP_REG_RESET   0x01  // 晶片重置

static void expWrite(uint8_t reg, uint8_t val) {
    Wire.beginTransmission(EXP_ADDR);
    Wire.write(reg); Wire.write(val);
    Wire.endTransmission();
}

// 初始化擴充晶片並釋放/重置 SX1262（P7）、開 RF 開關（P6）
// 冷開機時延遲加長：擴充晶片 soft reset 50ms、SX1262 reset 脈衝 10ms、
// 釋放後等 50ms，確保晶片完成內部啟動（原時序在冷開機偶爾不足）。
static bool initExpanderAndResetRadio() {
    delay(100); // 冷開機等待 I2C 擴充晶片電源穩定

    Wire.end(); // M5.begin 可能已佔用 Wire，先釋放再以正確腳位重啟
    Wire.begin(EXP_I2C_SDA, EXP_I2C_SCL);
    Wire.beginTransmission(EXP_ADDR);
    if (Wire.endTransmission() != 0) {
        Serial.println("[LoRa] 找不到 IO 擴充晶片 0x43");
        return false;
    }
    expWrite(EXP_REG_RESET,  0xFF);        delay(50);  // 原 10ms，加長確保 soft reset 完成
    expWrite(EXP_REG_DIR,    0b11000000);  // P6,P7 輸出
    expWrite(EXP_REG_HIZ,    0b00111100);
    expWrite(EXP_REG_PULLSEL,0b11000011);
    expWrite(EXP_REG_PULLEN, 0b11000011);
    expWrite(EXP_REG_INDEF,  0b00000011);
    expWrite(EXP_REG_IRQMASK,0b11111100);

    // SX1262 reset 脈衝：P7 低→高；同時 P6(RF 開關) 拉高
    expWrite(EXP_REG_OUT, 0b01000000);     // P7=0 (reset), P6=1
    delay(10);                              // 原 5ms，加長確保 reset 訊號穩定
    expWrite(EXP_REG_OUT, 0b11000000);     // P7=1 (release), P6=1
    delay(50);                              // 原 10ms，加長等待 SX1262 完成內部啟動
    return true;
}

bool LoRaHandler::begin(uint16_t myId) {
    _myId = myId;

    // 先初始化 IO 擴充晶片並釋放 SX1262 reset（否則 radio.begin 會卡在等 BUSY）
    if (!initExpanderAndResetRadio()) {
        _lastErr = -999; // 擴充晶片不存在
        return false;
    }

    // 設定 SPI 腳位（Unit C6L：SCK20 / MISO22 / MOSI21 / NSS23）
    SPI.begin(LORA_SCK, LORA_MISO, LORA_MOSI, LORA_NSS);

    // SX1262 初始化（含重試：冷開機時晶片可能尚未就緒，最多嘗試 3 次）
    int state = RADIOLIB_ERR_UNKNOWN;
    for (int attempt = 0; attempt < 3; attempt++) {
        if (attempt > 0) {
            Serial.printf("[LoRa] 重試第 %d 次...\n", attempt);
            delay(200);
            // 重新 reset SX1262（不重做整個擴充晶片初始化）
            expWrite(EXP_REG_OUT, 0b01000000);  // P7=0 (reset), P6=1
            delay(10);
            expWrite(EXP_REG_OUT, 0b11000000);  // P7=1 (release), P6=1
            delay(50);
        }

        // TCXO 由 DIO3 供電 3.0V（不設會晶振不起、begin 失敗）
        state = _radio.begin(LORA_FREQ, LORA_BW, LORA_SF, LORA_CR,
                             LORA_SYNC_WORD, LORA_TX_POWER,
                             LORA_PREAMBLE_SHORT, LORA_TCXO_V, false);
        _lastErr = state;
        if (state == RADIOLIB_ERR_NONE) break;
        Serial.printf("[LoRa] 初始化失敗（嘗試 %d/3），錯誤碼: %d\n", attempt + 1, state);
    }

    if (state != RADIOLIB_ERR_NONE) {
        Serial.printf("[LoRa] 初始化最終失敗，錯誤碼: %d\n", state);
        return false;
    }

    // RF 收發開關由 SX1262 DIO2 內建控制
    _radio.setDio2AsRfSwitch(true);

    _radio.setDio1Action(_loraIsr);
    _radio.startReceive();
    Serial.printf("[LoRa] 初始化成功，裝置 ID: 0x%04X\n", _myId);
    return true;
}

bool LoRaHandler::sendPacket(LoRaPacket& pkt) {
    // 韌體只覆寫 SRC_ID / HOP / MAC；SEQ 由上層（APP）提供並擁有，
    // 以利 APP 做 ACK 關聯與去重。DST_ID / TYPE / PAYLOAD 亦保留 APP 設定值。
    pkt.srcId = _myId;

    // 依封包類型設定不同的 HOP 初始值
    switch (pkt.type) {
        case PKT_TYPE_SOS:   pkt.hop = MAX_HOP_SOS;   break;
        case PKT_TYPE_VOICE: pkt.hop = MAX_HOP_VOICE;  break;
        default:             pkt.hop = MAX_HOP_TEXT;    break;
    }

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
