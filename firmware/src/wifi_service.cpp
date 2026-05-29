#include "wifi_service.h"

WiFiService wifiService;

void WiFiService::setSsid(const char* ssid) { strncpy(_ssid, ssid, 31); }
void WiFiService::setPassword(const char* pass) { strncpy(_pass, pass, 31); }

void WiFiService::begin() {
    setApEnabled(true);
}

void WiFiService::setApEnabled(bool enable) {
    if (enable == _apEnabled) return;

    if (enable) {
        // 開啟 WiFi AP 熱點模式
        WiFi.mode(WIFI_AP);
        WiFi.softAP(_ssid, _pass);
        IPAddress ip = WiFi.softAPIP();
        Serial.printf("[WiFi] AP 已啟動，SSID: %s，IP: %s\n",
                      _ssid, ip.toString().c_str());
        _udp.begin(WIFI_UDP_PORT);
        Serial.printf("[WiFi] UDP Server 監聽 port %d\n", WIFI_UDP_PORT);
    } else {
        _udp.stop();
        WiFi.softAPdisconnect(true);
        WiFi.mode(WIFI_OFF);
        _clientKnown = false;
        Serial.println("[WiFi] AP 已關閉（省電）");
    }
    _apEnabled = enable;
}

bool WiFiService::isConnected() {
    return _clientKnown;
}

void WiFiService::send(const uint8_t* data, size_t len) {
    if (!_clientKnown) return;
    _udp.beginPacket(_clientIp, _clientPort);
    _udp.write(data, len);
    _udp.endPacket();
}

void WiFiService::onReceive(std::function<void(const uint8_t*, size_t)> cb) {
    _rxCallback = cb;
}

void WiFiService::loop() {
    int pktSize = _udp.parsePacket();
    if (pktSize <= 0) return;

    // 記錄手機 IP/Port，之後 send() 回傳給同一個位址
    _clientIp    = _udp.remoteIP();
    _clientPort  = _udp.remotePort();
    _clientKnown = true;

    uint8_t buf[256];
    int len = _udp.read(buf, sizeof(buf));
    if (len > 0 && _rxCallback)
        _rxCallback(buf, (size_t)len);
}
