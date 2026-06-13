#include <Arduino.h>
#include <ArduinoJson.h>
#include <esp_ota_ops.h>  // F-063：OTA rollback 支援
#include <esp_random.h>   // 硬體亂數（PING 回覆隨機退避用）
#include "config.h"
#include "crypto.h"
#include "comm_interface.h"
#include "usb_serial_service.h"
#include "wifi_service.h"
#include "lora_handler.h"
#include "relay.h"
#include "power_mgr.h"
#include "display.h"
#include "button.h"
#include "led.h"
#include "ota_service.h"
#include "debug_log.h"
#include "gps.h"

// USB CDC 作為資料通道時抑制除錯 log（見 debug_log.h）
volatile bool g_usbDataMode = false;

// ── 韌體版本（F-064 版本查詢）──────────────────────────────
#define FW_VERSION "0.5.4"

// ── LoRa 啟用旗標 ─────────────────────────────────────────
// 暫時關閉：SX1262 初始化（radio.begin）在 Unit C6L 上會卡死主迴圈，
// 原因為 SX1262 RESET 經 I2C 擴充晶片、復位序列尚未完全正確（WIP）。
// 關閉時裝置仍為完整可用的 WiFi/USB 橋接；LoRa 硬體初始化修好後改回 1。
#define ENABLE_LORA 1

// ── 測試用：每 3 秒自動廣播一個 beacon（雙機 LoRa 收發驗證；測完設 0）──
// 實機已驗證雙機收發成功（CDB8 收到 BF8C，RSSI -27），平時關閉。
#define LORA_TEST_BEACON 0

// ── 線路幀類型（手機 ↔ C6L，USB/WiFi 共用）─────────────────
// 傳輸層各自負責邊界（USB 2-byte 長度前綴 / WiFi 一個 datagram）。
//   LINK_DATA：phone→C6L = [01][LoRa封包]；C6L→phone = [01][RSSI int16 BE][LoRa封包]
//   LINK_CTRL：控制/設定 JSON（雙向）
#define LINK_DATA 0x01
#define LINK_CTRL 0x02

// ── 傳輸層登錄（雙傳輸層多工：USB + WiFi 可同時運作）────────
struct Transport {
    ICommInterface* comm;
    bool            hasApp; // 是否已收到 APP 握手（F-053）
};
static Transport _transports[2];
static int       _transportCount = 0;
static DeviceConfig _cfg;
static bool      _loraOk = false; // LoRa 初始化結果（心跳輸出用）

static bool anyAppConnected() {
    for (int i = 0; i < _transportCount; i++)
        if (_transports[i].hasApp) return true;
    return false;
}

// 送出 CTRL（JSON）給指定傳輸層
static void sendCtrl(int idx, const char* json, size_t jsonLen) {
    if (idx < 0 || idx >= _transportCount) return;
    uint8_t buf[256];
    if (jsonLen + 1 > sizeof(buf)) return;
    buf[0] = LINK_CTRL;
    memcpy(buf + 1, json, jsonLen);
    _transports[idx].comm->send(buf, jsonLen + 1);
}

// 推送 DATA 幀給「所有已握手」的傳輸層
static void broadcastToApps(const uint8_t* frame, size_t len) {
    for (int i = 0; i < _transportCount; i++)
        if (_transports[i].hasApp)
            _transports[i].comm->send(frame, len);
}

// ── SOS 發送（F-071：C6L 實體按鈕觸發，不需手機）─────────────
static uint16_t _sosSeq = 0xF000; // SOS 專用 SEQ 區段，避免與 APP 的 SEQ 碰撞
static void sendSos() {
    Serial.println("[SOS] 緊急求救發送中！");
    Display::showSosSent();
    M5.Speaker.tone(1000, 1500); // 長嗶確認

    for (int i = 0; i < 3; i++) { // 重複發送 3 次（F-070）
        LoRaPacket pkt = {};
        pkt.dstId      = DST_BROADCAST;
        pkt.type       = PKT_TYPE_SOS;
        pkt.seq        = _sosSeq++;
        // Payload：簡易格式 [DeviceID 2B]（無手機 → 無 GPS）
        pkt.payload[0] = (_cfg.deviceId >> 8) & 0xFF;
        pkt.payload[1] = _cfg.deviceId & 0xFF;
        pkt.payloadLen = 2;
        Led::setLoRaTx();
        loraHandler.sendPacket(pkt);
        if (i < 2) delay(2000); // 間隔 2 秒
    }
    Led::setCarryMode();
}

