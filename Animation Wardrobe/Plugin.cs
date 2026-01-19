using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using AnimationWardrobe.Windows;
using AnimationWardrobe.IPC;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnimationWardrobe;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;

    private const string CommandName = "/animwardrobe";
    private const string DPoseCommandName = "/dpose";

    public Configuration Configuration { get; init; } = null!;
    public PenumbraManager PenumbraManager { get; init; } = null!;

    public string[] Emotes { get; private set; } = Array.Empty<string>();
    public Dictionary<string, uint> EmoteMap { get; private set; } = new();
    public Dictionary<string, ISharedImmediateTexture?> EmoteIcons { get; private set; } = new();

    private static IntPtr chatModulePtr = IntPtr.Zero;
    private delegate IntPtr ChatDelegate(IntPtr uiModulePtr, IntPtr message, IntPtr unknown1, byte unknown2);

    public readonly WindowSystem WindowSystem = new("AnimationWardrobe");
    private MainWindow mainWindow { get; init; } = null!;
    private HotkeyWindow hotkeyWindow { get; init; } = null!;

    private class DelayedCommand
    {
        public string Command = "";
        public DateTime ExecutionTime;
    }
    private readonly List<DelayedCommand> delayedCommands = new();

    public Plugin()
    {
        LogToFile("--- Plugin Constructor Start ---");
        
        try
        {
            if (SigScanner.TryScanText("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F2 48 8B F9 45 84 C9", out IntPtr ptr))
            {
                chatModulePtr = ptr;
                LogToFile("Chat module pointer found");
            }
            else
            {
                LogToFile("Warning: Could not find chat module pointer");
            }
        }
        catch (Exception ex)
        {
            LogToFile($"Chat module pointer scan EXCEPTION: {ex.Message}");
        }

        try
        {
            LogToFile($"Config Directory: {PluginInterface.GetPluginConfigDirectory()}");
        }
        catch { }
        // Try to load configuration, but handle migration errors from old format
        try
        {
            Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            LogToFile("Configuration loaded");
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to load configuration, starting fresh: {ex.Message}");
            Configuration = new Configuration();
            LogToFile($"Configuration load EXCEPTION: {ex.Message}");
        }

        try
        {
            LogToFile("Initializing PenumbraManager...");
            PenumbraManager = new PenumbraManager();
            LogToFile("PenumbraManager initialized");
        }
        catch (Exception ex)
        {
            LogToFile($"PenumbraManager initialization EXCEPTION: {ex.Message}");
            Log.Error($"Failed to initialize PenumbraManager: {ex.Message}");
        }

        LoadEmotes();

        // You might normally want to embed resources and load them from the manifest stream
        string animationImagePath = string.Empty;
        try 
        {
            if (PluginInterface.AssemblyLocation.Directory != null)
            {
                animationImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory.FullName, "wardrobe.png");
            }
            else
            {
                LogToFile("Warning: Assembly location directory is NULL");
            }
        }
        catch (Exception ex)
        {
            LogToFile($"Error calculating animation image path: {ex.Message}");
        }
        LogToFile($"Animation image path: {animationImagePath}");

        try
        {
            LogToFile("Creating MainWindow...");
            mainWindow = new MainWindow(this, animationImagePath);
            LogToFile("Creating HotkeyWindow...");
            hotkeyWindow = new HotkeyWindow(this);

            LogToFile("Adding windows to WindowSystem...");
            WindowSystem.AddWindow(mainWindow);
            WindowSystem.AddWindow(hotkeyWindow);
            LogToFile("Windows added successfully");
        }
        catch (Exception ex)
        {
            LogToFile($"WINDOW CREATION EXCEPTION: {ex.Message}\n{ex.StackTrace}");
            Log.Error($"Failed to create windows: {ex.Message}");
        }

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += HandleHotkey;
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        Framework.Update += OnUpdate;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Animation Wardrobe main window."
        });

        CommandManager.AddHandler(DPoseCommandName, new CommandInfo(OnDPoseCommand)
        {
            HelpMessage = "Manage player poses. Usage: /dpose <index>"
        });

        LogToFile("--- Plugin Constructor End ---");
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    private bool hotkeyWasPressed = false;

    public static unsafe bool IsInputTextActive()
    {
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (framework == null) return false;

        var module = framework->GetUIModule();
        if (module == null) return false;

        var atkModule = module->GetRaptureAtkModule();
        if (atkModule == null) return false;

        return atkModule->AtkModule.IsTextInputActive();
    }

    private void HandleHotkey()
    {
        if (IsInputTextActive()) return;

        var hotkey = Configuration.Hotkey;
        if (!KeyState.IsVirtualKeyValid(hotkey)) return;

        var isPressed = KeyState[hotkey];
        if (isPressed && !hotkeyWasPressed)
        {
            if (hotkeyWindow.IsOpen)
            {
                LogToFile($"Hotkey {hotkey} pressed, closing hotkey window");
                hotkeyWindow.IsOpen = false;
            }
            else
            {
                LogToFile($"Hotkey {hotkey} pressed, opening hotkey window at mouse");
                hotkeyWindow.OpenAtMouse();
            }
        }
        hotkeyWasPressed = isPressed;
    }

    public void Dispose()
    {
        LogToFile("--- Plugin Disposing ---");
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= HandleHotkey;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Framework.Update -= OnUpdate;
        
        WindowSystem.RemoveAllWindows();

        mainWindow.Dispose();
        hotkeyWindow.Dispose();

        PenumbraManager.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(DPoseCommandName);
        LogToFile("--- Plugin Disposed ---");
    }

    public static void LogToFile(string message)
    {
        try
        {
            var logMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            Log.Information($"[AnimationWardrobe] {message}");
            
            var configDir = PluginInterface.GetPluginConfigDirectory();
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);
            
            var logPath = Path.Combine(configDir, "log.txt");
            using (var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs))
            {
                sw.AutoFlush = true;
                sw.WriteLine(logMessage);
            }

            // Backup log
            try
            {
                var assemblyDir = PluginInterface.AssemblyLocation.DirectoryName;
                if (!string.IsNullOrEmpty(assemblyDir))
                {
                    var backupLogPath = Path.Combine(assemblyDir, "plugin_log.txt");
                    using (var fs = new FileStream(backupLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs))
                    {
                        sw.AutoFlush = true;
                        sw.WriteLine(logMessage);
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to write to log file: {ex.Message}");
        }
    }

    public static unsafe void SendChatMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        // let dalamud process the command first
        if (CommandManager.ProcessCommand(message))
        {
            return;
        }

        if (chatModulePtr == IntPtr.Zero)
            return;

        // Encode message
        var bytes = Encoding.UTF8.GetBytes(message);
        var text = Marshal.AllocHGlobal(bytes.Length + 30);
        Marshal.Copy(bytes, 0, text, bytes.Length);
        Marshal.WriteByte(text + bytes.Length, 0);
        var length = bytes.Length + 1;

        // Create payload
        var payload = Marshal.AllocHGlobal(400);
        Marshal.WriteInt64(payload, text.ToInt64());
        Marshal.WriteInt64(payload + 0x8, 64);
        Marshal.WriteInt64(payload + 0x10, length);
        Marshal.WriteInt64(payload + 0x18, 0);

        try
        {
            var chatDelegate = Marshal.GetDelegateForFunctionPointer<ChatDelegate>(chatModulePtr);
            chatDelegate.Invoke(GameGui.GetUIModule(), payload, IntPtr.Zero, 0);
        }
        catch (Exception ex)
        {
            Log.Error($"Error sending chat message: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
            Marshal.FreeHGlobal(text);
        }
    }

    private void LoadEmotes()
    {
        try
        {
            var emoteSheet = DataManager.GetExcelSheet<Emote>();
            if (emoteSheet != null)
            {
                var emoteList = new List<string>();
                foreach (var emote in emoteSheet)
                {
                    if (emote.TextCommand.IsValid)
                    {
                        var emoteName = emote.TextCommand.Value.Command.ToString().Trim('/');
                        if (!string.IsNullOrWhiteSpace(emoteName))
                        {
                            emoteList.Add(emoteName);
                            EmoteMap[emoteName] = emote.RowId;
                            
                            // Load emote icon
                            try
                            {
                                var iconId = emote.Icon;
                                if (iconId > 0)
                                {
                                    var texture = TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(iconId));
                                    if (texture != null)
                                    {
                                        EmoteIcons[emoteName] = texture;
                                    }
                                    else
                                    {
                                        EmoteIcons[emoteName] = null;
                                    }
                                }
                                else
                                {
                                    EmoteIcons[emoteName] = null;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"Failed to load icon for emote {emoteName}: {ex.Message}");
                                EmoteIcons[emoteName] = null;
                            }
                        }
                    }
                }
                Emotes = emoteList.OrderBy(e => e).ToArray();
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to load emotes: {ex.Message}");
            Emotes = Array.Empty<string>();
        }
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        mainWindow.Toggle();
    }

    private void OnDPoseCommand(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            Chat.Print($"Current pose index: {GetCurrentPoseIndex()}");
        }
        else if (byte.TryParse(args, out var index))
        {
            ChangePose(index);
        }
        else
        {
            Chat.Print("Command usage: /dpose <index>");
        }
    }

    private unsafe byte GetCurrentPoseIndex()
    {
        var player = ObjectTable.LocalPlayer;
        if (player != null)
        {
            var character = (Character*)player.Address;
            return character->EmoteController.CPoseState;
        }
        return 0;
    }

    private void ChangePose(byte target)
    {
        const int maxTries = 8;
        byte max = 0;
        byte current;
        for (var i = 0; (current = GetCurrentPoseIndex()) != target; i++)
        {
            if (i > maxTries)
            {
                Chat.PrintError($"Failed to change pose index to {target} (max seen: {max})");
                break;
            }
            else if (current > max)
            {
                max = current;
            }
            SendChatMessage("/cpose");
        }
    }
    
    public void ToggleConfigUi() => mainWindow.OpenSettings();
    public void ToggleMainUi() => mainWindow.Toggle();

    public void AddDelayedCommand(string command, int delayMs)
    {
        lock (delayedCommands)
        {
            delayedCommands.Add(new DelayedCommand
            {
                Command = command,
                ExecutionTime = DateTime.Now.AddMilliseconds(delayMs)
            });
        }
    }

    private void OnUpdate(IFramework framework)
    {
        lock (delayedCommands)
        {
            if (delayedCommands.Count == 0) return;

            var now = DateTime.Now;
            for (int i = delayedCommands.Count - 1; i >= 0; i--)
            {
                var cmd = delayedCommands[i];
                if (now >= cmd.ExecutionTime)
                {
                    LogToFile($"Executing delayed command: {cmd.Command}");
                    SendChatMessage(cmd.Command);
                    delayedCommands.RemoveAt(i);
                }
            }
        }
    }
}
