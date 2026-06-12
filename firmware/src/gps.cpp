#include "gps.h"

namespace Gps {

// ── Grove PORT.A 腳位（C6L）──
static const int PIN_RX = 4;   // C6L RX ← GPS TXD（白）
static const int PIN_TX = 5;   // C6L TX → GPS RXD（黃）

// 自動偵測鮑率：AT6668/U032 出廠多為 9600，部分模組 38400。
static const uint32_t BAUDS[] = { 9600, 38400 };
static int      _baudIdx     = 0;
static uint32_t _lastByteMs  = 0;   // 最後一次收到位元組的時間
static uint32_t _baudTryMs   = 0;   // 本鮑率開始嘗試的時間

// 解析狀態
static char     _line[120];
static int      _lineLen     = 0;

static bool     _hasFix      = false;
static int      _sats        = 0;
static double   _lat         = 0;
static double   _lon         = 0;
static uint32_t _rxBytes     = 0;
static uint32_t _sentences   = 0;

static void startBaud(uint32_t b) {
    Serial1.end();
    Serial1.begin(b, SERIAL_8N1, PIN_RX, PIN_TX);
    _baudTryMs = millis();
}

void begin() {
    startBaud(BAUDS[_baudIdx]);
    Serial1.printf(""); // no-op，確保連結
}

// NMEA ddmm.mmmm + 方向 → 十進位度
static double nmeaToDeg(const char* val, char dir) {
    if (!val || !*val) return 0;
    double v = atof(val);
    int deg = (int)(v / 100);
    double minutes = v - deg * 100;
    double d = deg + minutes / 60.0;
    if (dir == 'S' || dir == 'W') d = -d;
    return d;
}

// 解析一條 GGA：$xxGGA,time,lat,NS,lon,EW,fix,sats,hdop,alt,...
static void parseGGA(char* s) {
    // 以逗號切欄位（就地）
    char* f[16];
    int n = 0;
    f[n++] = s;
    for (char* p = s; *p && n < 16; ++p) {
        if (*p == ',') { *p = 0; f[n++] = p + 1; }
    }
    if (n < 8) return;
    int fixQ = atoi(f[6]);
    _hasFix  = fixQ > 0;
    _sats    = atoi(f[7]);
    if (_hasFix) {
        _lat = nmeaToDeg(f[2], f[3][0]);
        _lon = nmeaToDeg(f[4], f[5][0]);
    }
    _sentences++;
}

void loop() {
    while (Serial1.available()) {
        char c = (char)Serial1.read();
        _rxBytes++;
        _lastByteMs = millis();
        if (c == '\n' || c == '\r') {
            if (_lineLen > 6) {
                _line[_lineLen] = 0;
                // 比對 "GGA"（涵蓋 $GPGGA/$GNGGA/$BDGGA…）
                if (strstr(_line, "GGA")) parseGGA(_line);
            }
            _lineLen = 0;
        } else if (_lineLen < (int)sizeof(_line) - 1) {
            _line[_lineLen++] = c;
        }
    }

    // 自動鮑率：本鮑率試了 4 秒仍一個位元組都沒收到 → 換下一個鮑率
    if (_rxBytes == 0 && millis() - _baudTryMs > 4000) {
        _baudIdx = (_baudIdx + 1) % (sizeof(BAUDS) / sizeof(BAUDS[0]));
        startBaud(BAUDS[_baudIdx]);
    }
}

bool     hasFix()    { return _hasFix; }
int      sats()      { return _sats; }
double   lat()       { return _lat; }
double   lon()       { return _lon; }
uint32_t rxBytes()   { return _rxBytes; }
uint32_t baud()      { return BAUDS[_baudIdx]; }
uint32_t sentences() { return _sentences; }

} // namespace Gps