// ── PING 回覆（F-004：廣播探測發現裝置）─────────────────────
static void replyPing(const LoRaPacket& pingPkt) {
    // 隨機退避 30~150ms：避免回覆與「轉發回音」背靠背碰撞——接收端抓到回音後
    // 正忙著處理/透過 WiFi 推給 APP（短暫離開接收），緊接而來的回覆會被漏掉。
    // 退避讓接收端先處理完回音、回到接收狀態，再收回覆；多節點時也錯開彼此回覆。
    // 用硬體亂數 esp_random()，確保每台/每次退避都不同（一般 random() 無種子各台
    // 會挑到相同延遲、照樣互撞）。
    delay(30 + (esp_random() % 120));

    LoRaPacket reply = {};
    reply.dstId      = pingPkt.srcId; // 回覆給發送 PING 的人
    reply.type       = PKT_TYPE_PING;
    reply.seq        = (uint16_t)(millis() & 0xFFFF);

    // Payload：[DeviceID 2B][暱稱 UTF-8 NB]
    reply.payload[0] = (_cfg.deviceId >> 8) & 0xFF;
    reply.payload[1] = _cfg.deviceId & 0xFF;
    size_t nameLen = strlen(_cfg.deviceName);
    if (nameLen > PKT_MAX_PAYLOAD - 2) nameLen = PKT_MAX_PAYLOAD - 2;
    memcpy(reply.payload + 2, _cfg.deviceName, nameLen);
    reply.payloadLen = 2 + nameLen;

    loraHandler.sendPacket(reply);
    Serial.printf("[PING] 回覆給 0x%04X\n", pingPkt.srcId);
}

// ── 收到 LoRa 封包（給自己/廣播/群組，已解密）→ 推給手機 ────
static void onLoRaReceived(const LoRaPacket& pkt, int16_t rssi) {
    PowerMgr::onActivity();
    Led::setLoRaRx();
    Display::lastRssi = rssi;
    Display::rxCount++;
    Ota::setRxStats(Display::rxCount, rssi, pkt.srcId); // 供 /version 觀察雙機收訊

    // F-073：收到 SOS → 全節點警報（不管有沒有接手機）
    if (pkt.type == PKT_TYPE_SOS) {
        if (!g_usbDataMode) Serial.printf("[SOS] 收到求救！來自 0x%04X\n", pkt.srcId);
        M5.Speaker.tone(800, 3000); // Buzzer 連續長響 3 秒
        Led::setSosAlert();          // RGB LED 紅色快閃
        Display::showSosReceived(pkt.srcId);
    }

    // F-004：收到廣播 PING → 回覆自己的 ID + 暱稱（韌體自動回覆，不需 APP）
    if (pkt.type == PKT_TYPE_PING && pkt.dstId == DST_BROADCAST) {
        replyPing(pkt);
    }

    // F-043：收到文字訊息短響一聲（語音幀過於頻繁，不蜂鳴）
    if (pkt.type == PKT_TYPE_TEXT) M5.Speaker.tone(2000, 80);

    // C6L→phone DATA：[01][RSSI hi][RSSI lo][LoRa封包]
    uint8_t buf[3 + PKT_MAX_LEN];
    buf[0] = LINK_DATA;
    buf[1] = (uint8_t)((rssi >> 8) & 0xFF);
    buf[2] = (uint8_t)(rssi & 0xFF);
    size_t plen = packetSerialize(pkt, buf + 3, sizeof(buf) - 3);
    if (plen > 0) broadcastToApps(buf, 3 + plen);

    Led::setCarryMode();
}

