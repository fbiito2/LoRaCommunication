#include "packet.h"
#include <string.h>

size_t packetSerialize(const LoRaPacket& pkt, uint8_t* out, size_t outLen) {
    size_t total = PKT_HEADER_LEN + pkt.payloadLen + PKT_MAC_LEN;
    if (total > outLen) return 0;

    size_t i = 0;
    out[i++] = (pkt.srcId >> 8) & 0xFF;
    out[i++] =  pkt.srcId       & 0xFF;
    out[i++] = (pkt.dstId >> 8) & 0xFF;
    out[i++] =  pkt.dstId       & 0xFF;
    out[i++] =  pkt.hop;
    out[i++] = (pkt.seq >> 8)   & 0xFF;
    out[i++] =  pkt.seq         & 0xFF;
    out[i++] =  pkt.type;

    memcpy(&out[i], pkt.payload, pkt.payloadLen);
    i += pkt.payloadLen;

    memcpy(&out[i], pkt.mac, PKT_MAC_LEN);
    i += PKT_MAC_LEN;

    return i;
}

bool packetDeserialize(const uint8_t* in, size_t inLen, LoRaPacket& pkt) {
    if (inLen < PKT_HEADER_LEN + PKT_MAC_LEN) return false;

    size_t i = 0;
    pkt.srcId = ((uint16_t)in[i] << 8) | in[i+1]; i += 2;
    pkt.dstId = ((uint16_t)in[i] << 8) | in[i+1]; i += 2;
    pkt.hop   = in[i++];
    pkt.seq   = ((uint16_t)in[i] << 8) | in[i+1]; i += 2;
    pkt.type  = in[i++];

    pkt.payloadLen = inLen - PKT_HEADER_LEN - PKT_MAC_LEN;
    if (pkt.payloadLen > PKT_MAX_PAYLOAD) return false;

    memcpy(pkt.payload, &in[i], pkt.payloadLen);
    i += pkt.payloadLen;

    memcpy(pkt.mac, &in[i], PKT_MAC_LEN);
    return true;
}
