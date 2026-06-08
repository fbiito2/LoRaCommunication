#include "led.h"

namespace Led {

// 閃爍狀態
static bool  _blink      = false;
static bool  _blinkState = false;
static uint32_t _color   = 0;
static uint32_t _lastMs  = 0;
static const uint32_t BLINK_MS = 300;

// Unit C6L 的 WS2812C RGB LED 接在 GPIO2（由 M5Unified 板表確認）。
// 用 ESP32 核心內建 neopixelWrite() 經 RMT 驅動，不需額外函式庫。
static const uint8_t LED_PIN    = 2;
static const uint8_t LED_BRIGHT = 40; // 整體亮度 0~255（全亮過刺眼且耗電）

// 寫入單顆 WS2812（顏色格式 0xRRGGBB，套用整體亮度縮放）
static void _write(uint32_t c) {
    uint8_t r = (uint8_t)((((c >> 16) & 0xFF) * LED_BRIGHT) / 255);
    uint8_t g = (uint8_t)((((c >>  8) & 0xFF) * LED_BRIGHT) / 255);
    uint8_t b = (uint8_t)((( c        & 0xFF) * LED_BRIGHT) / 255);
    neopixelWrite(LED_PIN, r, g, b); // 內部處理 GRB 順序
}

static void _set(uint32_t color, bool blink = false) {
    _color      = color;
    _blink      = blink;
    _blinkState = true;
    _write(color); // 常亮立即點亮；閃爍先亮，後續由 loop() 切換
}

void init()           { _write(0x000000); } // 關燈（neopixelWrite 首次呼叫自動初始化 RMT）
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
    _write(_blinkState ? _color : 0x000000); // 交替顯示顏色與關閉
}

} // namespace Led