// ── CTRL（JSON 控制/設定）處理 ──────────────────────────────
static void handleCtrl(int idx, const char* json, size_t len) {
    JsonDocument doc;
    if (deserializeJson(doc, json, len) != DeserializationError::Ok) return;
    const char* cmd = doc["cmd"];
    if (!cmd) return;

    if (strcmp(cmd, "hello") == 0) {
        // F-053 握手：標記此傳輸層「有 APP」，回 hello-ack（含 device_id / fw_ver）
        _transports[idx].hasApp = true;
        if (doc["name"].is<const char*>())
            Serial.printf("[Main] APP 握手於傳輸層 %d，暱稱: %s\n",
                          idx, doc["name"].as<const char*>());
        // 握手成功的 LED 提示：USB=綠、WiFi=藍
        if (idx == 0) Led::setCarryMode(); else Led::setBaseMode();

        JsonDocument ack;
        ack["status"]    = "hello";
        ack["device_id"] = _cfg.deviceId;
        ack["name"]      = _cfg.deviceName;
        ack["fw_ver"]    = FW_VERSION;
        ack["lora_ok"]   = _loraOk; // 實機驗證：LoRa 初始化是否成功
        ack["lora_err"]  = loraHandler.lastError(); // radio.begin 錯誤碼
        char out[160];
        size_t n = serializeJson(ack, out, sizeof(out));
        sendCtrl(idx, out, n);
        return;
    }

    if (strcmp(cmd, "set_config") == 0) {
        // F-051 設定指令
        if (doc["wifi_ssid"].is<const char*>())
            strncpy(_cfg.wifiSsid, doc["wifi_ssid"], 31);
        if (doc["wifi_pass"].is<const char*>())
            strncpy(_cfg.wifiPass, doc["wifi_pass"], 31);
        if (doc["device_name"].is<const char*>())
            strncpy(_cfg.deviceName, doc["device_name"], 31);
        if (doc["lora_freq"].is<float>())
            _cfg.loraFreq = doc["lora_freq"];
        configSave(_cfg);
        const char* resp = "{\"status\":\"ok\"}";
        sendCtrl(idx, resp, strlen(resp));
        return;
    }
}

// ── 收到手機線路幀（USB 或 WiFi）──────────────────────────
// DATA：手機送來完整 LoRa 封包（DST/TYPE/SEQ/PAYLOAD），韌體覆寫 SRC/HOP/MAC 後發送。
static void onLinkReceived(int idx, const uint8_t* data, size_t len) {
    if (len < 1) return;
    uint8_t type = data[0];

    if (type == LINK_DATA) {
        PowerMgr::onActivity();
        LoRaPacket pkt;
        if (!packetDeserialize(data + 1, len - 1, pkt)) {
            Serial.println("[Main] 手機封包解析失敗，丟棄");
            return;
        }
        Led::setLoRaTx();
        bool txOk = loraHandler.sendPacket(pkt);
        Display::txCount++;
        Ota::setTxStats(Display::txCount, txOk); // 供 /version 確認是否真的發射
        Led::setCarryMode();
    } else if (type == LINK_CTRL) {
        handleCtrl(idx, (const char*)(data + 1), len - 1);
    }
    // 其他類型（含 WiFiCommService 的註冊用空封包）忽略
}

