using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FrooxEngine;
using HarmonyLib;
using HideRMLMenu;

internal static class Program
{
    private static int checks;

    private static async Task Main(string[] args)
    {
        string game = args.ElementAtOrDefault(0) ?? @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";
        string profile = args.ElementAtOrDefault(1) ?? game;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            // Resolve dependencies in place without loading RML.
            string? file = new[] { Path.Combine(profile, "BepInEx", "core"), game }
                .Select(dir => Path.Combine(dir, name.Name + ".dll")).FirstOrDefault(File.Exists)
                ?? Directory.EnumerateFiles(Path.Combine(profile, "BepInEx", "plugins"), name.Name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
            return file == null ? null : context.LoadFromAssemblyPath(file);
        };
        await Run(game);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Run(string game)
    {
        InspectMetadata(game);
        Check(!((Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin))).Unload(), "The base class keeps runtime unloading disabled");
        var harmony = new Harmony("markn2000.HideRMLMenu.Checks");
        Check(!MenuPatches.Apply(harmony), "Skip patching without RML");
        Check(!harmony.GetPatchedMethods().Any(), "No patches without RML");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "ResoniteModLoader"), "Do not load RML automatically");

        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ResoniteModLoader"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("TestRML");
        Check(!MenuPatches.Apply(harmony), "Skip forks without menu types");
        Check(!harmony.GetPatchedMethods().Any(), "No patches for forks without menu types");
        var loader = DefineSetting(module, "ResoniteModLoader.ModLoaderSettings");
        var debug = DefineSetting(module, "ResoniteModLoader.ModSettings");
        var foreignModule = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ForeignMod"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("OtherMod");
        var collision = DefineSetting(foreignModule, "Other.ModLoaderSettings");
        Check(MenuPatches.Apply(harmony), "Apply patches when RML settings types exist");
        Check(harmony.GetPatchedMethods().Count() == 2, "Patch only enumeration and category creation");

        var types = new Dictionary<string, HashSet<Type>> {
            ["ResoniteModLoader"] = [loader, debug], ["BepInEx"] = [typeof(ForeignSetting)],
            ["Other"] = [typeof(ForeignSetting)] };
        AccessTools.Field(typeof(Settings), "_typesByCategory").SetValue(null, types);
        var categories = types.Keys.ToDictionary(key => key, key => { var info = new SettingCategoryInfo(null!, 0); info.InitKey(key); return info; });
        AccessTools.Field(typeof(Settings), "_categoryInfos").SetValue(null, categories);
        var feed = (SettingsDataFeed)RuntimeHelpers.GetUninitializedObject(typeof(SettingsDataFeed));

        for (int pass = 0; pass < 2; pass++)
        {
            var root = await feed.Enumerate([], [], "", null!).ToListAsync();
            Check(!Visible(root.Single(item => item.ItemKey == "ResoniteModLoader")), "Hide the RML category on each enumeration");
            Check(root.Where(item => item.ItemKey != "ResoniteModLoader").All(Visible), "Keep other categories visible");
            Check((await feed.Enumerate(["ResoniteModLoader"], [], "", null!).ToListAsync()).Count == 0, "Exclude RML groups and fields");
        }

        // Check category notifications through the same generation method.
        AccessTools.Field(typeof(SettingsDataFeed), "_categoryListeners").SetValue(feed,
            new Dictionary<DataFeedUpdateHandler, Action<SettingCategoryInfo>>());
        DataFeedItem? added = null;
        DataFeedUpdateHandler handler = (item, _) => added = item;
        feed.ListenToUpdates([], [], "", handler, null!);
        var notify = (Action<SettingCategoryInfo>)AccessTools.Field(typeof(Settings), "SettingCategoryAdded").GetValue(null)!;
        notify(categories["ResoniteModLoader"]);
        Check(added != null && !Visible(added), "Hide RML in category-added notifications");
        feed.UnregisterListener([], [], "", handler);
        Check(AccessTools.Field(typeof(Settings), "SettingCategoryAdded").GetValue(null) == null, "Listener removal still works");

        types["ResoniteModLoader"].Add(typeof(ForeignSetting));
        var mixedRoot = await feed.Enumerate([], [], "", null!).ToListAsync();
        Check(Visible(mixedRoot.Single(item => item.ItemKey == "ResoniteModLoader")), "Keep the category when another mod uses it");
        var mixed = await feed.Enumerate(["ResoniteModLoader"], [], "search", null!).ToListAsync();
        Check(mixed.Select(item => item.ItemKey).SequenceEqual(new[] { "ForeignSetting", "ForeignSetting.Enabled" }), "Keep only the other mod's group and field");
        var untouched = await feed.Enumerate(["BepInEx"], [], "", null!).ToListAsync();
        Check(untouched.Select(item => item.ItemKey).SequenceEqual(mixed.Select(item => item.ItemKey)), "Preserve Bepis mod settings");

        types["ResoniteModLoader"].Add(collision);
        var ambiguous = await feed.Enumerate(["ResoniteModLoader"], [], "", null!).ToListAsync();
        Check(ambiguous.Count(item => item.ItemKey == "ModLoaderSettings") == 2, "Keep both groups when type names collide");
        Check(ambiguous.Count(item => item.ItemKey == "ModLoaderSettings.Enabled") == 2, "Keep fields when type names collide");
        Check(types["ResoniteModLoader"].SetEquals([loader, debug, typeof(ForeignSetting), collision]), "Do not change registered settings types");

        // Check both patch orders with another mod's replacement Postfix.
        var otherHarmony = new Harmony("HideRMLMenu.Checks.OtherMod");
        var enumerate = AccessTools.DeclaredMethod(typeof(SettingsDataFeed), nameof(SettingsDataFeed.Enumerate));
        foreach (int priority in new[] { HarmonyLib.Priority.First, HarmonyLib.Priority.Last })
        {
            otherHarmony.Patch(enumerate, postfix: new HarmonyMethod(typeof(Program), nameof(OtherPostfix)) { priority = priority });
            var items = await feed.Enumerate(["BepInEx"], [], "", null!).ToListAsync();
            Check(items.Count == 1 && items[0].ItemKey == "OtherModReplacement", "Preserve the replacement Bepis enumeration");
            otherHarmony.UnpatchSelf();
        }

        harmony.UnpatchSelf();
        Check(!harmony.GetPatchedMethods().Any(), "Remove this plugin's patches");
        var restored = await feed.Enumerate(["ResoniteModLoader"], [], "", null!).ToListAsync();
        Check(restored.Any(item => item.ItemKey == "ModSettings.Enabled"), "Restore RML settings after unpatching");
        Check((await feed.Enumerate([], [], "", null!).ToListAsync()).All(Visible), "Restore all categories after unpatching");
        Check(Engine.Current == null, "Do not start the engine during checks");
        Console.WriteLine($"Passed {checks} standalone checks. No game session was started.");
    }

    private static Type DefineSetting(ModuleBuilder module, string name)
    {
        var type = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Abstract, typeof(SettingComponent));
        var field = type.DefineField("Enabled", typeof(Sync<bool>), FieldAttributes.Public);
        var attribute = typeof(ForeignSetting).GetField(nameof(ForeignSetting.Enabled))!.GetCustomAttributesData()
            .Single(data => data.AttributeType == typeof(SettingPropertyAttribute));
        field.SetCustomAttribute(new CustomAttributeBuilder(attribute.Constructor, attribute.ConstructorArguments.Select(arg => arg.Value).ToArray()));
        return type.CreateType()!;
    }

    private static bool Visible(DataFeedItem item)
    {
        var field = DispatchProxy.Create<IField<bool>, BoolField>();
        ((BoolField)(object)field).Value = true;
        item.SetupVisible?.Invoke(field);
        return ((BoolField)(object)field).Value;
    }

    private static void OtherPostfix(IReadOnlyList<string> path, ref IAsyncEnumerable<DataFeedItem> __result)
    {
        if (path.Count == 1 && path[0] == "BepInEx")
            __result = new DataFeedItem[] { new DataFeedLabel("OtherModReplacement", path, [], "Test") }.ToAsyncEnumerable();
    }

    private static void InspectMetadata(string game)
    {
        using var engine = Mono.Cecil.ModuleDefinition.ReadModule(Path.Combine(game, "FrooxEngine.dll"));
        var methods = AllTypes(engine.Types).SelectMany(type => type.Methods).Where(method => method.HasBody).ToList();
        foreach (string name in new[] { "GenerateCategory", "Enumerate" })
        {
            var callers = methods.Where(method => method.Body.Instructions.Any(instruction => instruction.Operand is Mono.Cecil.MethodReference called
                && (called.DeclaringType.FullName == "FrooxEngine.SettingsDataFeed"
                    || (name == "Enumerate" && called.DeclaringType.FullName == "FrooxEngine.IDataFeed"))
                && called.Name == name)).ToList();
            Console.WriteLine($"Callers of {name}: {string.Join(", ", callers.Select(method => method.FullName))}");
            Check(callers.Count > 0, $"Find callers of {name} in the game DLL");
        }
        Check(methods.Any(method => method.Body.Instructions.Any(instruction => instruction.Operand is Mono.Cecil.MethodReference called
            && called.DeclaringType.FullName == "FrooxEngine.DataFeedItem" && called.Name == "get_SetupVisible")), "The game UI uses the visibility callback");
        Console.WriteLine("Visibility callback callers: " + string.Join(", ", methods.Where(method => method.Body.Instructions.Any(instruction =>
            instruction.Operand is Mono.Cecil.MethodReference called && called.DeclaringType.FullName == "FrooxEngine.DataFeedItem"
            && called.Name == "get_SetupVisible")).Select(method => method.FullName)));
        string rmlPath = Path.Combine(game, "Libraries", "ResoniteModLoader.dll");
        if (File.Exists(rmlPath))
        {
            using var rml = Mono.Cecil.ModuleDefinition.ReadModule(rmlPath);
            Check(rml.Assembly.Name.Name == "ResoniteModLoader", "Verify the installed RML assembly name");
            Console.WriteLine($"Installed RML: {rml.Assembly.Name.Version}; menu types: " +
                (rml.Types.Any(type => type.FullName == "ResoniteModLoader.ModLoaderSettings") ? "present" : "absent (for example, a menu-free fork)"));
        }
        using var plugin = Mono.Cecil.ModuleDefinition.ReadModule(typeof(Plugin).Assembly.Location);
        Check(plugin.AssemblyReferences.All(reference => reference.Name != "ResoniteModLoader"), "The plugin has no RML assembly reference");
        Console.WriteLine($"FrooxEngine MVID: {engine.Mvid}");
    }

    private static IEnumerable<Mono.Cecil.TypeDefinition> AllTypes(IEnumerable<Mono.Cecil.TypeDefinition> types) =>
        types.SelectMany(type => new[] { type }.Concat(AllTypes(type.NestedTypes)));

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
        checks++;
    }
}

public abstract class ForeignSetting : SettingComponent
{
    [SettingProperty]
    public new readonly Sync<bool> Enabled = null!;
}

public class BoolField : DispatchProxy
{
    public bool Value;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "set_Value") { Value = (bool)args![0]!; return null; }
        if (method?.Name == "get_Value") return Value;
        throw new NotSupportedException(method?.Name);
    }
}
