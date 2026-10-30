using BepInEx;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisResoniteWrapper;
using FrooxEngine;
using HarmonyLib;

namespace HideRMLMenu;

[BepInPlugin("markn2000.HideRMLMenu", "HideRMLMenu", "0.1.1")]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID)]
[BepInDependency(BepisResoniteWrapper.PluginMetadata.GUID)]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        if (Engine.Current?.IsReady == true) OnEngineReady();
        else ResoniteHooks.OnEngineReady += OnEngineReady;
    }

    private void OnEngineReady()
    {
        ResoniteHooks.OnEngineReady -= OnEngineReady;
        try
        {
            Log.LogInfo(MenuPatches.Apply(HarmonyInstance)
                ? "RML menu hiding enabled. Individual mod settings are preserved."
                : "No RML menu types found. No patches applied.");
        }
        catch (Exception error)
        {
            HarmonyInstance.UnpatchSelf();
            Log.LogError($"Failed to hide the RML menu. Plugin patches removed: {error}");
        }
    }
}

internal static class MenuPatches
{
    private const string Category = "ResoniteModLoader";
    private static Type[] hiddenTypes = [];

    internal static bool Apply(Harmony harmony)
    {
        hiddenTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name == "ResoniteModLoader")
            .SelectMany(assembly => new[] {
                assembly.GetType("ResoniteModLoader.ModLoaderSettings", false),
                assembly.GetType("ResoniteModLoader.ModSettings", false) })
            .OfType<Type>().Where(type => typeof(SettingComponent).IsAssignableFrom(type)).ToArray();
        if (hiddenTypes.Length == 0) return false;

        var category = AccessTools.DeclaredMethod(typeof(SettingsDataFeed), "GenerateCategory",
            [typeof(SettingCategoryInfo), typeof(IReadOnlyList<string>)])
            ?? throw new MissingMethodException("SettingsDataFeed.GenerateCategory");
        var enumerate = AccessTools.DeclaredMethod(typeof(SettingsDataFeed), nameof(SettingsDataFeed.Enumerate),
            [typeof(IReadOnlyList<string>), typeof(IReadOnlyList<string>), typeof(string), typeof(object)])
            ?? throw new MissingMethodException("SettingsDataFeed.Enumerate");
        harmony.Patch(category, postfix: new HarmonyMethod(typeof(MenuPatches), nameof(AfterCategory)));
        harmony.Patch(enumerate, postfix: new HarmonyMethod(typeof(MenuPatches), nameof(AfterEnumerate)));
        return true;
    }

    private static List<Type> CategoryTypes()
    {
        var types = new List<Type>();
        Settings.GetSettingTypesInCategory(Category, types);
        return types;
    }

    private static void AfterCategory(DataFeedCategory __result)
    {
        if (__result.ItemKey != Category) return;
        if (CategoryTypes().All(hiddenTypes.Contains))
            __result.InitVisible(field => field.Value = false);
    }

    private static void AfterEnumerate(IReadOnlyList<string> path, ref IAsyncEnumerable<DataFeedItem> __result)
    {
        if (path.Count != 1 || path[0] != Category) return;
        var types = CategoryTypes();
        var names = hiddenTypes.Select(type => type.Name)
            .Where(name => !types.Any(type => type.Name == name && !hiddenTypes.Contains(type)))
            .ToHashSet(StringComparer.Ordinal);
        // Match the game's group identifiers, not display labels or prefixes.
        __result = __result.Where(item =>
            !(item is DataFeedGroup && names.Contains(item.ItemKey)) &&
            !(item.GroupingParameters is { Count: > 0 } groups && names.Contains(groups[0])));
    }
}
