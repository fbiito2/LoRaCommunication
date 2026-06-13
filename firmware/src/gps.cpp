#include "gps.h"

namespace Gps {

// 接 C6L Grove PORT.A：GPS TXD→C6L RX、GPS RXD→C6L TX。
// RX 腳位（GPIO4 或 GPIO5）與鮑率（9600/38400）皆不確定 → 自動輪試 4 種組合，
// 看到合法 NMEA 句子（以 '$' 開頭、含 '*' 校驗分隔）才鎖定，避免被雜訊誤鎖。
struct Combo { int rx; int tx; uint32_t baud; };
static const Combo COMBOS[] = {
    { 4, 5, 9600 }, { 4, 5, 38400 }, { 4, 5, 115200 }, { 4, 5, 4800 },
    { 5, 4, 9600 }, { 5, 4, 38400 }, { 5, 4, 115200 }, { 5, 4, 4800 },
};
static const int N_COMBO = sizeof(COMBOS) / sizeof(COMBOS[0]);

static int      _idx        = 0;
static bool     _locked     = false;
static uint32_t _comboMs    = 0;

static char     _line[120];
static int      _lineLen    = 0;

static bool     _hasFix     = false;
static int      _sats       = 0;
static double   _lat        = 0;
static double   _lon        = 0;
static uint32_t _rxBytes    = 0;
static uint32_t _sentences  = 0;

static void startCombo(int i) {
    Serial1.end();
    delay(5);
    Serial1.begin(COMBOS[i].baud, SERIAL_8N1, COMBOS[i].rx, COMBOS[i].tx);
    _comboMs = millis();
    _lineLen = 0;
}

void begin() {
    startCombo(0);
}

static double nmeaToDeg(const char* val, char dir) {
    if (!val || !*val) return 0;
    double v = atof(val);
    int deg = (int)(v / 100);
    double minutes = v - deg * 100;
    double d = deg + minutes / 60.0;
    if (dir == 'S' || dir == 'W') d = -d;
    return d;
}

// $xxGGA,time,lat,NS,lon,EW,fix,sats,...
static void parseGGA(char* s) {
    char* f[16]; int n = 0;
    f[n++] = s;
    for (char* p = s; *p && n < 16; ++p)
        if (*p == ',') { *p = 0; f[n++] = p + 1; }
    if (n < 8) return;
    int fixQ = atoi(f[6]);
    _hasFix  = fixQ > 0;
    _sats    = atoi(f[7]);
    if (_hasFix) { _lat = nmeaToDeg(f[2], f[3][0]); _lon = nmeaToDeg(f[4], f[5][0]); }
    _sentences++;
}

static void handleLine() {
    if (_lineLen < 6) return;
    _line[_lineLen] = 0;
    // 合法 NMEA：'$' 開頭且含 '*' 校驗 → 鎖定此組合
    if (_line[0] == '$' && strchr(_line, '*')) {
        _locked = true;
        if (strstr(_line, "GGA")) parseGGA(_line);
    }
}

void loop() {
    while (Serial1.available()) {
        char c = (char)Serial1.read();
        _rxBytes++;
        if (c == '\n' || c == '\r') { handleLine(); _lineLen = 0; }
        else if (_lineLen < (int)sizeof(_line) - 1) _line[_lineLen++] = c;
    }
    // 未鎖定且本組合試了 3 秒仍沒看到合法句子 → 換下一組合
    if (!_locked && millis() - _comboMs > 3000) {
        _idx = (_idx + 1) % N_COMBO;
        startCombo(_idx);
    }
}

bool     hasFix()    { return _hasFix; }
int      sats()      { return _sats; }
double   lat()       { return _lat; }
double   lon()       { return _lon; }
uint32_t rxBytes()   { return _rxBytes; }
uint32_t baud()      { return COMBOS[_idx].baud; }
uint32_t rxPin()     { return COMBOS[_idx].rx; }
uint32_t sentences() { return _sentences; }

} // namespace Gps
