#include "button.h"
#include <M5Unified.h>

namespace Button {

static VoidCb    _shortCb;
static VoidCb    _longCb;
static VoidCb    _veryLongCb;
static uint32_t  _pressMs = 0;
static bool      _pressing = false;
static bool      _beep3 = false; // 已對 3 秒門檻提示
static bool      _beep6 = false; // 已對 6 秒門檻提示

static const uint32_t LONG_MS      = 3000;
static const uint32_t VERY_LONG_MS = 6000;

void init() { /* M5.begin() 已初始化按鈕 */ }

void onShortPress(VoidCb cb)    { _shortCb    = cb; }
void onLongPress(VoidCb cb)     { _longCb     = cb; }
void onVeryLongPress(VoidCb cb) { _veryLongCb = cb; }

void loop() {
    M5.update(); // 必須在 loop 呼叫，M5Unified 更新按鈕狀態

    if (M5.BtnA.wasPressed()) {
        _pressMs  = millis();
        _pressing = true;
        _beep3    = false;
        _beep6    = false;
    }

    // 按住期間：跨越門檻時給提示音（僅回饋，不執行動作）
    if (_pressing && M5.BtnA.isPressed()) {
        uint32_t held = millis() - _pressMs;
        if (!_beep3 && held >= LONG_MS)      { _beep3 = true; M5.Speaker.tone(1200, 60); }
        if (!_beep6 && held >= VERY_LONG_MS) { _beep6 = true; M5.Speaker.tone(800, 60); }
    }

    // 放開時依「按住時長」判定唯一動作（修正：6 秒分支不再被 3 秒搶先鎖死）
    if (M5.BtnA.wasReleased() && _pressing) {
        uint32_t held = millis() - _pressMs;
        _pressing = false;
        if (held >= VERY_LONG_MS) {
            if (_veryLongCb) _veryLongCb();
        } else if (held >= LONG_MS) {
            if (_longCb) _longCb();
        } else {
            if (_shortCb) _shortCb();
        }
    }
}

} // namespace Button
