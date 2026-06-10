namespace LoRaPTT.Services;

/// <summary>
/// USB 傳輸層標記介面（F-054）。僅 Android 有實作；用於 DI 區分，
/// 讓 <see cref="CommRouter"/> 能在「有 USB 實作」時注入、無則略過。
/// </summary>
public interface IUsbCommService : ICommService
{
}
