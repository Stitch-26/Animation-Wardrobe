using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace AnimationWardrobe;

public enum HotkeyWindowSize
{
    Small,
    Medium,
    Large,
    Wide,
    Tall
}

public enum HotkeyWindowAnchor
{
    TopLeft,
    Center,
    TopRight
}

[Serializable]
public class ModEntry
{
    public string ModName { get; set; } = "";
    public string ButtonName { get; set; } = "";
    public string Emote { get; set; } = "";
    public int Pose { get; set; } = 0;
    public string Category { get; set; } = "";
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;
    public List<ModEntry> TextEntries { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public Dictionary<string, uint> CategoryIcons { get; set; } = new();
    public Dalamud.Game.ClientState.Keys.VirtualKey Hotkey { get; set; } = Dalamud.Game.ClientState.Keys.VirtualKey.F9;
    public HotkeyWindowAnchor HotkeyWindowAnchor { get; set; } = HotkeyWindowAnchor.TopLeft;
    public float HotkeyWindowOpacity { get; set; } = 0.0f;
    public int ColumnsCount { get; set; } = 3;
    public HotkeyWindowSize HotkeyWindowSize { get; set; } = HotkeyWindowSize.Medium;
    public Dictionary<string, int> EmotePoseDelays { get; set; } = new()
    {
        { "groundsit", 1000 },
        { "sit", 1000 },
        { "doze", 1000 }
    };

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