// ══ POST 自我測試 ══════════════════════════════════════════
// 開機逐項檢測硬體，OLED 顯示進度 + 蜂鳴器回饋。
// 任一步驟失敗 → 紅燈 + 錯誤畫面 → 等使用者按鈕重試（不需拔電）。
static void runPost() {
    const int TOTAL = 3;

    while (true) { // POST 重試迴圈（按按鈕可重跑）
        bool allPass = true;

        // ── Step 1/3: I2C 擴充晶片（PI4IOE5V6408, 0x43）──────
        Display::showPostStep(1, TOTAL, "I2C");
        delay(500); // 讓使用者看到畫面

        {
            // 透過 M5Unified IO 擴充晶片 API 驗證（不使用 Wire，避免破壞 In_I2C 導致按鈕失效）
            bool i2cOk = false;
            if (M5.getBoard() != m5::board_t::board_M5UnitC6L) {
                Serial.println("[POST] FAIL: 板型非 UnitC6L（IO 擴充晶片不可用）");
            } else {
                auto& ioexp = M5.getIOExpander(0);
                uint8_t devId = ioexp.readRegister8(0x01);
                i2cOk = (devId != 0);
            }

            if (!i2cOk) {
                Serial.println("[POST] FAIL: I2C 擴充晶片 0x43 無回應");
                Display::showPostFail("I2C 0x43", -1);
                M5.Speaker.tone(400, 800);
                Led::setError();
                allPass = false;
            } else {
                Serial.println("[POST] PASS: I2C 0x43");
                Display::showPostPass(1, TOTAL, "I2C");
                M5.Speaker.tone(2000, 80);
                Ota::setI2cScan("43");
                delay(600);
            }
        }

        // ── Step 2/3: SX1262 LoRa ────────────────────────────
        if (allPass) {
            Display::showPostStep(2, TOTAL, "LoRa");
            delay(300);

#if ENABLE_LORA
            Ota::setLoraStatus(false, -1000);
            _loraOk = loraHandler.begin(_cfg.deviceId);
            Ota::setLoraStatus(_loraOk, loraHandler.lastError());

            if (!_loraOk) {
                Serial.printf("[POST] FAIL: LoRa err=%d\n", loraHandler.lastError());
                Display::showPostFail("LoRa", loraHandler.lastError());
                M5.Speaker.tone(400, 500);
                delay(600);
                M5.Speaker.tone(400, 500);
                Led::setError();
                allPass = false;
            } else {
                Serial.println("[POST] PASS: LoRa SX1262");
                Display::showPostPass(2, TOTAL, "LoRa");
                M5.Speaker.tone(2000, 80);
                delay(600);
            }
#else
            Serial.println("[POST] SKIP: LoRa 停用（ENABLE_LORA=0）");
            Display::showPostPass(2, TOTAL, "LoRa OFF");
            M5.Speaker.tone(2000, 80);
            delay(600);
#endif
        }

        // ── Step 3/3: WiFi AP ────────────────────────────────
        if (allPass) {
            Display::showPostStep(3, TOTAL, "WiFi AP");
            delay(300);

            wifiService.begin();
            _transports[1] = { &wifiService, false };
            delay(200); // 等待 AP 啟動

            bool wifiOk = (WiFi.softAPIP() != IPAddress(0, 0, 0, 0));
            if (!wifiOk) {
                Serial.println("[POST] FAIL: WiFi AP 啟動失敗");
                Display::showPostFail("WiFi AP", -1);
                M5.Speaker.tone(400, 500);
                delay(600);
                M5.Speaker.tone(400, 500);
                Led::setError();
                allPass = false;
            } else {
                Serial.println("[POST] PASS: WiFi AP");
                Display::showPostPass(3, TOTAL, "WiFi AP");
                M5.Speaker.tone(2000, 80);
                delay(600);
            }
        }

        // ── 結果判定 ──────────────────────────────────────────
        if (allPass) {
            Display::showPostDone(_cfg.deviceId, FW_VERSION);
            // 開機完成音：嗶嗶兩短聲
            M5.Speaker.tone(2000, 60);
            delay(120);
            M5.Speaker.tone(2000, 60);
            delay(1500); // 顯示結果畫面 1.5 秒
            Serial.println("[POST] === 全部通過 ===");
            return; // POST 通過，繼續 setup 後段
        }

        // ── 失敗：等使用者按按鈕重試 ────────────────────────
        Serial.println("[POST] 按使用者按鈕重試...");
        while (true) {
            M5.update();
            if (M5.BtnA.wasPressed()) {
                M5.Speaker.tone(1000, 100);
                delay(300); // 消抖
                break;      // 跳出等待，回到 POST 重試迴圈
            }
            delay(10);
        }
        Serial.println("[POST] 使用者按下按鈕，重新測試...");
    } // while(true) POST retry
}

