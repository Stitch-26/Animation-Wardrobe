using System;
using System.Numerics;
using AnimationWardrobe;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AnimationWardrobe.Windows;

public class QuickMenuWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public QuickMenuWindow(Plugin plugin) : base("Quick Menu###QuickMenuWindow")
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
        var commands = plugin.Configuration.QuickCommands;
        if (commands.Count == 0)
        {
            ImGui.Text("No quick commands. Add some in the Commands tab.");
            return;
        }

        var columnsCount = Math.Max(1, plugin.Configuration.ColumnsCount);
        var childFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground;

        using (var scrollArea = ImRaii.Child("QuickMenuScrollArea", new Vector2(-1, -1), false, childFlags))
        {
            if (!scrollArea) return;

            if (ImGui.BeginTable("QuickMenuTable", columnsCount, ImGuiTableFlags.Resizable | ImGuiTableFlags.NoSavedSettings))
            {
                for (int i = 0; i < commands.Count; i++)
                {
                    if (i % columnsCount == 0)
                        ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(i % columnsCount);
                    var entry = commands[i];
                    if (entry.IconId != 0)
                    {
                        var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(entry.IconId));
                        var wrap = texture.GetWrapOrDefault();
                        if (wrap != null)
                        {
                            ImGui.Image(wrap.Handle, new Vector2(24, 24));
                            ImGui.SameLine();
                        }
                    }
                    var label = string.IsNullOrWhiteSpace(entry.Name) ? entry.Command : entry.Name;
                    if (ImGui.Button($"{label}##quick{i}", new Vector2(-1, 0)))
                    {
                        Plugin.SendChatMessage(entry.Command);
                        IsOpen = false;
                    }
                }
                ImGui.EndTable();
            }
        }
    }
}
