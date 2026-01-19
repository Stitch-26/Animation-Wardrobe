using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace AnimationWardrobe.Windows;

public class HotkeyWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public HotkeyWindow(Plugin plugin) : base("Hotkey Mod Window###HotkeyModWindow")
    {
        this.plugin = plugin;

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings;
        UpdateFlags();
        
        IsOpen = false;
    }

    public void Dispose() { }

    private bool isFirstFrame = false;
    private Vector2 targetPos = Vector2.Zero;

    public void OpenAtMouse()
    {
        UpdateFlags();
        
        // Calculate position immediately to avoid one-frame jump
        var mousePos = ImGui.GetIO().MousePos;
        var anchor = plugin.Configuration.HotkeyWindowAnchor;
        var size = plugin.Configuration.HotkeyWindowSize switch
        {
            HotkeyWindowSize.Small  => new Vector2(300, 200),
            HotkeyWindowSize.Medium => new Vector2(500, 400),
            HotkeyWindowSize.Large  => new Vector2(800, 600),
            HotkeyWindowSize.Wide   => new Vector2(1000, 300),
            HotkeyWindowSize.Tall   => new Vector2(400, 800),
            _                       => new Vector2(500, 400)
        };

        targetPos = mousePos;
        if (anchor == HotkeyWindowAnchor.Center)
        {
            targetPos -= size / 2;
        }
        else if (anchor == HotkeyWindowAnchor.TopRight)
        {
            targetPos.X -= size.X;
        }

        IsOpen = true;
        isFirstFrame = true;
    }

    private void UpdateFlags()
    {
        var opacity = plugin.Configuration.HotkeyWindowOpacity;
        BgAlpha = opacity;
        
        if (opacity <= 0.0f)
        {
            Flags |= ImGuiWindowFlags.NoBackground;
        }
        else
        {
            Flags &= ~ImGuiWindowFlags.NoBackground;
        }
    }

    public override void PreDraw()
    {
        UpdateFlags();

        var size = plugin.Configuration.HotkeyWindowSize switch
        {
            HotkeyWindowSize.Small  => new Vector2(300, 200),
            HotkeyWindowSize.Medium => new Vector2(500, 400),
            HotkeyWindowSize.Large  => new Vector2(800, 600),
            HotkeyWindowSize.Wide   => new Vector2(1000, 300),
            HotkeyWindowSize.Tall   => new Vector2(400, 800),
            _                       => new Vector2(500, 400)
        };
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);

        if (isFirstFrame)
        {
            ImGui.SetNextWindowPos(targetPos);
            isFirstFrame = false;
        }
    }

    public override void Draw()
    {
        if (plugin.Configuration.TextEntries.Count == 0)
        {
            ImGui.Text("No mod entries found.");
            return;
        }

        // Let's redo the Draw logic with categories and columns
        DrawCategorizedEntries();
    }

    private void DrawCategorizedEntries()
    {
        var entries = plugin.Configuration.TextEntries;
        var categories = plugin.Configuration.Categories;
        var columnsCount = plugin.Configuration.ColumnsCount;

        // Always use NoBackground for the scroll area to avoid flickering/double backgrounds
        var childFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground;

        using (var scrollArea = ImRaii.Child("HotkeyScrollArea", new Vector2(-1, -1), false, childFlags))
        {
            if (!scrollArea) return;

            if (ImGui.BeginTable("HotkeyTable", columnsCount, ImGuiTableFlags.Resizable | ImGuiTableFlags.NoSavedSettings))
            {
                // Group entries by category
                var categorized = entries.GroupBy(e => string.IsNullOrWhiteSpace(e.Category) ? "" : e.Category)
                                         .ToDictionary(g => g.Key, g => g.ToList());

                // Create a list of categories to display in order: "" followed by configured categories
                var displayCategories = new List<string> { "" };
                displayCategories.AddRange(categories);

                int catCount = 0;
                foreach (var cat in displayCategories)
                {
                    if (!categorized.TryGetValue(cat, out var groupEntries)) continue;

                    if (catCount % columnsCount == 0)
                    {
                        ImGui.TableNextRow();
                    }

                    ImGui.TableSetColumnIndex(catCount % columnsCount);
                    
                    if (string.IsNullOrEmpty(cat))
                    {
                        ImGui.Spacing();
                        ImGui.Spacing();
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1, 1, 0, 1), $"[ {cat} ]");
                        ImGui.Separator();
                    }

                    foreach (var entry in groupEntries)
                    {
                        var buttonLabel = string.IsNullOrWhiteSpace(entry.ButtonName) ? entry.ModName : entry.ButtonName;
                        
                        // Draw category icon if available, otherwise fallback to emote icon
                        uint catIconId = 0;
                        bool hasCatIcon = !string.IsNullOrWhiteSpace(entry.Category) && 
                                          plugin.Configuration.CategoryIcons.TryGetValue(entry.Category, out catIconId) && 
                                          catIconId != 0;

                        if (hasCatIcon)
                        {
                            var texture = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(catIconId));
                            var wrap = texture.GetWrapOrDefault();
                            if (wrap != null)
                            {
                                ImGui.Image(wrap.Handle, new Vector2(24, 24));
                                ImGui.SameLine();
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(entry.Emote) && plugin.EmoteIcons.TryGetValue(entry.Emote, out var emoteIcon) && emoteIcon != null)
                        {
                            var wrap = emoteIcon.GetWrapOrDefault();
                            if (wrap != null)
                            {
                                ImGui.Image(wrap.Handle, new Vector2(24, 24));
                                ImGui.SameLine();
                            }
                        }

                        if (ImGui.Button($"{buttonLabel}##{entry.GetHashCode()}"))
                        {
                            ExecuteModEntry(entry);
                        }
                    }
                    
                    ImGui.Spacing();
                    ImGui.Spacing();
                    catCount++;
                }
                ImGui.EndTable();
            }
        }
    }

    private void ExecuteModEntry(ModEntry entry)
    {
        var mods = plugin.PenumbraManager.GetMods();
        var currentEmote = entry.Emote;

        // 1. Handle mod states: disable others in the same emote group, enable the current one
        if (!string.IsNullOrWhiteSpace(currentEmote))
        {
            var (collectionId, _) = plugin.PenumbraManager.GetCurrentCollection();
            if (collectionId != Guid.Empty)
            {
                foreach (var otherEntry in plugin.Configuration.TextEntries)
                {
                    if (otherEntry.Emote == currentEmote)
                    {
                        var modPath = mods.FirstOrDefault(kvp => kvp.Value == otherEntry.ModName).Key;
                        if (string.IsNullOrEmpty(modPath)) continue;

                        int state = (otherEntry.ModName == entry.ModName) ? 0 : 1;
                        plugin.PenumbraManager.HandleModState(state, collectionId, modPath, otherEntry.ModName);
                    }
                }
            }
        }
        else
        {
            var modPath = mods.FirstOrDefault(kvp => kvp.Value == entry.ModName).Key;
            if (!string.IsNullOrEmpty(modPath))
            {
                var (collectionId, _) = plugin.PenumbraManager.GetCurrentCollection();
                if (collectionId != Guid.Empty)
                {
                    plugin.PenumbraManager.HandleModState(0, collectionId, modPath, entry.ModName);
                }
            }
        }

        // 2. Perform the emote
        if (!string.IsNullOrWhiteSpace(entry.Emote))
        {
            var cmd = $"/{entry.Emote}";
            Plugin.LogToFile($"HotkeyWindow: Executing emote '{cmd}' via SendChatMessage");
            Plugin.SendChatMessage(cmd);

            if (entry.Pose > 0)
            {
                int delay = plugin.Configuration.EmotePoseDelays.GetValueOrDefault(entry.Emote, 1000);
                plugin.AddDelayedCommand($"/dpose {entry.Pose}", delay);
            }
        }
        IsOpen = false;
    }
}

