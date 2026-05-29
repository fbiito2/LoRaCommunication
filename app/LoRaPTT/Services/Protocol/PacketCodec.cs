namespace LoRaPTT.Services.Protocol;

/// <summary>
/// LoRa 封包序列化/反序列化 — 與韌體 packet.cpp / crypto.cpp 位元級相容。
///
/// 封包結構：SRC(2 BE) DST(2 BE) HOP(1) SEQ(2 BE) TYPE(1) PAYLOAD(N) MAC(4 BE)
/// MAC：Phase 1 為 CRC32（計算時跳過 HOP 欄位，使中繼改 HOP 不破壞 MAC）。
///
/// 傳輸層幀格式（由 ICommService 實作負責）：
///   - USB Serial：前綴 2-byte 大端長度
///   - WiFi UDP：無前綴，一個 datagram 即一個封包
/// </summary>
public static class PacketCodec
{
    public const int HeaderLen = 8;    // SRC(2)+DST(2)+HOP(1)+SEQ(2)+TYPE(1)
    public const int MacLen = 4;       // CRC32 / HMAC 截斷
    public const int MaxPayload = 243; // 255 - header(8) - mac(4)
    public const int MaxLen = 255;
    private const int HopOffset = 4;   // HOP 在緩衝區中的位移（MAC 計算跳過）

    /// <summary>將封包序列化為 LoRa 位元組（含 CRC32 MAC）</summary>
    public static byte[] Serialize(LoRaPacket pkt)
    {
        var payload = pkt.Payload ?? Array.Empty<byte>();
        if (payload.Length > MaxPayload)
            throw new ArgumentException(
                $"payload 長度 {payload.Length} 超過上限 {MaxPayload}", nameof(pkt));

        var buf = new byte[HeaderLen + payload.Length + MacLen];
        buf[0] = (byte)(pkt.SrcId >> 8);
        buf[1] = (byte)(pkt.SrcId & 0xFF);
        buf[2] = (byte)(pkt.DstId >> 8);
        buf[3] = (byte)(pkt.DstId & 0xFF);
        buf[4] = pkt.Hop;
        buf[5] = (byte)(pkt.Seq >> 8);
        buf[6] = (byte)(pkt.Seq & 0xFF);
        buf[7] = (byte)pkt.Type;
        Array.Copy(payload, 0, buf, HeaderLen, payload.Length);

        int bodyLen = HeaderLen + payload.Length;
        WriteMac(buf, bodyLen);
        return buf;
    }

    /// <summary>
    /// 解析 LoRa 位元組為封包。
    /// </summary>
    /// <param name="verifyMac">
    /// 是否驗證 CRC32。來自自己 C6L 的封包已由韌體驗證過（且 Phase 2 解密後
    /// MAC 不再吻合），故預設 false；驗證原始空中封包時才設 true。
    /// </param>
    public static bool TryDeserialize(byte[] data, out LoRaPacket pkt, bool verifyMac = false)
    {
        pkt = new LoRaPacket();
        if (data is null || data.Length < HeaderLen + MacLen)
            return false;

        int payloadLen = data.Length - HeaderLen - MacLen;
        if (payloadLen > MaxPayload)
            return false;

        if (verifyMac && !VerifyMac(data, HeaderLen + payloadLen))
            return false;

        var payload = new byte[payloadLen];
        Array.Copy(data, HeaderLen, payload, 0, payloadLen);

        pkt = new LoRaPacket
        {
            SrcId = (ushort)((data[0] << 8) | data[1]),
            DstId = (ushort)((data[2] << 8) | data[3]),
            Hop = data[4],
            Seq = (ushort)((data[5] << 8) | data[6]),
            Type = (PacketType)data[7],
            Payload = payload,
        };
        return true;
    }

    // ── MAC（Phase 1：CRC32，跳過 HOP 欄位）──────────────────────────
    private static void WriteMac(byte[] buf, int bodyLen)
    {
        uint crc = Crc32SkipHop(buf, bodyLen);
        buf[bodyLen + 0] = (byte)(crc >> 24);
        buf[bodyLen + 1] = (byte)(crc >> 16);
        buf[bodyLen + 2] = (byte)(crc >> 8);
        buf[bodyLen + 3] = (byte)crc;
    }

    private static bool VerifyMac(byte[] data, int bodyLen)
    {
        uint crc = Crc32SkipHop(data, bodyLen);
        return data[bodyLen + 0] == (byte)(crc >> 24)
            && data[bodyLen + 1] == (byte)(crc >> 16)
            && data[bodyLen + 2] == (byte)(crc >> 8)
            && data[bodyLen + 3] == (byte)crc;
    }

    /// <summary>對 buf[0..bodyLen) 計算 CRC32，但跳過 HOP 欄位（與韌體一致）</summary>
    private static uint Crc32SkipHop(byte[] buf, int bodyLen)
    {
        uint crc = 0xFFFFFFFFu;
        crc = Crc32Step(crc, buf, 0, HopOffset);                       // SRC+DST
        crc = Crc32Step(crc, buf, HopOffset + 1, bodyLen - HopOffset - 1); // SEQ+TYPE+PAYLOAD
        return crc ^ 0xFFFFFFFFu;
    }

    // CRC32（IEEE 802.3，反射式 poly 0xEDB88320）— 可分段累加
    private static uint Crc32Step(uint crc, byte[] data, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            crc ^= data[offset + i];
            for (int b = 0; b < 8; b++)
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
        }
        return crc;
    }
}
