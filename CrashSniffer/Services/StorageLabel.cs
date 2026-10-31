using System;
using System.Collections.Generic;
using System.Linq;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>磁盘显示辅助：稳定键、简短标签、按读数查找。</summary>
public static class StorageLabel
{
    /// <summary>跨读数/跨快照稳定的磁盘标识</summary>
    public static string Key(StorageReading s)
    {
        if (!string.IsNullOrEmpty(s.Id)) return s.Id;
        if (s.Index >= 0) return $"idx:{s.Index}";
        return "name:" + s.Name;
    }

    /// <summary>简短标签：优先盘符，其次磁盘序号/型号</summary>
    public static string Short(StorageReading s)
    {
        if (s.Volumes.Count > 0) return string.Join("/", s.Volumes);
        if (s.Index >= 0) return $"磁盘{s.Index}";
        return string.IsNullOrWhiteSpace(s.Name) ? "磁盘" : s.Name;
    }

    /// <summary>完整标签：盘符 + 型号</summary>
    public static string Full(StorageReading s)
    {
        string shortLabel = Short(s);
        if (string.IsNullOrWhiteSpace(s.Name)) return shortLabel;
        return $"{shortLabel} {s.Name}";
    }

    public static StorageReading? Find(HardwareReading reading, string key)
    {
        if (reading.Storages == null) return null;
        return reading.Storages.FirstOrDefault(s => Key(s) == key);
    }

    /// <summary>按盘符字母排序（C→D→E…），无盘符的排在最后</summary>
    public static IEnumerable<StorageReading> OrderForDisplay(IEnumerable<StorageReading> storages)
        => storages
            .OrderBy(DriveLetter)
            .ThenBy(s => s.Index < 0 ? int.MaxValue : s.Index)
            .ThenBy(Short);

    private static char DriveLetter(StorageReading storage)
    {
        foreach (string volume in storage.Volumes)
        {
            string text = volume.Trim();
            if (text.Length > 0 && char.IsLetter(text[0]))
                return char.ToUpperInvariant(text[0]);
        }
        return char.MaxValue;
    }

    /// <summary>一次快照中出现过的全部磁盘（每个键取首个，优先带盘符的样本）</summary>
    public static List<StorageReading> Representatives(HardwareSnapshot snapshot)
    {
        var map = new Dictionary<string, StorageReading>();
        foreach (var reading in snapshot.Readings)
        {
            if (reading.Storages == null) continue;
            foreach (var storage in reading.Storages)
            {
                string key = Key(storage);
                if (!map.TryGetValue(key, out var existing))
                {
                    map[key] = storage;
                }
                else if (existing.Volumes.Count == 0 && storage.Volumes.Count > 0)
                {
                    map[key] = storage;
                }
            }
        }
        return OrderForDisplay(map.Values).ToList();
    }
}
