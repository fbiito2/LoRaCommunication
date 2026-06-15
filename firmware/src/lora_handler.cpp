#include "lora_handler.h"
#include "crypto.h"
#include "relay.h"
#include "debug_log.h"
#include <SPI.h>
#include <M5Unified.h>
#include <string.h>

LoRaHandler loraHandler;

static volatile bool _isrFlag = false;
static void IRAM_ATTR _loraIsr() { _isrFlag = true; }

// ── 透過 M5Unified IO 擴充晶片 API 控制 SX1262 ──────────────────
// 不使用 Arduino Wire（Wire.end/begin 會破壞 M5Unified 的 In_I2C，導致按鈕失效）。
// 做完整 soft reset + 暫存器重配，確保冷開機時擴充晶片處於正確狀態。
//   P7 = SX1262 NRST、P6 = RF 天線開關、P5 = LNA、P0/P1 = 按鈕
static bool initExpanderAndResetRadio() {
    delay(100); // 冷開機等待 I2C 擴充晶片電源穩定

    // 檢查 M5Unified 是否正確偵測為 UnitC6L（否則 _io_expander[0] 不存在會崩潰）
    if (M5.getBoard() != m5::board_t::board_M5UnitC6L) {
        Serial.println("[LoRa] 板型非 UnitC6L，無法存取 IO 擴充晶片");
        return false;
    }

    auto& ioexp = M5.getIOExpander(0);

    // 驗證擴充晶片可存取（讀取裝置 ID 暫存器，回傳非 0 表示晶片存在）
    uint8_t devId = ioexp.readRegister8(0x01);
    if (devId == 0) {
        Serial.println("[LoRa] IO 擴充晶片 0x43 無回應");
        return false;
    }

    // Soft reset 擴充晶片（確保乾淨狀態，冷開機 Power_Class 可能寫入時晶片尚未就緒）
    ioexp.writeRegister8(0x01, 0xFF);
    delay(50);

    // 重新配置暫存器（soft reset 後全歸預設值）
    // 與 M5Unified Power_Class reg_data_array_for_lorac6 一致
    ioexp.writeRegister8(0x03, 0b11100000);  // DIR: P5,P6,P7 output
    ioexp.writeRegister8(0x07, 0b00011100);  // HIZ: P2,P3,P4 high-impedance
    ioexp.writeRegister8(0x0D, 0b11000011);  // PULLSEL: P0,P1,P6,P7 pull-up
    ioexp.writeRegister8(0x0B, 0b11000011);  // PULLEN: P0,P1,P6,P7 pull enabled
    ioexp.writeRegister8(0x09, 0b00000011);  // INDEF: P0,P1 default state
    ioexp.writeRegister8(0x11, 0b11111100);  // IRQMASK: only P0,P1 interrupt

    // SX1262 reset 脈衝：DIR 設完時 OUT 預設 0x00 → P7 已為 0（reset）
    ioexp.writeRegister8(0x05, 0b01000000);  // P6=1 RF 開關, P7=0 維持 reset
    delay(10);
    ioexp.writeRegister8(0x05, 0b11000000);  // P6=1, P7=1 釋放 SX1262
    delay(50);
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
            // 重新 reset SX1262（透過 M5Unified IO 擴充晶片 API）
            auto& ioexp = M5.getIOExpander(0);
            ioexp.writeRegister8(0x05, 0b01000000);  delay(10);  // P7=0 reset
            ioexp.writeRegister8(0x05, 0b11000000);  delay(50);  // P7=1 release
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
        case PKT_TYPE_POS:   pkt.hop = MAX_HOP_POS;    break;
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

void LoRaHandler::setMode(float bw, uint8_t sf) {
    // 執行期切換頻寬/展頻因子（F-052 語音 PTT）。SX1262 支援動態變更，
    // 切完重啟接收。全網各節點靠 PTT_START/END 控制封包同步切換。
    _radio.setBandwidth(bw);
    _radio.setSpreadingFactor(sf);
    _radio.startReceive();
    Serial.printf("[LoRa] 切換模式 SF%d BW%.0f\n", sf, bw);
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
    if (!g_usbDataMode)
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
