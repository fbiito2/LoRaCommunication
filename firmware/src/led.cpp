#include "led.h"

namespace Led {

// 閃爍狀態
static bool  _blink      = false;
static bool  _blinkState = false;
static uint32_t _color   = 0;
static uint32_t _lastMs  = 0;
static const uint32_t BLINK_MS = 300;

static void _set(uint32_t color, bool blink = false) {
    _color = color;
    _blink = blink;
    if (!blink) M5.Power.setLed(0); // 先清除（M5Unified 透過電源管理 LED）
    // WS2812C 由 M5.dis 控制（Unit C6L 使用 M5Unified）
    // 顏色格式：0xRRGGBB
    // TODO: 確認 Unit C6L 的 WS2812 是否透過 M5.dis 或其他 API
    // M5.dis.drawpix(0, color);
    Serial.printf("[LED] 顏色: 0x%06X，閃爍: %s\n", color, blink ? "是" : "否");
}

void init()           { M5.begin(); _set(0x000000); }
void setCarryMode()   { _set(0x00FF00); }          // 綠色常亮
void setBaseMode()    { _set(0x0000FF); }           // 藍色常亮
void setBaseWaiting() { _set(0x0000FF, true); }     // 藍色閃爍
void setLoRaTx()      { _set(0xFF0000, true); }     // 紅色閃爍
void setLoRaRx()      { _set(0xFFFF00, true); }     // 黃色閃爍
void setError()       { _set(0xFF0000); }           // 紅色常亮
void setSosAlert()    { _set(0xFF0000, true); }     // SOS 紅色快閃（F-073）
void setOff()         { _set(0x000000); }

void loop() {
    if (!_blink) return;
    uint32_t now = millis();
    if (now - _lastMs < BLINK_MS) return;
    _lastMs     = now;
    _blinkState = !_blinkState;
    // 閃爍：交替顯示顏色與關閉
    // M5.dis.drawpix(0, _blinkState ? _color : 0x000000);
}

} // namespace Led
