using System.Text.Json;
using LoRaPTT.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
// 避免與 MAUI 的 Microsoft.Maui.ApplicationModel.Communication.Contact 撞名
using Contact = LoRaPTT.Models.Contact;

namespace LoRaPTT.Services;

/// <summary>
/// 通訊錄與群組的本機持久化（F-003 / F-020~022）。
/// 以 MAUI Preferences 儲存 JSON，斷電/重啟不遺失。
/// </summary>
public sealed class RosterStore
{
    private const string ContactsKey = "roster.contacts";
    private const string GroupsKey = "roster.groups";
    private const string NicknameKey = "roster.nickname";

    private readonly ILogger<RosterStore> _logger;

    public RosterStore(ILogger<RosterStore> logger) => _logger = logger;

    // ── DTO（僅持久化必要欄位）──────────────────────────────
    private sealed record ContactDto(ushort Id, string Name, bool Discovered);
    private sealed record GroupDto(int Index, string Name, bool Joined);

    // ── 聯絡人 ──────────────────────────────────────────────
    public List<Contact> LoadContacts()
    {
        var json = Preferences.Default.Get(ContactsKey, "");
        if (string.IsNullOrEmpty(json)) return new();
        try
        {
            var dtos = JsonSerializer.Deserialize<List<ContactDto>>(json) ?? new();
            return dtos.Select(d => new Contact
            {
                DeviceId = d.Id,
                Name = d.Name,
                Discovered = d.Discovered,
            }).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "聯絡人資料解析失敗，重置為空");
            return new();
        }
    }

    public void SaveContacts(IEnumerable<Contact> contacts)
    {
        var dtos = contacts.Select(c => new ContactDto(c.DeviceId, c.Name, c.Discovered));
        Preferences.Default.Set(ContactsKey, JsonSerializer.Serialize(dtos));
    }

    // ── 群組 ────────────────────────────────────────────────
    public List<DeviceGroup> LoadGroups()
    {
        var json = Preferences.Default.Get(GroupsKey, "");
        if (string.IsNullOrEmpty(json)) return new();
        try
        {
            var dtos = JsonSerializer.Deserialize<List<GroupDto>>(json) ?? new();
            return dtos.Select(d => new DeviceGroup
            {
                Index = d.Index,
                Name = d.Name,
                Joined = d.Joined,
            }).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "群組資料解析失敗，重置為空");
            return new();
        }
    }

    public void SaveGroups(IEnumerable<DeviceGroup> groups)
    {
        var dtos = groups.Select(g => new GroupDto(g.Index, g.Name, g.Joined));
        Preferences.Default.Set(GroupsKey, JsonSerializer.Serialize(dtos));
    }

    // ── 本機暱稱（F-002）────────────────────────────────────
    public string LoadNickname(string fallback) => Preferences.Default.Get(NicknameKey, fallback);
    public void SaveNickname(string nickname) => Preferences.Default.Set(NicknameKey, nickname);
}
