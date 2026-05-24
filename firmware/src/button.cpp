#include "button.h"
#include <M5Unified.h>

namespace Button {

static VoidCb    _shortCb;
static VoidCb    _longCb;
static VoidCb    _veryLongCb;
static uint32_t  _pressMs  = 0;
static bool      _pressing  = false;
static bool      _longFired = false;

void init() { /* M5.begin() 已初始化按鈕 */ }

void onShortPress(VoidCb cb)    { _shortCb    = cb; }
void onLongPress(VoidCb cb)     { _longCb     = cb; }
void onVeryLongPress(VoidCb cb) { _veryLongCb = cb; }

void loop() {
    M5.update(); // 必須在 loop 呼叫，M5Unified 更新按鈕狀態

    if (M5.BtnA.wasPressed()) {
        _pressMs  = millis();
        _pressing  = true;
        _longFired = false;
    }

    if (_pressing && M5.BtnA.isPressed()) {
        uint32_t held = millis() - _pressMs;
        if (!_longFired && held >= 6000) {
            // 長按 6 秒：重置
            _longFired = true;
            if (_veryLongCb) _veryLongCb();
        } else if (!_longFired && held >= 3000) {
            // 長按 3 秒：切換模式
            _longFired = true;
            if (_longCb) _longCb();
        }
    }

    if (M5.BtnA.wasReleased()) {
        if (_pressing && !_longFired) {
            // 短按：切換頁面
            if (_shortCb) _shortCb();
        }
        _pressing = false;
    }
}

} // namespace Button
