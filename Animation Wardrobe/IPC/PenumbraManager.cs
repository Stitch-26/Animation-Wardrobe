using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;

namespace AnimationWardrobe.IPC;

public class PenumbraManager
{
    private readonly ICallGateSubscriber<int> _apiVersionSubscriber;
    private readonly ICallGateSubscriber<bool> _getEnabledStateSubscriber;
    private readonly ICallGateSubscriber<Dictionary<string, string>> _getModListSubscriber;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>> _getCollectionsSubscriber;
    private readonly ICallGateSubscriber<byte, (Guid, string)?> _getCollectionSubscriber;
    private readonly ICallGateSubscriber<byte, Guid, bool, bool, (int, (Guid Id, string Name)?)> _setCollectionSubscriber;
    private readonly ICallGateSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)> _getCurrentModSettingsSubscriber;
    private readonly ICallGateSubscriber<Guid, string, string, bool, int> _trySetModSubscriber;
    private readonly ICallGateSubscriber<Guid, string, string, bool, int> _tryInheritModSubscriber;
    private readonly ICallGateSubscriber<string, string, Dictionary<string, object?>> _getChangedItemsSubscriber;
    private readonly ICallGateSubscriber<object> _initializedSubscriber;
    private readonly ICallGateSubscriber<object> _disposedSubscriber;

    public event Action? Initialized;
    public event Action? Disposed;

    public PenumbraManager()
    {
        var pi = Plugin.PluginInterface;
        _apiVersionSubscriber = pi.GetIpcSubscriber<int>("Penumbra.ApiVersion");
        _getEnabledStateSubscriber = pi.GetIpcSubscriber<bool>("Penumbra.GetEnabledState");
        _getModListSubscriber = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        _getCollectionsSubscriber = pi.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections.V5");
        _getCollectionSubscriber = pi.GetIpcSubscriber<byte, (Guid, string)?>("Penumbra.GetCollection");
        _setCollectionSubscriber = pi.GetIpcSubscriber<byte, Guid, bool, bool, (int, (Guid Id, string Name)?)>("Penumbra.SetCollection");
        _getCurrentModSettingsSubscriber = pi.GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>("Penumbra.GetCurrentModSettings.V5");
        _trySetModSubscriber = pi.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5");
        _tryInheritModSubscriber = pi.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TryInheritMod.V5");
        _getChangedItemsSubscriber = pi.GetIpcSubscriber<string, string, Dictionary<string, object?>>("Penumbra.GetChangedItems");
        _initializedSubscriber = pi.GetIpcSubscriber<object>("Penumbra.Initialized");
        _disposedSubscriber = pi.GetIpcSubscriber<object>("Penumbra.Disposed");

        _initializedSubscriber.Subscribe(OnInitialized);
        _disposedSubscriber.Subscribe(OnDisposed);
    }

    private void OnInitialized() => Initialized?.Invoke();
    private void OnDisposed() => Disposed?.Invoke();

    public bool IsAvailable()
    {
        try
        {
            var version = _apiVersionSubscriber.InvokeFunc();
            var enabled = _getEnabledStateSubscriber.InvokeFunc();
            if (version < 4 || !enabled)
            {
                Plugin.LogToFile($"PenumbraManager.IsAvailable check: version={version} (required >= 4), enabled={enabled}");
            }
            return version >= 4 && enabled;
        }
        catch (Exception ex)
        {
            // Only log exception once every few seconds to avoid spamming log.txt if Penumbra is missing
            Plugin.Log.Debug($"PenumbraManager.IsAvailable EXCEPTION: {ex.Message}");
            return false;
        }
    }

    public Dictionary<string, string> GetMods()
    {
        try
        {
            return _getModListSubscriber.InvokeFunc();
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Could not get mod list: {ex.Message}");
            return new();
        }
    }

    public Dictionary<Guid, string> GetCollections()
    {
        try
        {
            var result = _getCollectionsSubscriber.InvokeFunc();
            return result ?? new();
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Could not get collections: {ex.Message}");
            return new();
        }
    }

    public (Guid Id, string Name) GetCurrentCollection()
    {
        try
        {
            var res = _getCollectionSubscriber.InvokeFunc(0); // 0 = Yourself
            return res ?? (Guid.Empty, "None");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Could not get current collection: {ex.Message}");
            return (Guid.Empty, "None");
        }
    }

    public void SetCollection(Guid collectionId)
    {
        try
        {
            // 0 = Your character, but here we use ApiCollectionType.Current = 0
            // The subscriber is <byte type, Guid id, bool allowCreate, bool allowDelete, (int, (Guid, string)?)>
            var result = _setCollectionSubscriber.InvokeFunc(0, collectionId, true, true);
            Plugin.LogToFile($"PenumbraManager.SetCollection: set to {collectionId}, result={result.Item1}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not set collection: {ex.Message}");
            Plugin.LogToFile($"PenumbraManager.SetCollection EXCEPTION: {ex.Message}");
        }
    }

    public void SetMod(Guid collectionId, string modPath, string modName, bool enabled)
    {
        try
        {
            var ec = _trySetModSubscriber.InvokeFunc(collectionId, modPath, modName, enabled);
            Plugin.LogToFile($"PenumbraManager.SetMod: {modName} in {collectionId} to {enabled}, result={ec}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not set mod state: {ex.Message}");
            Plugin.LogToFile($"PenumbraManager.SetMod EXCEPTION: {ex.Message}");
        }
    }

    public bool HandleModState(int settingState, Guid collectionId, string modPath, string modName)
    {
        try
        {
            switch (settingState)
            {
                case 0: // Enable
                    return _trySetModSubscriber.InvokeFunc(collectionId, modPath, modName, true) == 0;

                case 1: // Disable
                    return _trySetModSubscriber.InvokeFunc(collectionId, modPath, modName, false) == 0;

                case 2: // Toggle
                    var (ec, settings) = _getCurrentModSettingsSubscriber.InvokeFunc(collectionId, modPath, modName, false);
                    if (ec != 0 || settings == null) return false;
                    var newState = !settings.Value.Item1; // settings.Value.Enabled
                    return _trySetModSubscriber.InvokeFunc(collectionId, modPath, modName, newState) == 0;

                case 3: // Inherit
                    return _tryInheritModSubscriber.InvokeFunc(collectionId, modPath, modName, true) == 0;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogToFile($"PenumbraManager.HandleModState EXCEPTION: {ex.Message}");
        }
        return false;
    }

    public Dictionary<string, object?> GetChangedItems(string modDirectory, string modName)
    {
        try
        {
            return _getChangedItemsSubscriber.InvokeFunc(modDirectory, modName);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Could not get changed items for {modName}: {ex.Message}");
            return new();
        }
    }

    public void Dispose()
    {
        _initializedSubscriber.Unsubscribe(OnInitialized);
        _disposedSubscriber.Unsubscribe(OnDisposed);
    }

    public void RunOnFramework(Action action)
    {
        Plugin.Framework.RunOnFrameworkThread(action);
    }
}

