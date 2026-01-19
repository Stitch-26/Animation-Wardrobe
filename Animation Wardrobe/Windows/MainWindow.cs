using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace AnimationWardrobe.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string animationImagePath;
    private readonly Plugin plugin;
    private string newEntryText = "";
    private string newButtonName = "";
    private string newCategory = "";
    private string newCategoryName = "";
    private uint newCategoryIcon = 0;
    private string iconSearchText = "";
    private string emoteSearchText = "";
    private int selectedEntryIndex = -1;
    private int selectedEmoteIndex = 0;
    private int newPose = 0;
    private static readonly string[] PoseEmotes = { "groundsit", "doze", "sit" };

    // Penumbra mod search fields (via IPC)
    private Dictionary<string, string> availableMods = new();
    private List<string> filteredMods = new();
    private Dictionary<Guid, string> penumbraCollections = new();
    private bool penumbraAvailable = false;
    private DateTime lastPenumbraCheck = DateTime.MinValue;

    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin, string animationImagePath)
        : base("Animation Wardrobe##With a hidden ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 450),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        Size = new Vector2(500, 450);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.animationImagePath = animationImagePath;
        this.plugin = plugin;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.SyncAlt,
            Priority = 0,
            Click = _ => UpdatePenumbraData(),
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.Text("Refresh collections and mod list from Penumbra");
                ImGui.EndTooltip();
            }
        });

        InitializePenumbraApi();
    }

    public void Dispose() 
    {
        plugin.PenumbraManager.Initialized -= OnPenumbraInitialized;
        plugin.PenumbraManager.Disposed -= OnPenumbraDisposed;
    }

    public new void Toggle() => IsOpen = !IsOpen;

    public void OpenSettings()
    {
        IsOpen = true;
        switchToSettings = true;
    }

    private void InitializePenumbraApi()
    {
        UpdatePenumbraData();
        
        // Subscribe to events for dynamic updates
        plugin.PenumbraManager.Initialized += OnPenumbraInitialized;
        plugin.PenumbraManager.Disposed += OnPenumbraDisposed;
    }

    private void OnPenumbraInitialized()
    {
        Plugin.Log.Information("Penumbra Initialized event received.");
        UpdatePenumbraData();
    }

    private void OnPenumbraDisposed()
    {
        Plugin.Log.Information("Penumbra Disposed event received.");
        penumbraAvailable = false;
        availableMods.Clear();
        penumbraCollections.Clear();
            }

    private void UpdatePenumbraData()
    {
        Plugin.LogToFile("UpdatePenumbraData called");
        try
        {
            if (plugin.PenumbraManager.IsAvailable())
            {
                penumbraAvailable = true;
                availableMods = plugin.PenumbraManager.GetMods();
                var collections = plugin.PenumbraManager.GetCollections();
                
                // Sort collections by name for better visibility
                penumbraCollections = collections.OrderBy(kvp => kvp.Value).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                
                Plugin.LogToFile($"Successfully loaded {availableMods.Count} mods and {penumbraCollections.Count} collections");
            }
            else
            {
                Plugin.LogToFile("UpdatePenumbraData: PenumbraManager.IsAvailable() returned FALSE");
                penumbraAvailable = false;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogToFile($"UpdatePenumbraData EXCEPTION: {ex.Message}\n{ex.StackTrace}");
            penumbraAvailable = false;
        }
    }

    private void SetModState(ModEntry entry, int state)
    {
        var modDir = availableMods.FirstOrDefault(kvp => kvp.Value == entry.ModName).Key;
        if (string.IsNullOrEmpty(modDir)) return;

        var (collectionId, _) = plugin.PenumbraManager.GetCurrentCollection();

        if (collectionId != Guid.Empty)
        {
            plugin.PenumbraManager.HandleModState(state, collectionId, modDir, entry.ModName);
        }
    }

    private void EnableMod(ModEntry entry) => SetModState(entry, 0);
    private void DisableMod(ModEntry entry) => SetModState(entry, 1);
    private void ToggleMod(ModEntry entry) => SetModState(entry, 2);
    private void InheritMod(ModEntry entry) => SetModState(entry, 3);

    private int FuzzyScore(string search, string target)
    {
        if (string.IsNullOrEmpty(search))
            return int.MaxValue;
        
        search = search.ToLower();
        target = target.ToLower();
        
        int score = 0;
        int searchIdx = 0;
        
        for (int i = 0; i < target.Length && searchIdx < search.Length; i++)
        {
            if (target[i] == search[searchIdx])
            {
                searchIdx++;
                // Bonus for consecutive character matches
                if (searchIdx == 1 || target[i - 1] == ' ' || char.IsUpper(target[i]))
                    score += 10;
                else
                    score += 1;
            }
        }
        
        // If not all characters matched, return high score (worse match)
        if (searchIdx < search.Length)
            return int.MaxValue;
        
        return -score; // Negative so that higher scores sort first
    }

    private void UpdateFilteredMods(string searchText)
    {
        filteredMods.Clear();
        
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return;
        }
        
        var matches = availableMods
            .Where(kvp => FuzzyScore(searchText, kvp.Value) != int.MaxValue)
            .OrderBy(kvp => FuzzyScore(searchText, kvp.Value))
            .Select(kvp => kvp.Value)
            .ToList();
        
        filteredMods = matches;
    }

    private bool switchToSettings = false;
    private bool isListeningForHotkey = false;

    public override void Draw()
    {
        if (isListeningForHotkey)
        {
            foreach (var key in Plugin.KeyState.GetValidVirtualKeys())
            {
                if (key == Dalamud.Game.ClientState.Keys.VirtualKey.LBUTTON || 
                    key == Dalamud.Game.ClientState.Keys.VirtualKey.RBUTTON || 
                    key == Dalamud.Game.ClientState.Keys.VirtualKey.MBUTTON) 
                    continue;

                if (Plugin.KeyState[key])
                {
                    plugin.Configuration.Hotkey = key;
                    plugin.Configuration.Save();
                    isListeningForHotkey = false;
                    break;
                }
            }

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                isListeningForHotkey = false;
            }
        }

        // Periodic check if penumbra is not available
        if (!penumbraAvailable && DateTime.Now - lastPenumbraCheck > TimeSpan.FromSeconds(5))
        {
            lastPenumbraCheck = DateTime.Now;
            UpdatePenumbraData();
        }

        if (ImGui.BeginTabBar("MainTabBar"))
        {
            var modsTabFlags = ImGuiTabItemFlags.None;
            var settingsTabFlags = ImGuiTabItemFlags.None;

            if (switchToSettings)
            {
                settingsTabFlags |= ImGuiTabItemFlags.SetSelected;
                switchToSettings = false;
            }

            if (ImGui.BeginTabItem("Mods", modsTabFlags))
            {
                DrawModsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings", settingsTabFlags))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("About"))
            {
                DrawAboutTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawAboutTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.4f, 0.7f, 1.0f, 1.0f), "Plugin Overview");
        ImGui.Separator();
        ImGui.TextWrapped("Animations at a click of a button by assigning it to a button!");
        ImGui.TextWrapped("Using an animation will enable the configured mod and disable all configured animations with the same emote assignment.");
        ImGui.TextWrapped("This will allow you to have multiple animations without needing to mess with priorities or worrying if it is enabled or disabled.");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.4f, 0.7f, 1.0f, 1.0f), "How to use:");
        ImGui.Separator();
        
        if (ImGui.BeginChild("AboutUsageChild", new Vector2(0, 0), false))
        {
            ImGui.BulletText("Go to the 'Settings' tab to configure your Hotkey.");
            ImGui.BulletText("In the 'Mods' tab, search for a mod from Penumbra and assign it an Emote.");
            ImGui.BulletText("Press your hotkey to open Animation Wardrobe at your cursor and select your animation.");
            ImGui.BulletText("For 'groundsit', 'sit', or 'doze' emotes, a pose index can be assigned to automatically switch to a cpose index.");  
            ImGui.EndChild();
        }
    }

    private bool DrawIconSelector(string label, ref uint iconId)
    {
        bool changed = false;
        string preview = iconId == 0 ? "None" : "";
        
        ImGui.PushID(label);
        
        // Preview image if iconId is not 0
        if (iconId != 0)
        {
            var texture = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(iconId));
            var wrap = texture.GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, new Vector2(24, 24));
                ImGui.SameLine();
            }
        }
        
        ImGui.SetNextItemWidth(iconId == 0 ? 100 : 30);
        if (ImGui.BeginCombo("##iconCombo", preview, ImGuiComboFlags.HeightLarge))
        {
            ImGui.InputTextWithHint("##iconSearch", "Search icons or enter ID...", ref iconSearchText, 100);
            
            if (uint.TryParse(iconSearchText, out uint searchedId))
            {
                if (ImGui.Selectable($"Select ID: {searchedId}", iconId == searchedId))
                {
                    iconId = searchedId;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.Separator();
            }

            if (ImGui.Selectable("None", iconId == 0))
            {
                iconId = 0;
                changed = true;
                ImGui.CloseCurrentPopup();
            }

            bool isSearching = !string.IsNullOrWhiteSpace(iconSearchText) && iconSearchText.Length >= 2;

            if (isSearching)
            {
                ImGui.TextDisabled($"Searching for '{iconSearchText}'...");
                
                // 1. Search Emotes (Pre-loaded names)
                foreach (var kvp in plugin.EmoteIcons)
                {
                    if (kvp.Key.Contains(iconSearchText, StringComparison.OrdinalIgnoreCase))
                    {
                        if (plugin.EmoteMap.TryGetValue(kvp.Key, out uint emoteRowId))
                        {
                            var emote = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRow(emoteRowId);
                            if (emote != null && emote.Value.Icon != 0)
                            {
                                if (DrawIconSelectable(kvp.Key, emote.Value.Icon, ref iconId))
                                {
                                    changed = true;
                                    ImGui.CloseCurrentPopup();
                                }
                            }
                        }
                    }
                }

                // 2. Search Actions
                var actionSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                if (actionSheet != null)
                {
                    int matchCount = 0;
                    foreach (var action in actionSheet)
                    {
                        if (matchCount > 20) break;
                        var name = action.Name.ToString();
                        if (name.Contains(iconSearchText, StringComparison.OrdinalIgnoreCase))
                        {
                            if (action.Icon != 0)
                            {
                                if (DrawIconSelectable($"{name} (Action)", action.Icon, ref iconId))
                                {
                                    changed = true;
                                    ImGui.CloseCurrentPopup();
                                }
                                matchCount++;
                            }
                        }
                    }
                }

                // 3. Search Items
                var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                if (itemSheet != null)
                {
                    int matchCount = 0;
                    foreach (var item in itemSheet)
                    {
                        if (matchCount > 20) break;
                        var name = item.Name.ToString();
                        if (name.Contains(iconSearchText, StringComparison.OrdinalIgnoreCase))
                        {
                            if (item.Icon != 0)
                            {
                                if (DrawIconSelectable($"{name} (Item)", item.Icon, ref iconId))
                                {
                                    changed = true;
                                    ImGui.CloseCurrentPopup();
                                }
                                matchCount++;
                            }
                        }
                    }
                }
            }
            else
            {
                ImGui.TextDisabled("Emote Icons (common):");
                int displayCount = 0;
                foreach (var kvp in plugin.EmoteIcons)
                {
                    if (displayCount > 50) break;
                    if (plugin.EmoteMap.TryGetValue(kvp.Key, out uint emoteRowId))
                    {
                        var emote = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRow(emoteRowId);
                        if (emote != null && emote.Value.Icon != 0)
                        {
                            if (DrawIconSelectable(kvp.Key, emote.Value.Icon, ref iconId))
                            {
                                changed = true;
                                ImGui.CloseCurrentPopup();
                            }
                            displayCount++;
                        }
                    }
                }
            }
            ImGui.EndCombo();
        }
        ImGui.PopID();
        return changed;
    }

    private bool DrawIconSelectable(string label, uint currentIconId, ref uint selectedIconId)
    {
        var texture = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(currentIconId));
        var wrap = texture.GetWrapOrDefault();
        if (wrap != null)
        {
            ImGui.Image(wrap.Handle, new Vector2(24, 24));
            ImGui.SameLine();
        }

        if (ImGui.Selectable($"{label}##icon{currentIconId}", selectedIconId == currentIconId))
        {
            selectedIconId = currentIconId;
            return true;
        }
        return false;
    }

    private void DrawSettingsTab()
    {
        if (ImGui.BeginChild("SettingsChild"))
        {
            // --- Hotkey Configuration ---
            if (ImGui.CollapsingHeader("Hotkey Configuration", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Spacing();
                var hotkey = plugin.Configuration.Hotkey;
                var buttonLabel = isListeningForHotkey ? "Press any key... (Click elsewhere to cancel)" : $"{hotkey}###HotkeyButton";

                bool wasListening = isListeningForHotkey;
                if (wasListening)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.1f, 0.1f, 1.0f));
                }

                if (ImGui.BeginTable("HotkeySettingsTable", 2, ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableSetupColumn("Input", ImGuiTableColumnFlags.WidthStretch);

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Activation Hotkey:");
                    ImGuiComponents.HelpMarker("The keyboard key used to toggle the transparent mod window.");
                    
                    ImGui.TableSetColumnIndex(1);
                    if (ImGui.Button(buttonLabel, new Vector2(250, 0)))
                    {
                        isListeningForHotkey = !isListeningForHotkey;
                    }

                    ImGui.EndTable();
                }

                if (wasListening)
                {
                    ImGui.PopStyleColor(3);
                }
                ImGui.Spacing();
            }

            // --- Hotkey Window Appearance ---
            if (ImGui.CollapsingHeader("Hotkey Window Appearance", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Spacing();
                
                if (ImGui.BeginTable("AppearanceSettingsTable", 2, ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableSetupColumn("Input", ImGuiTableColumnFlags.WidthStretch);

                    // Columns
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Grid Columns:");
                    ImGuiComponents.HelpMarker("Number of columns used to organize mod categories in the transparent window.");
                    ImGui.TableSetColumnIndex(1);
                    var columns = plugin.Configuration.ColumnsCount;
                    ImGui.SetNextItemWidth(150);
                    if (ImGui.InputInt("##GridColumns", ref columns))
                    {
                        plugin.Configuration.ColumnsCount = Math.Max(1, columns);
                        plugin.Configuration.Save();
                    }

                    // Preset Size
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Window Size:");
                    ImGuiComponents.HelpMarker("Pre-configured dimensions for the transparent window.");
                    ImGui.TableSetColumnIndex(1);
                    var windowSize = plugin.Configuration.HotkeyWindowSize;
                    ImGui.SetNextItemWidth(150);
                    if (ImGui.BeginCombo("##WindowSize", windowSize.ToString()))
                    {
                        foreach (HotkeyWindowSize val in Enum.GetValues(typeof(HotkeyWindowSize)))
                        {
                            if (ImGui.Selectable(val.ToString(), val == windowSize))
                            {
                                plugin.Configuration.HotkeyWindowSize = val;
                                plugin.Configuration.Save();
                            }
                        }
                        ImGui.EndCombo();
                    }

                    // Anchor Position
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Anchor Position:");
                    ImGuiComponents.HelpMarker("Where the transparent window should be placed relative to your mouse cursor when opened.");
                    ImGui.TableSetColumnIndex(1);
                    var anchor = plugin.Configuration.HotkeyWindowAnchor;
                    ImGui.SetNextItemWidth(150);
                    if (ImGui.BeginCombo("##AnchorPosition", anchor.ToString()))
                    {
                        foreach (HotkeyWindowAnchor val in Enum.GetValues(typeof(HotkeyWindowAnchor)))
                        {
                            if (ImGui.Selectable(val.ToString(), val == anchor))
                            {
                                plugin.Configuration.HotkeyWindowAnchor = val;
                                plugin.Configuration.Save();
                            }
                        }
                        ImGui.EndCombo();
                    }

                    // Opacity
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Background Opacity:");
                    ImGuiComponents.HelpMarker("Transparency level of the window background. 0.0 is fully transparent.");
                    ImGui.TableSetColumnIndex(1);
                    var opacity = plugin.Configuration.HotkeyWindowOpacity;
                    ImGui.SetNextItemWidth(150);
                    if (ImGui.SliderFloat("##BackgroundOpacity", ref opacity, 0.0f, 1.0f))
                    {
                        plugin.Configuration.HotkeyWindowOpacity = opacity;
                        plugin.Configuration.Save();
                    }

                    ImGui.EndTable();
                }
                
                ImGui.Spacing();
            }

            // --- Pose Command Settings ---
            if (ImGui.CollapsingHeader("Pose Command Settings", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Spacing();
                ImGui.Text("Delay after emote before executing /dpose (ms):");
                ImGuiComponents.HelpMarker("The game requires a brief moment after an emote starts before it will accept a pose change command.");
                ImGui.Spacing();

                if (ImGui.BeginTable("PoseSettingsTable", 2, ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableSetupColumn("Input", ImGuiTableColumnFlags.WidthStretch);

                    foreach (var emote in PoseEmotes)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text($"{char.ToUpper(emote[0]) + emote.Substring(1)} Delay:");
                        
                        ImGui.TableSetColumnIndex(1);
                        int delay = plugin.Configuration.EmotePoseDelays.GetValueOrDefault(emote, 1000);
                        ImGui.SetNextItemWidth(250);
                        if (ImGui.SliderInt($"##{emote}Delay", ref delay, 1000, 5000))
                        {
                            plugin.Configuration.EmotePoseDelays[emote] = delay;
                            plugin.Configuration.Save();
                        }
                    }
                    ImGui.EndTable();
                }
                ImGui.Spacing();
            }

            // --- Category Management ---
            if (ImGui.CollapsingHeader("Category Management", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Spacing();
                
                ImGui.InputTextWithHint("##newCategoryName", "New category name...", ref newCategoryName, 50);
                ImGui.SameLine();
                DrawIconSelector("##newCatIcon", ref newCategoryIcon);
                ImGui.SameLine();
                if (ImGui.Button("Add Category"))
                {
                    if (!string.IsNullOrWhiteSpace(newCategoryName) && !plugin.Configuration.Categories.Contains(newCategoryName))
                    {
                        plugin.Configuration.Categories.Add(newCategoryName);
                        if (newCategoryIcon != 0)
                        {
                            plugin.Configuration.CategoryIcons[newCategoryName] = newCategoryIcon;
                        }
                        plugin.Configuration.Save();
                        newCategoryName = "";
                        newCategoryIcon = 0;
                    }
                }
                ImGuiComponents.HelpMarker("Create groups to organize your mods. Each category can have its own icon.");

                if (ImGui.BeginTable("CategoriesTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Category Name", ImGuiTableColumnFlags.WidthStretch, 200);
                    ImGui.TableSetupColumn("Icon", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 40);
                    ImGui.TableHeadersRow();

                    for (int i = 0; i < plugin.Configuration.Categories.Count; i++)
                    {
                        var cat = plugin.Configuration.Categories[i];
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text(cat);
                        
                        ImGui.TableSetColumnIndex(1);
                        uint iconId = 0;
                        plugin.Configuration.CategoryIcons.TryGetValue(cat, out iconId);
                        if (DrawIconSelector($"##catIcon{i}", ref iconId))
                        {
                            if (iconId == 0)
                                plugin.Configuration.CategoryIcons.Remove(cat);
                            else
                                plugin.Configuration.CategoryIcons[cat] = iconId;
                            plugin.Configuration.Save();
                        }

                        ImGui.TableSetColumnIndex(2);
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.0f, 0.0f, 1.0f));
                        
                        var buttonWidth = 25f;
                        var columnWidth = ImGui.GetColumnWidth();
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (columnWidth - buttonWidth) * 0.5f);
                        
                        if (ImGui.Button($"X##delcat{i}", new Vector2(buttonWidth, 0)))
                        {
                            plugin.Configuration.Categories.RemoveAt(i);
                            plugin.Configuration.Save();
                        }
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete Category");
                    }
                    ImGui.EndTable();
                }
                ImGui.Spacing();
            }

            ImGui.EndChild();
        }
    }

    private void DrawModsTab()
    {
        if (!penumbraAvailable)
        {
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Penumbra not detected or IPC not ready.");
            if (ImGui.Button("Retry Connection"))
            {
                UpdatePenumbraData();
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0, 1, 0, 1), $"Penumbra Connected: {availableMods.Count} mods loaded.");
        }
       

        ImGui.Spacing();
        ImGui.Separator();

        // Input field with fuzzy search for mods
        ImGui.SetNextItemWidth(180);
        bool inputChanged = ImGui.InputTextWithHint("##newEntryInput", "Mod Name...", ref newEntryText, 256);
        if (inputChanged)
        {
            UpdateFilteredMods(newEntryText);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        ImGui.InputTextWithHint("##newButtonName", "Button Label (optional)...", ref newButtonName, 100);
        ImGui.SameLine();
        
        // Show mod suggestions dropdown if there are matches and not too many
        if (filteredMods.Count > 0 && filteredMods.Count <= 30 && !string.IsNullOrWhiteSpace(newEntryText))
        {
            ImGui.Spacing();
            var dropdownHeight = Math.Min(500, filteredMods.Count * 25);
            using (var child = ImRaii.Child("ModSuggestions##dropdown", new Vector2(-1, dropdownHeight), true))
            {
                if (child.Success)
                {
                    bool modSelected = false;
                    foreach (var modName in filteredMods)
                    {
                        if (ImGui.Selectable(modName))
                        {
                            newEntryText = modName;
                            modSelected = true;
                            break;
                        }
                    }
                    if (modSelected)
                    {
                        filteredMods.Clear();
                    }
                }
            }
        }
        else if (filteredMods.Count > 30 && !string.IsNullOrWhiteSpace(newEntryText))
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), $"Too many matches ({filteredMods.Count}). Refine your search.");
        }
        
        ImGui.Separator();
        
        // Custom emote dropdown with icons
        ImGui.SetNextItemWidth(120);
        var selectedEmoteName = selectedEmoteIndex >= 0 && selectedEmoteIndex < plugin.Emotes.Length ? plugin.Emotes[selectedEmoteIndex] : "None";
        var emotePreview = selectedEmoteName;
        if (plugin.EmoteIcons.TryGetValue(selectedEmoteName, out var icon) && icon != null)
        {
            emotePreview = $"{selectedEmoteName}##emotemenu";
        }
        
        if (ImGui.BeginCombo("##emoteDropdown", emotePreview, ImGuiComboFlags.HeightLarge))
        {
            // Emote search input - fixed at the top
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##emoteSearch", "Search emotes...", ref emoteSearchText, 100);
            ImGui.Separator();

            // Scrollable list area
            using (var child = ImRaii.Child("##emoteListChild", new Vector2(0, 250), false))
            {
                if (child.Success)
                {
                    for (int i = 0; i < plugin.Emotes.Length; i++)
                    {
                        var emoteName = plugin.Emotes[i];

                        // Filter based on search text
                        if (!string.IsNullOrWhiteSpace(emoteSearchText) && FuzzyScore(emoteSearchText, emoteName) == int.MaxValue)
                        {
                            continue;
                        }

                        var isSelected = selectedEmoteIndex == i;
                        
                        // Display icon if available
                        if (plugin.EmoteIcons.TryGetValue(emoteName, out var emoteIcon) && emoteIcon != null)
                        {
                            var wrap = emoteIcon.GetWrapOrDefault();
                            if (wrap != null)
                            {
                                ImGui.Image(wrap.Handle, new Vector2(24, 24));
                                ImGui.SameLine();
                            }
                        }
                        
                        if (ImGui.Selectable(emoteName, isSelected))
                        {
                            selectedEmoteIndex = i;
                            emoteSearchText = ""; // Clear search after selection
                        }
                        
                        if (isSelected)
                        {
                            ImGui.SetItemDefaultFocus();
                        }
                    }
                }
            }
            ImGui.EndCombo();
        }
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        var categoryPreview = string.IsNullOrWhiteSpace(newCategory) ? "None" : newCategory;
        if (ImGui.BeginCombo("##newCategory", categoryPreview))
        {
            if (ImGui.Selectable("None", string.IsNullOrWhiteSpace(newCategory)))
            {
                newCategory = "";
            }
            foreach (var cat in plugin.Configuration.Categories)
            {
                if (ImGui.Selectable(cat, newCategory == cat))
                {
                    newCategory = cat;
                }
            }
            ImGui.EndCombo();
        }

        var selectedEmote = selectedEmoteIndex >= 0 && selectedEmoteIndex < plugin.Emotes.Length ? plugin.Emotes[selectedEmoteIndex] : "";
        if (PoseEmotes.Contains(selectedEmote))
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(30);
            ImGui.InputInt("##newPose", ref newPose, 0, 0);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pose Number");
        }

        ImGui.SameLine();
        if (ImGui.Button("+##addEntry", new Vector2(30, 0)))
        {
            if (!string.IsNullOrWhiteSpace(newEntryText))
            {
                var emoteName = selectedEmoteIndex >= 0 && selectedEmoteIndex < plugin.Emotes.Length ? plugin.Emotes[selectedEmoteIndex] : "";
                var entry = new ModEntry
                {
                    ModName = newEntryText,
                    ButtonName = newButtonName,
                    Emote = emoteName,
                    Pose = PoseEmotes.Contains(emoteName) ? newPose : 0,
                    Category = newCategory
                };
                plugin.Configuration.TextEntries.Add(entry);
                plugin.Configuration.Save();

                // Log what emotes this mod actually affects
                var modDir = availableMods.FirstOrDefault(kvp => kvp.Value == newEntryText).Key;
                if (!string.IsNullOrEmpty(modDir))
                {
                    var changedItems = plugin.PenumbraManager.GetChangedItems(modDir, newEntryText);
                    var affectedEmotes = changedItems.Keys
                        .Where(name => plugin.EmoteMap.ContainsKey(name))
                        .ToList();

                    if (affectedEmotes.Count > 0)
                    {
                        Plugin.LogToFile($"New mod entry added: '{newEntryText}' (Label: '{newButtonName}', Category: '{newCategory}', Selected emote: '{emoteName}'). Mod itself affects: {string.Join(", ", affectedEmotes)}");
                    }
                    else
                    {
                        Plugin.LogToFile($"New mod entry added: '{newEntryText}' (Label: '{newButtonName}', Category: '{newCategory}', Selected emote: '{emoteName}'). Mod doesn't seem to affect any known emotes.");
                    }
                }
                else
                {
                    Plugin.LogToFile($"New mod entry added: '{newEntryText}' (Label: '{newButtonName}', Category: '{newCategory}', Selected emote: '{emoteName}'). Could not find mod directory to check affected emotes.");
                }

                newEntryText = "";
                newButtonName = "";
                selectedEmoteIndex = 0;
                newPose = 0;
            }
        }

        ImGui.Spacing();

        // Table of entries with columns: Mod Name | Label | Emote | Pose | Category | Actions
        using (var child = ImRaii.Child("ModsTableContainer", new Vector2(-1, -35), true))
        {
            if (child.Success)
            {
                if (ImGui.BeginTable("ModsTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Hideable | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Mod Name", ImGuiTableColumnFlags.WidthStretch, 150);
                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthStretch, 80);
                    ImGui.TableSetupColumn("Emote", ImGuiTableColumnFlags.WidthStretch, 80);
                    ImGui.TableSetupColumn("Pose", ImGuiTableColumnFlags.WidthFixed, 25);
                    ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthStretch, 80);
                    ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 40);
                    ImGui.TableHeadersRow();

                    var groupedEntries = plugin.Configuration.TextEntries
                        .GroupBy(e => e.Emote)
                        .OrderBy(g => g.Key);

                    foreach (var group in groupedEntries)
                    {
                        var emoteName = string.IsNullOrWhiteSpace(group.Key) ? "No Emote" : group.Key;
                        
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        var isHeaderOpen = ImGui.TreeNodeEx($"{emoteName}##header", ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.DefaultOpen);
                        
                        if (isHeaderOpen)
                        {
                            foreach (var entry in group)
                            {
                                var i = plugin.Configuration.TextEntries.IndexOf(entry);
                                ImGui.TableNextRow();

                                // Mod Name column
                                ImGui.TableSetColumnIndex(0);
                                ImGui.Text($"  {entry.ModName}");

                                // Label column
                                ImGui.TableSetColumnIndex(1);
                                var label = entry.ButtonName;
                                ImGui.SetNextItemWidth(-1);
                                if (ImGui.InputText($"##label{i}", ref label, 100))
                                {
                                    entry.ButtonName = label;
                                    plugin.Configuration.Save();
                                }

                                // Emote column
                                ImGui.TableSetColumnIndex(2);
                                ImGui.Text(entry.Emote);

                                // Pose column
                                ImGui.TableSetColumnIndex(3);
                                if (PoseEmotes.Contains(entry.Emote))
                                {
                                    var pose = entry.Pose;
                                    ImGui.SetNextItemWidth(-1);
                                    if (ImGui.InputInt($"##pose{i}", ref pose, 0, 0))
                                    {
                                        entry.Pose = pose;
                                        plugin.Configuration.Save();
                                    }
                                }

                                // Category column
                                ImGui.TableSetColumnIndex(4);
                                ImGui.SetNextItemWidth(-1);
                                var entryCategoryPreview = string.IsNullOrWhiteSpace(entry.Category) ? "None" : entry.Category;
                                if (ImGui.BeginCombo($"##cat{i}", entryCategoryPreview))
                                {
                                    if (ImGui.Selectable("None", string.IsNullOrWhiteSpace(entry.Category)))
                                    {
                                        entry.Category = "";
                                        plugin.Configuration.Save();
                                    }
                                    foreach (var cat in plugin.Configuration.Categories)
                                    {
                                        if (ImGui.Selectable(cat, entry.Category == cat))
                                        {
                                            entry.Category = cat;
                                            plugin.Configuration.Save();
                                        }
                                    }
                                    ImGui.EndCombo();
                                }

                                // Actions column
                                ImGui.TableSetColumnIndex(5);
                                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.0f, 0.0f, 1.0f));
                                
                                var buttonWidth = 25f;
                                var columnWidth = ImGui.GetColumnWidth();
                                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (columnWidth - buttonWidth) * 0.5f);
                                
                                if (ImGui.Button($"X##delete{i}", new Vector2(buttonWidth, 0)))
                                {
                                    plugin.Configuration.TextEntries.Remove(entry);
                                    plugin.Configuration.Save();
                                }
                                ImGui.PopStyleColor();
                                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete Entry");
                            }
                            ImGui.TreePop();
                        }
                    }

                    ImGui.EndTable();
                }
            }
        }

        ImGui.Spacing();

        // Remove button for selected entry
        if (selectedEntryIndex >= 0 && selectedEntryIndex < plugin.Configuration.TextEntries.Count)
        {
            if (ImGui.Button("Remove Selected", new Vector2(-1, 0)))
            {
                plugin.Configuration.TextEntries.RemoveAt(selectedEntryIndex);
                plugin.Configuration.Save();
                selectedEntryIndex = -1;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }
}
