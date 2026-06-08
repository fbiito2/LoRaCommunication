#include "button.h"
#include <M5Unified.h>

namespace Button {

static VoidCb    _shortCb;
static VoidCb    _longCb;
static VoidCb    _veryLongCb;
static VoidCb    _tripleCb;      // 連按 3 下 callback（F-071 SOS）
static uint32_t  _pressMs = 0;
static bool      _pressing = false;
static bool      _beep3 = false; // 已對 3 秒門檻提示
static bool      _beep6 = false; // 已對 6 秒門檻提示

// 連按偵測狀態
static int       _tapCount = 0;        // 短按連擊次數
static uint32_t  _lastTapMs = 0;       // 上次短按放開時間
static const uint32_t TAP_WINDOW = 800; // 連擊判定窗口（ms）
static const int TAP_TARGET = 3;        // 連按目標次數

// 偵錯計數器
static uint32_t _dbgWasPressed  = 0;
static uint32_t _dbgWasReleased = 0;
static uint32_t _dbgShortCb     = 0;
static uint32_t _dbgTripleCb    = 0;

static const uint32_t LONG_MS      = 3000;
static const uint32_t VERY_LONG_MS = 6000;

void init() { /* M5.begin() 已初始化按鈕 */ }

void onShortPress(VoidCb cb)    { _shortCb    = cb; }
void onLongPress(VoidCb cb)     { _longCb     = cb; }
void onVeryLongPress(VoidCb cb) { _veryLongCb = cb; }
void onTriplePress(VoidCb cb)   { _tripleCb   = cb; }

void loop() {
    M5.update(); // 必須在 loop 呼叫，M5Unified 更新按鈕狀態

    if (M5.BtnA.wasPressed()) {
        _dbgWasPressed++;
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

    // 放開時依「按住時長」判定動作
    if (M5.BtnA.wasReleased() && _pressing) {
        _dbgWasReleased++;
        uint32_t held = millis() - _pressMs;
        _pressing = false;
        if (held >= VERY_LONG_MS) {
            // 長按 6 秒：重置設定
            _tapCount = 0;
            if (_veryLongCb) _veryLongCb();
        } else if (held >= LONG_MS) {
            // 長按 3 秒：開/關 WiFi
            _tapCount = 0;
            if (_longCb) _longCb();
        } else {
            // 短按：累計連擊次數
            uint32_t now = millis();
            if (now - _lastTapMs > TAP_WINDOW) _tapCount = 0; // 超時重計
            _tapCount++;
            _lastTapMs = now;

            if (_tapCount >= TAP_TARGET) {
                // 連按 3 下 → SOS（F-071）
                _tapCount = 0;
                _dbgTripleCb++;
                if (_tripleCb) _tripleCb();
            }
            // 注意：不在此處立即觸發 shortCb，需等連擊窗口逾時才確認是普通短按
        }
    }

    // 連擊窗口逾時：確認為普通短按（1 或 2 下皆視為短按）
    if (_tapCount > 0 && !_pressing && (millis() - _lastTapMs > TAP_WINDOW)) {
        _dbgShortCb++;
        if (_shortCb) _shortCb();
        _tapCount = 0;
    }
}

DebugInfo getDebugInfo() {
    return {
        _dbgWasPressed,
        _dbgWasReleased,
        _dbgShortCb,
        _dbgTripleCb,
        _tapCount,
        _pressing
    };
}

} // namespace Button