void setup() {
    // Serial 先起，並加長延遲等待 USB CDC 列舉 + host 連線，確保檢查點不被丟棄
    Serial.begin(115200);
    // HWCDC：無 host 讀取時直接丟棄輸出，不阻塞主迴圈。
    // 否則 USB 插著供電但沒程式讀序列埠時，TX 緩衝區塞滿會卡死 loop，
    // 導致按鈕漏抓、OLED 不重繪、WiFi/HTTP 反應遲緩。
    Serial.setTxTimeoutMs(0);
    delay(2000);
    // M5Unified 初始化（電源/I2C/OLED/按鈕/蜂鳴器）——須在使用 M5.* 前呼叫
    auto m5cfg = M5.config();
    M5.begin(m5cfg);

    // OLED 初始化提前（供 POST 顯示用）
    Display::init();

    Serial.println("=== LoRa PTT Bridge 啟動 ===");

    _cfg = configLoad();
    Serial.printf("[Main] 裝置 ID: 0x%04X，名稱: %s，韌體: %s\n",
                  _cfg.deviceId, _cfg.deviceName, FW_VERSION);

    cryptoInit(_cfg.aesKey, _cfg.hmacKey);

    // USB Serial CDC 先啟動（不需測試，不會失敗）
    usbSerialService.begin();
    _transports[0] = { &usbSerialService, false };

    // GPS（Grove PORT.A UART，選配；沒接也無害，只是讀不到資料）
    Gps::begin();

    // WiFi 參數設定（實際啟動在 POST Step 3）
    if (strcmp(_cfg.wifiSsid, "LoRaPTT") == 0) {
        static char ssidBuf[32];
        snprintf(ssidBuf, sizeof(ssidBuf), "LoRaPTT_%04X", _cfg.deviceId);
        wifiService.setSsid(ssidBuf);
    } else {
        wifiService.setSsid(_cfg.wifiSsid);
    }
    wifiService.setPassword(_cfg.wifiPass);

    // ══ POST 自我測試（I2C → LoRa → WiFi，失敗停住等按鈕重試）══
    runPost();

    // ── POST 通過，繼續初始化其餘模組 ─────────────────────────
    _transportCount = 2;

    // 韌體 OTA HTTP 伺服器（F-060~064），開在 WiFi AP 上
    Ota::begin(FW_VERSION);

    // 兩傳輸層各自掛 onReceive，帶入索引
    usbSerialService.onReceive([](const uint8_t* d, size_t l) { onLinkReceived(0, d, l); });
    wifiService.onReceive(   [](const uint8_t* d, size_t l) { onLinkReceived(1, d, l); });

    // Display 裝置與網路資訊（POST 完成後更新為正常頁面資料）
    Display::deviceId   = _cfg.deviceId;
    Display::deviceName = _cfg.deviceName;
    Display::wifiSsid   = WiFi.softAPSSID();
    Display::wifiIp     = WiFi.softAPIP().toString();

    // LoRa 回呼（begin 在 POST 中已完成）
#if ENABLE_LORA
    loraHandler.setPacketCallback(onLoRaReceived);
#else
    Serial.println("[Main] LoRa 暫時停用（ENABLE_LORA=0）");
#endif

    // Relay：轉發用 sendRaw 原樣送出，保留原始 SRC/SEQ/MAC（去重依據）
    relayHandler.init(_cfg.deviceId, [](const uint8_t* d, size_t l) {
        return loraHandler.sendRaw(d, l);
    });

    PowerMgr::init([](bool en) { loraHandler.setDutyCycle(en); });

    // HMI
    Led::setBaseWaiting(); // WiFi AP 已開、尚無 APP 握手 → 藍色閃爍
    Button::init();
    Button::onShortPress([]() { Display::nextPage(); });
    Button::onLongPress([]() {
        // F-041：長按 3 秒 → 開/關 WiFi AP（省電）
        bool en = !wifiService.isApEnabled();
        wifiService.setApEnabled(en);
        if (en) { wifiService.onReceive([](const uint8_t* d, size_t l) { onLinkReceived(1, d, l); });
                  Display::wifiSsid = WiFi.softAPSSID();
                  Display::wifiIp   = WiFi.softAPIP().toString(); }
        else    { _transports[1].hasApp = false; }
        M5.Speaker.tone(en ? 1500 : 800, 200);
    });
    Button::onVeryLongPress([]() {
        Serial.println("[Main] 長按 6 秒：重置設定");
        configReset();
        M5.Speaker.tone(500, 500);
    });
    Button::onTriplePress([]() {
        // F-071：連按 3 下 → SOS 緊急求救（不需手機也能發送）
        sendSos();
    });

    // F-063：OTA 回滾驗證 ─────────────────────────────────────
    // 若此韌體是 OTA 更新後首次啟動（pending verify），
    // 標記為有效；否則下次重開機會自動回滾到前一版本。
    const esp_partition_t* running = esp_ota_get_running_partition();
    esp_ota_img_states_t ota_state;
    if (esp_ota_get_state_partition(running, &ota_state) == ESP_OK) {
        if (ota_state == ESP_OTA_IMG_PENDING_VERIFY) {
            Serial.println("[OTA] 新韌體首次開機，驗證通過 → 標記有效");
            esp_ota_mark_app_valid_cancel_rollback();
        }
    }

    Serial.println("=== 橋接就緒（USB + WiFi 雙傳輸層）===");
}

