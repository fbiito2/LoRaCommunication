#include "ota_service.h"
#include <WebServer.h>
#include <Update.h>
#include <M5Unified.h>

namespace Ota {

static WebServer _server(80);
static String  _fwVer    = "?";
static bool    _active   = false;
static int     _progress = 0;
static int     _lastDrawn = -1;
static size_t  _written  = 0;
static size_t  _total    = 0;
static bool     _loraOk   = false;
static int      _loraErr  = 0;
static String   _i2cScan  = "?";
static uint32_t _rxCount  = 0;
static int      _rxRssi   = 0;
static uint16_t _rxSrc    = 0;
static uint32_t _txCount  = 0;
static int      _txOk     = -1; // -1=尚無, 1=最後一次成功, 0=失敗
static int      _boardType = -1;    // 按鈕偵錯：板型
static uint8_t  _btnReg    = 0xFF;  // 按鈕偵錯：IO 擴充晶片 reg 0x0F 原始值
static bool     _btnPressed = false; // 按鈕偵錯：M5.BtnA.isPressed()
static uint32_t _loopCnt   = 0;     // 按鈕偵錯：main loop 執行次數
// 邊緣事件偵錯
static uint32_t _wpCnt  = 0; // wasPressed 計數
static uint32_t _wrCnt  = 0; // wasReleased 計數
static uint32_t _scCnt  = 0; // shortCb 計數
static uint32_t _tcCnt  = 0; // tripleCb 計數
static int      _tapCnt = 0; // 目前 tapCount
static bool     _btnPressing = false; // 目前 pressing 狀態

void setLoraStatus(bool ok, int err) { _loraOk = ok; _loraErr = err; }
void setI2cScan(const String& s) { _i2cScan = s; }
void setRxStats(uint32_t count, int rssi, uint16_t src) { _rxCount = count; _rxRssi = rssi; _rxSrc = src; }
void setTxStats(uint32_t count, bool ok) { _txCount = count; _txOk = ok ? 1 : 0; }
void setBtnDebug(int board, uint8_t reg0F, bool btnPressed, uint32_t loopCnt) {
    _boardType = board; _btnReg = reg0F; _btnPressed = btnPressed; _loopCnt = loopCnt;
}
void setBtnEdgeDebug(uint32_t wpCnt, uint32_t wrCnt, uint32_t scCnt, uint32_t tcCnt, int tapCnt, bool pressing) {
    _wpCnt = wpCnt; _wrCnt = wrCnt; _scCnt = scCnt; _tcCnt = tcCnt; _tapCnt = tapCnt; _btnPressing = pressing;
}

// ── OLED 直接繪製進度（OTA 上傳期間 main loop 被 handleClient 阻塞，
//    Display::loop 不會執行，故由此直接畫）─────────────────────
static void drawProgress(const char* title) {
    M5.Display.fillScreen(TFT_BLACK);
    M5.Display.setCursor(0, 0);
    M5.Display.println(title);
    M5.Display.printf("%d%%\n", _progress);
    // 進度條
    int w = M5.Display.width();
    int bar = (w - 4) * _progress / 100;
    M5.Display.drawRect(2, 30, w - 4, 10, TFT_WHITE);
    M5.Display.fillRect(2, 30, bar, 10, TFT_WHITE);
}

// GET /version（F-064）+ LoRa 狀態（實機除錯）
static void handleVersion() {
    char src[8]; snprintf(src, sizeof(src), "%04X", _rxSrc);
    char regHex[8]; snprintf(regHex, sizeof(regHex), "0x%02X", _btnReg);
    String j = String("{\"fw_ver\":\"") + _fwVer + "\",\"lora_ok\":" +
               (_loraOk ? "true" : "false") + ",\"lora_err\":" + String(_loraErr) +
               ",\"i2c\":\"" + _i2cScan + "\",\"rx\":" + String(_rxCount) +
               ",\"rssi\":" + String(_rxRssi) + ",\"src\":\"" + src + "\"" +
               ",\"tx\":" + String(_txCount) + ",\"txok\":" + String(_txOk) +
               ",\"board\":" + String(_boardType) +
               ",\"btn_reg\":\"" + regHex + "\"" +
               ",\"btn\":" + (_btnPressed ? "true" : "false") +
               ",\"loops\":" + String(_loopCnt) +
               ",\"wp\":" + String(_wpCnt) +
               ",\"wr\":" + String(_wrCnt) +
               ",\"sc\":" + String(_scCnt) +
               ",\"tc\":" + String(_tcCnt) +
               ",\"tap\":" + String(_tapCnt) +
               ",\"pressing\":" + (_btnPressing ? "true" : "false") + "}";
    _server.send(200, "application/json", j);
}

// POST /update 完成回應
static void handleUpdateDone() {
    bool ok = !Update.hasError();
    _server.send(ok ? 200 : 500, "application/json",
                 ok ? "{\"status\":\"ota_ok\"}" : "{\"status\":\"ota_fail\"}");
    if (ok) {
        Serial.println("[OTA] 更新成功，1 秒後重開機");
        delay(1000);
        ESP.restart();
    } else {
        _active = false;
        Serial.println("[OTA] 更新失敗");
    }
}

// POST /update 上傳串流處理
static void handleUpload() {
    HTTPUpload& up = _server.upload();

    if (up.status == UPLOAD_FILE_START) {
        _active = true;
        _written = 0;
        _progress = 0;
        _lastDrawn = -1;
        _total = _server.header("Content-Length").toInt(); // 含 multipart 邊界，略大於影像
        Serial.printf("[OTA] 開始：%s（約 %u bytes）\n",
                      up.filename.c_str(), (unsigned)_total);
        if (!Update.begin(UPDATE_SIZE_UNKNOWN)) Update.printError(Serial);
        drawProgress("OTA 更新中");
    } else if (up.status == UPLOAD_FILE_WRITE) {
        if (Update.write(up.buf, up.currentSize) != up.currentSize)
            Update.printError(Serial);
        _written += up.currentSize;
        if (_total > 0) _progress = (int)(_written * 100 / _total);
        if (_progress > 100) _progress = 100;
        if (_progress - _lastDrawn >= 2) { _lastDrawn = _progress; drawProgress("OTA 更新中"); }
    } else if (up.status == UPLOAD_FILE_END) {
        if (Update.end(true)) {
            _progress = 100;
            drawProgress("OTA 完成");
            Serial.printf("[OTA] 寫入完成 %u bytes\n", (unsigned)_written);
        } else {
            Update.printError(Serial);
            drawProgress("OTA 失敗");
        }
    } else if (up.status == UPLOAD_FILE_ABORTED) {
        Update.abort();
        _active = false;
        Serial.println("[OTA] 上傳中止");
    }
}

void begin(const char* fwVersion) {
    _fwVer = fwVersion;
    const char* headers[] = { "Content-Length" };
    _server.collectHeaders(headers, 1);
    _server.on("/version", HTTP_GET, handleVersion);
    _server.on("/update", HTTP_POST, handleUpdateDone, handleUpload);
    _server.begin();
    Serial.println("[OTA] HTTP 更新伺服器啟動於 port 80");
}

void loop()      { _server.handleClient(); }
bool isActive()  { return _active; }
int  progress()  { return _progress; }

} // namespace Ota
