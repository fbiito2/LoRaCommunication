using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LoRaPTT.Services;

/// <summary>
/// 韌體 OTA 上傳服務（F-060/064）。透過 WiFi HTTP 將 .bin 上傳到 C6L。
///   GET  http://{host}/version → 取得目前韌體版本
///   POST http://{host}/update  → multipart 上傳韌體，回報進度
/// </summary>
public sealed class OtaService
{
    /// <summary>C6L WiFi AP 預設位址</summary>
    public const string DefaultHost = "192.168.4.1";

    private readonly ILogger<OtaService> _logger;
    private readonly HttpClient _http;

    public OtaService(ILogger<OtaService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>查詢 C6L 目前韌體版本（F-064），失敗回 null</summary>
    public async Task<string?> GetVersionAsync(string host = DefaultHost,
        CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"http://{host}/version", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("fw_ver", out var v) ? v.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查詢韌體版本失敗");
            return null;
        }
    }

    /// <summary>
    /// 上傳韌體 .bin 到 C6L。progress 回報 0~100。
    /// </summary>
    public async Task<bool> UploadAsync(Stream firmware, long length, string fileName,
        string host, IProgress<int>? progress, CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var inner = new ProgressableStreamContent(firmware, length,
                sent => progress?.Report(length > 0 ? (int)(sent * 100 / length) : 0));
            content.Add(inner, "firmware", fileName);

            using var resp = await _http.PostAsync($"http://{host}/update", content, ct);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                _logger.LogInformation("OTA 上傳完成，C6L 將重開機");
                progress?.Report(100);
                return true;
            }
            _logger.LogError("OTA 上傳失敗，HTTP {Code}", (int)resp.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OTA 上傳發生錯誤");
            return false;
        }
    }

    /// <summary>可回報傳送進度的 HttpContent</summary>
    private sealed class ProgressableStreamContent : HttpContent
    {
        private const int BufferSize = 16384;
        private readonly Stream _stream;
        private readonly long _length;
        private readonly Action<long> _onProgress;

        public ProgressableStreamContent(Stream stream, long length, Action<long> onProgress)
        {
            _stream = stream;
            _length = length;
            _onProgress = onProgress;
        }

        protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context)
        {
            var buffer = new byte[BufferSize];
            long sent = 0;
            int read;
            while ((read = await _stream.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                sent += read;
                _onProgress(sent);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
