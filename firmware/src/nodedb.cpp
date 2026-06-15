#include "nodedb.h"
#include <string.h>

namespace NodeDb {

static const int MAX_NODES = 16;
static Node _nodes[MAX_NODES];
static int  _count = 0;

// 找到 id 的索引；不存在則建立（滿了淘汰最舊 lastMs）。回傳索引。
static int findOrAdd(uint16_t id) {
    for (int i = 0; i < _count; i++)
        if (_nodes[i].id == id) return i;
    int idx;
    if (_count < MAX_NODES) {
        idx = _count++;
    } else {
        idx = 0; // 淘汰最久沒聽到的
        for (int i = 1; i < MAX_NODES; i++)
            if (_nodes[i].lastMs < _nodes[idx].lastMs) idx = i;
    }
    _nodes[idx] = Node{};
    _nodes[idx].id = id;
    return idx;
}

void heard(uint16_t id, int16_t rssi) {
    int i = findOrAdd(id);
    _nodes[i].lastMs = millis();
    _nodes[i].rssi   = rssi;
}

void setName(uint16_t id, const char* name) {
    int i = findOrAdd(id);
    strncpy(_nodes[i].name, name, sizeof(_nodes[i].name) - 1);
    _nodes[i].name[sizeof(_nodes[i].name) - 1] = 0;
}

void setPos(uint16_t id, double lat, double lon) {
    int i = findOrAdd(id);
    _nodes[i].lat = lat; _nodes[i].lon = lon; _nodes[i].hasPos = true;
}

int count() { return _count; }

// 依 lastMs 由新到舊，複製前 maxN 筆到 out（節點數少，選擇排序即可）
int snapshot(Node* out, int maxN) {
    Node tmp[MAX_NODES];
    memcpy(tmp, _nodes, sizeof(Node) * _count);
    int n = (_count < maxN) ? _count : maxN;
    for (int i = 0; i < n; i++) {
        int best = i;
        for (int j = i + 1; j < _count; j++)
            if (tmp[j].lastMs > tmp[best].lastMs) best = j;
        Node t = tmp[i]; tmp[i] = tmp[best]; tmp[best] = t;
        out[i] = tmp[i];
    }
    return n;
}

} // namespace NodeDb
