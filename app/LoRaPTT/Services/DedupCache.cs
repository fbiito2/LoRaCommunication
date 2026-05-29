namespace LoRaPTT.Services;

/// <summary>
/// 收訊去重快取（ring buffer）。洪泛網路中同一封包可能經多條路徑重複抵達，
/// 以 (SRC_ID, SEQ) 為鍵去重，與韌體 relay 去重機制對應。
/// </summary>
public sealed class DedupCache
{
    private readonly (ushort Src, ushort Seq)[] _buf;
    private readonly HashSet<(ushort, ushort)> _set;
    private int _head;
    private readonly object _lock = new();

    public DedupCache(int capacity = 128)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buf = new (ushort, ushort)[capacity];
        _set = new HashSet<(ushort, ushort)>(capacity);
    }

    /// <summary>
    /// 查詢並登記。若 (src, seq) 已存在回傳 true（應丟棄）；
    /// 否則登記後回傳 false（首次出現，應處理）。
    /// </summary>
    public bool SeenOrAdd(ushort src, ushort seq)
    {
        var key = (src, seq);
        lock (_lock)
        {
            if (_set.Contains(key)) return true;

            // 覆蓋最舊一筆（ring buffer）
            var old = _buf[_head];
            if (old != default) _set.Remove(old);
            _buf[_head] = key;
            _set.Add(key);
            _head = (_head + 1) % _buf.Length;
            return false;
        }
    }
}
