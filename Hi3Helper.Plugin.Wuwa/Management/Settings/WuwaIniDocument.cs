using System;
using System.Collections.Generic;
using System.IO;

namespace Hi3Helper.Plugin.Wuwa.Management.Settings;

internal sealed class WuwaIniDocument
{
    private readonly List<string> _lines;
    private readonly string _newLine;

    private WuwaIniDocument(string content)
    {
        _newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _lines = [.. content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
    }

    internal static WuwaIniDocument Load(string path) =>
        new(File.Exists(path) ? File.ReadAllText(path) : string.Empty);

    internal string? Get(string section, string key)
    {
        int sectionStart = FindSection(section);
        if (sectionStart < 0) return null;

        for (int i = sectionStart + 1; i < _lines.Count; i++)
        {
            string line = _lines[i].Trim();
            if (line.StartsWith("[", StringComparison.Ordinal)) break;
            if (TryGetKey(line, out string? currentKey, out string? value) &&
                string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    internal void Set(string section, string key, string value)
    {
        int sectionStart = FindSection(section);
        if (sectionStart < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Length != 0) _lines.Add(string.Empty);
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        int insertAt = _lines.Count;
        for (int i = sectionStart + 1; i < _lines.Count; i++)
        {
            string line = _lines[i].Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                insertAt = i;
                break;
            }

            if (TryGetKey(line, out string? currentKey, out _) &&
                string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{key}={value}";
                return;
            }
        }

        _lines.Insert(insertAt, $"{key}={value}");
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join(_newLine, _lines));
    }

    private int FindSection(string section)
    {
        string header = $"[{section}]";
        return _lines.FindIndex(line => string.Equals(line.Trim(), header, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetKey(string line, out string? key, out string? value)
    {
        int separator = line.IndexOf('=');
        if (separator <= 0 || line.StartsWith(';') || line.StartsWith('#'))
        {
            key = null;
            value = null;
            return false;
        }

        key = line[..separator].Trim();
        value = line[(separator + 1)..].Trim();
        return true;
    }
}