void loop() {
    usbSerialService.loop();
    wifiService.loop();
    Ota::loop(); // 處理 HTTP OTA 更新請求

    // GPS：讀取/解析 NMEA，並回報給 /version（實機定位驗證）
    Gps::loop();
    Ota::setGps(Gps::hasFix(), Gps::sats(), Gps::lat(), Gps::lon(),
                Gps::rxBytes(), Gps::baud(), Gps::rxPin());

    // 更新 OLED 統計與 APP 連線狀態
    bool u = _transports[0].hasApp, w = _transports[1].hasApp;
    g_usbDataMode = u; // USB 有 APP 握手 → CDC 為資料通道 → 抑制除錯 log
    Display::appStatus  = (u && w) ? "USB+WiFi" : u ? "USB" : w ? "WiFi" : "none";
    Display::relayCount = relayHandler.forwardCount();

#if ENABLE_LORA
    loraHandler.loop();
#endif
    PowerMgr::loop();
    Display::loop();
    Button::loop();
    Led::loop();

    // 按鈕偵錯：每次 loop 更新 IO 擴充晶片原始值 + 邊緣事件計數供 /version HTTP 查看
    {
        static uint32_t _dbgCnt = 0;
        _dbgCnt++;
        if (M5.getBoard() == m5::board_t::board_M5UnitC6L) {
            uint8_t reg = M5.getIOExpander(0).readRegister8(0x0F);
            Ota::setBtnDebug((int)M5.getBoard(), reg, M5.BtnA.isPressed(), _dbgCnt);
        } else {
            Ota::setBtnDebug((int)M5.getBoard(), 0xFF, false, _dbgCnt);
        }
        auto bi = Button::getDebugInfo();
        Ota::setBtnEdgeDebug(bi.wasPressedCnt, bi.wasReleasedCnt, bi.shortCbCnt, bi.tripleCbCnt, bi.tapCount, bi.pressing);
    }

#if ENABLE_LORA && LORA_TEST_BEACON
    // 測試：每 3 秒自動廣播一個 beacon，供雙機 LoRa 收發驗證
    static uint32_t _beaconMs = 0;
    static uint16_t _beaconSeq = 0xE000;
    if (_loraOk && millis() - _beaconMs >= 3000) {
        _beaconMs = millis();
        LoRaPacket bp{};
        bp.dstId = DST_BROADCAST;
        bp.type  = PKT_TYPE_TEXT;
        bp.seq   = _beaconSeq++;
        const char* m = "BEACON";
        bp.payloadLen = 6;
        memcpy(bp.payload, m, 6);
        bool ok = loraHandler.sendPacket(bp);
        Display::txCount++;
        Ota::setTxStats(Display::txCount, ok);
    }
#endif

    // ── 心跳輸出（每 2 秒）：實機監看用，原生 USB 重置後仍能持續觀察 ──
    static uint32_t _hbMs = 0;
    if (millis() - _hbMs >= 2000) {
        _hbMs = millis();
        if (!g_usbDataMode)
        Serial.printf("[HB] up=%lus id=0x%04X fw=%s loraOk=%d apIP=%s app=USB%d/WiFi%d rx=%lu tx=%lu fwd=%lu\n",
                      (unsigned long)(millis() / 1000), _cfg.deviceId, FW_VERSION, _loraOk ? 1 : 0,
                      WiFi.softAPIP().toString().c_str(),
                      _transports[0].hasApp ? 1 : 0, _transports[1].hasApp ? 1 : 0,
                      (unsigned long)Display::rxCount, (unsigned long)Display::txCount,
                      (unsigned long)relayHandler.forwardCount());
    }

    delay(1);
}
