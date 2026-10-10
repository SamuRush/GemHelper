namespace Gem.UI;

public sealed class AudioDeviceItem
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;

    public AudioDeviceItem() { }

    public AudioDeviceItem(int index, string name)
    {
        Index = index;
        Name = name;
    }

    public override string ToString() => $"[{Index}] {Name}";
}

public sealed class SteamGameEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public SteamGameEntry() { }

    public SteamGameEntry(string key, string value)
    {
        Key = key;
        Value = value;
    }
}
