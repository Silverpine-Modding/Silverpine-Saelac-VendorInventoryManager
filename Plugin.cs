#nullable enable

using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Silverpine.ModdingTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SilverpineMods.VendorInventoryManager;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(
    "renegadex.silverpine.customitemloader",
    BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid =
        "renegadex.silverpine.vendorinventorymanager";
    public const string PluginName = "Vendor Inventory Manager";
    public const string PluginVersion = "1.4.3";

    internal static ManualLogSource Log = null!;
    internal static string ConfigDirectory = "";
    internal static string StockPath = "";

    private Harmony? harmony;

    private void Awake()
    {
        Log = Logger;
        ConfigDirectory = Path.Combine(
            Paths.ConfigPath,
            "VendorInventoryManager");
        StockPath = Path.Combine(ConfigDirectory, "vendor-stock.json");
        Directory.CreateDirectory(ConfigDirectory);

        VendorStockStore.Load();
        ModdingToolsMenu.RegisterSession(
            PluginGuid + ".editor",
            "Vendor Inventory Manager",
            (_, session) => VendorInventoryEditorUI.Open(session),
            order: 320);
        InventoryModTools.RegisterSession(
            PluginGuid + ".game-editor",
            "Vendor Inventory Manager",
            (_, session) => VendorInventoryEditorUI.Open(session),
            order: 320);
        Logger.LogInfo(
            "Registered Vendor Inventory Manager with the Modding Tools " +
            "main menu and in-game Mods tab.");

        harmony = new Harmony(PluginGuid);
        harmony.PatchAll();
        SerializationManager.OnFinishedLoadingSave += OnFinishedLoadingSave;
    }

    private static void OnFinishedLoadingSave()
    {
        VendorStockService.ApplyAll("save load");
    }

    private void OnDestroy()
    {
        // Silverpine destroys BepInEx's bootstrap plugin host while the game
        // continues running. Harmony patches and static framework callbacks
        // must survive that destruction so daily restocks keep working.
        Logger.LogInfo(
            "Main-menu plugin host destroyed during Silverpine's gameplay " +
            "bootstrap; Vendor Inventory Manager patches and save hook " +
            "remain installed.");
    }
}

internal sealed class VendorStockDocument
{
    public int version = 4;
    public List<VendorStockPack> packs = new();

    // Version 1 compatibility. Normalize moves these rules into Default and
    // clears this field before the document is saved again.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<VendorStockRule>? vendors;
}

internal sealed class VendorStockPack
{
    public string id = "";
    public string name = "";
    public bool enabled = true;
    public List<VendorStockRule> vendors = new();
}

internal sealed class VendorStockRule
{
    public string vendorId = "";
    public List<VendorStockEntry> items = new();
    public List<string> automaticStockExclusions = new();
    public List<VendorRandomStockRule> randomPools = new();
}

internal sealed class VendorStockEntry
{
    public string itemName = "";
    public int quantity = 1;
    public string quality = "Template";
}

internal sealed class VendorRandomStockRule
{
    public string id = "";
    public bool enabled = true;
    public string source = nameof(VendorRandomItemSource.Any);
    public string category = "Any";
    public string pickupable = nameof(VendorPickupableFilter.Any);
    public string quality = "Template";
    public string nameContains = "";
    public string nameExcludes = "";
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? minValue;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? maxValue;
    public int minQuantity = 1;
    public int maxQuantity = 1;
    public float chancePercent = 100f;
    public bool allowDuplicates = true;
}

internal enum VendorRandomItemSource
{
    Any,
    BaseGame,
    Custom
}

internal enum VendorPickupableFilter
{
    Any,
    Pickupable,
    VendorCarryable
}

internal static class VendorStockStore
{
    internal static VendorStockDocument Document { get; private set; } = new();

    internal static void Load()
    {
        try
        {
            if (!File.Exists(Plugin.StockPath))
            {
                Document = new VendorStockDocument();
                Save();
                return;
            }

            VendorStockDocument? loaded =
                JsonConvert.DeserializeObject<VendorStockDocument>(
                    File.ReadAllText(Plugin.StockPath));
            Document = loaded ?? new VendorStockDocument();
            Normalize();
            int rules = Document.packs.Sum(pack => pack.vendors.Count);
            Plugin.Log.LogInfo(
                $"Loaded {Document.packs.Count} vendor stock pack(s), " +
                $"{Document.packs.Count(pack => pack.enabled)} enabled, " +
                $"containing {rules} vendor rule(s).");
        }
        catch (Exception exception)
        {
            Document = new VendorStockDocument();
            Plugin.Log.LogError(
                $"Could not load '{Plugin.StockPath}': {exception}");
        }
    }

    internal static void Save()
    {
        Normalize();
        Directory.CreateDirectory(Plugin.ConfigDirectory);
        string temporary = Plugin.StockPath + ".tmp";
        string json = JsonConvert.SerializeObject(
            Document,
            Formatting.Indented);
        File.WriteAllText(temporary, json);
        File.Copy(temporary, Plugin.StockPath, overwrite: true);
        File.Delete(temporary);
    }

    internal static VendorStockPack CreatePack(string? preferredName = null)
    {
        int sequence = 1;
        string id;
        do
        {
            id = "pack-" + sequence++;
        }
        while (Document.packs.Any(pack => pack.id.Equals(
                   id,
                   StringComparison.OrdinalIgnoreCase)));

        var pack = new VendorStockPack
        {
            id = id,
            name = string.IsNullOrWhiteSpace(preferredName)
                ? "Pack " + (sequence - 1)
                : preferredName.Trim(),
            enabled = true
        };
        Document.packs.Add(pack);
        return pack;
    }

    internal static VendorStockPack? FindPack(string? packId) =>
        string.IsNullOrWhiteSpace(packId)
            ? null
            : Document.packs.FirstOrDefault(candidate =>
                candidate.id.Equals(
                    packId,
                    StringComparison.OrdinalIgnoreCase));

    internal static VendorStockRule GetOrCreate(
        VendorStockPack pack,
        string vendorId)
    {
        VendorStockRule? rule = pack.vendors.FirstOrDefault(candidate =>
            candidate.vendorId.Equals(
                vendorId,
                StringComparison.OrdinalIgnoreCase));
        if (rule != null)
            return rule;

        rule = new VendorStockRule { vendorId = vendorId };
        pack.vendors.Add(rule);
        return rule;
    }

    internal static VendorStockRule? Find(
        VendorStockPack pack,
        string vendorId) =>
        pack.vendors.FirstOrDefault(candidate =>
            candidate.vendorId.Equals(
                vendorId,
                StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<VendorStockRule> GetAllRules() =>
        Document.packs.SelectMany(pack => pack.vendors);

    internal static List<VendorStockEntry> GetEffectiveEntries(
        string vendorId)
    {
        return Document.packs
            .Where(pack => pack.enabled)
            .SelectMany(pack => pack.vendors)
            .Where(rule => rule.vendorId.Equals(
                vendorId,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(rule => rule.items)
            .GroupBy(
                entry => entry.itemName + "\u001f" + entry.quality,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new VendorStockEntry
            {
                itemName = group.First().itemName,
                quantity = group.Max(entry => entry.quantity),
                quality = group.First().quality
            })
            .OrderBy(entry => entry.itemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.quality, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static HashSet<string> GetEffectiveAutomaticStockExclusions(
        string vendorId)
    {
        return Document.packs
            .Where(pack => pack.enabled)
            .SelectMany(pack => pack.vendors)
            .Where(rule => rule.vendorId.Equals(
                vendorId,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(rule => rule.automaticStockExclusions)
            .Where(itemName => !string.IsNullOrWhiteSpace(itemName))
            .Select(itemName => itemName.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static List<VendorRandomStockRule> GetEffectiveRandomPools(
        string vendorId)
    {
        return Document.packs
            .Where(pack => pack.enabled)
            .SelectMany(pack => pack.vendors)
            .Where(rule => rule.vendorId.Equals(
                vendorId,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(rule => rule.randomPools)
            .Where(pool => pool.enabled)
            .ToList();
    }

    internal static VendorRandomStockRule CreateRandomPool(
        VendorStockRule rule)
    {
        int sequence = 1;
        string id;
        do
        {
            id = "random-" + sequence++;
        }
        while (rule.randomPools.Any(pool => pool.id.Equals(
                   id,
                   StringComparison.OrdinalIgnoreCase)));

        var pool = new VendorRandomStockRule { id = id };
        rule.randomPools.Add(pool);
        return pool;
    }

    internal static void Normalize()
    {
        Document.version = 4;
        Document.packs ??= new List<VendorStockPack>();
        Document.packs = Document.packs
            .Where(pack => pack != null)
            .ToList();
        if (Document.vendors != null)
        {
            VendorStockPack defaultPack = Document.packs.FirstOrDefault(pack =>
                string.Equals(
                    pack.id,
                    "default",
                    StringComparison.OrdinalIgnoreCase)) ??
                new VendorStockPack
                {
                    id = "default",
                    name = "Default",
                    enabled = true
                };
            if (!Document.packs.Contains(defaultPack))
                Document.packs.Insert(0, defaultPack);
            defaultPack.vendors ??= new List<VendorStockRule>();
            defaultPack.vendors.AddRange(Document.vendors);
            Document.vendors = null;
        }

        if (Document.packs.Count == 0)
        {
            Document.packs.Add(new VendorStockPack
            {
                id = "default",
                name = "Default",
                enabled = true
            });
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sequence = 1;
        foreach (VendorStockPack pack in Document.packs)
        {
            string requestedId = pack.id?.Trim() ?? "";
            if (requestedId.Length == 0 || !usedIds.Add(requestedId))
            {
                do
                {
                    requestedId = "pack-" + sequence++;
                }
                while (!usedIds.Add(requestedId));
            }

            pack.id = requestedId;
            pack.name = string.IsNullOrWhiteSpace(pack.name)
                ? pack.id
                : pack.name.Trim();
            pack.vendors = NormalizeRules(pack.vendors);
        }
    }

    private static List<VendorStockRule> NormalizeRules(
        List<VendorStockRule>? source)
    {
        source ??= new List<VendorStockRule>();
        var normalized = new List<VendorStockRule>();
        foreach (VendorStockRule? candidate in source)
        {
            if (candidate == null ||
                string.IsNullOrWhiteSpace(candidate.vendorId))
                continue;

            VendorStockRule? rule = normalized.FirstOrDefault(existing =>
                existing.vendorId.Equals(
                    candidate.vendorId.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            if (rule == null)
            {
                rule = new VendorStockRule
                {
                    vendorId = candidate.vendorId.Trim()
                };
                normalized.Add(rule);
            }

            candidate.items ??= new List<VendorStockEntry>();
            foreach (VendorStockEntry? entry in candidate.items)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.itemName))
                    continue;
                string normalizedQuality = NormalizeQuality(entry.quality);
                VendorStockEntry? existing = rule.items.FirstOrDefault(value =>
                    value.itemName.Equals(
                        entry.itemName.Trim(),
                        StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    rule.items.Add(new VendorStockEntry
                    {
                        itemName = entry.itemName.Trim(),
                        quantity = Mathf.Clamp(entry.quantity, 1, 99),
                        quality = normalizedQuality
                    });
                }
                else
                {
                    if (entry.quantity > existing.quantity ||
                        (entry.quantity == existing.quantity &&
                         existing.quality.Equals(
                             "Template",
                             StringComparison.OrdinalIgnoreCase) &&
                         !normalizedQuality.Equals(
                             "Template",
                             StringComparison.OrdinalIgnoreCase)))
                        existing.quality = normalizedQuality;
                    existing.quantity = Mathf.Clamp(
                        Math.Max(existing.quantity, entry.quantity),
                        1,
                        99);
                }
            }

            candidate.automaticStockExclusions ??= new List<string>();
            rule.automaticStockExclusions.AddRange(
                candidate.automaticStockExclusions);

            candidate.randomPools ??= new List<VendorRandomStockRule>();
            rule.randomPools.AddRange(
                candidate.randomPools.Where(pool => pool != null));
        }

        foreach (VendorStockRule rule in normalized)
        {
            rule.items = rule.items
                .OrderBy(value => value.itemName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            rule.automaticStockExclusions = rule.automaticStockExclusions
                .Where(itemName => !string.IsNullOrWhiteSpace(itemName))
                .Select(itemName => itemName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(itemName => itemName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            rule.randomPools = NormalizeRandomPools(rule.randomPools);
        }

        return normalized
            .OrderBy(value => value.vendorId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<VendorRandomStockRule> NormalizeRandomPools(
        List<VendorRandomStockRule>? source)
    {
        source ??= new List<VendorRandomStockRule>();
        var normalized = new List<VendorRandomStockRule>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sequence = 1;
        foreach (VendorRandomStockRule? pool in source)
        {
            if (pool == null)
                continue;

            string requestedId = pool.id?.Trim() ?? "";
            if (requestedId.Length == 0 || !usedIds.Add(requestedId))
            {
                do
                {
                    requestedId = "random-" + sequence++;
                }
                while (!usedIds.Add(requestedId));
            }

            pool.id = requestedId;
            pool.source = NormalizeRandomSource(pool.source);
            if (string.Equals(pool.category, "Any",
                    StringComparison.OrdinalIgnoreCase))
                pool.category = "Any";
            else if (Enum.TryParse(
                         pool.category,
                         true,
                         out ItemCategory category))
                pool.category = category.ToString();
            else
                pool.category = "Any";
            pool.pickupable = NormalizeChoice<VendorPickupableFilter>(
                pool.pickupable,
                nameof(VendorPickupableFilter.Any));
            pool.quality = NormalizeQuality(pool.quality);
            pool.nameContains = pool.nameContains?.Trim() ?? "";
            pool.nameExcludes = pool.nameExcludes?.Trim() ?? "";
            if (pool.minValue < 0)
                pool.minValue = 0;
            if (pool.maxValue < 0)
                pool.maxValue = 0;
            if (pool.minValue.HasValue && pool.maxValue.HasValue &&
                pool.maxValue.Value < pool.minValue.Value)
                pool.maxValue = pool.minValue;
            pool.minQuantity = Mathf.Clamp(pool.minQuantity, 0, 99);
            pool.maxQuantity = Mathf.Clamp(
                Math.Max(pool.maxQuantity, pool.minQuantity),
                0,
                99);
            pool.chancePercent = float.IsNaN(pool.chancePercent) ||
                                 float.IsInfinity(pool.chancePercent)
                ? 100f
                : Mathf.Clamp(pool.chancePercent, 0f, 100f);
            normalized.Add(pool);
        }

        return normalized;
    }

    private static string NormalizeChoice<T>(string? value, string fallback)
        where T : struct
    {
        return Enum.TryParse(value, true, out T parsed)
            ? parsed.ToString()!
            : fallback;
    }

    private static string NormalizeRandomSource(string? value)
    {
        if (Enum.TryParse(
                value,
                true,
                out VendorRandomItemSource source))
            return source.ToString();

        const string packPrefix = "Pack:";
        if (value?.StartsWith(
                packPrefix,
                StringComparison.OrdinalIgnoreCase) == true)
        {
            string packId = value.Substring(packPrefix.Length).Trim();
            if (packId.Length > 0)
                return packPrefix + packId;
        }

        return nameof(VendorRandomItemSource.Any);
    }

    internal static string NormalizeQuality(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("Template", StringComparison.OrdinalIgnoreCase))
            return "Template";
        return Enum.TryParse(value, true, out Quality quality)
            ? quality.ToString()
            : "Template";
    }
}

internal sealed class VendorDescriptor
{
    internal string Id = "";
    internal string Name = "";
    internal bool IsCustom;
    internal bool IsAvailable;
    internal string Detail = "";
}

internal static class VendorCatalog
{
    private static readonly NPCName[] KnownBaseVendors =
    {
        NPCName.Aldric,
        NPCName.Gareth,
        NPCName.Hayden,
        NPCName.Rosalyn
    };

    internal static List<VendorDescriptor> Discover()
    {
        var results = new Dictionary<string, VendorDescriptor>(
            StringComparer.OrdinalIgnoreCase);

        foreach (NPCName npcName in KnownBaseVendors)
            AddOrUpdate(
                results,
                "base:" + npcName,
                npcName.ToString(),
                isCustom: false,
                isAvailable: IsBaseVendorAvailable(npcName),
                "Base-game vendor");

        foreach (NeuralNPC npc in
                 Resources.FindObjectsOfTypeAll<NeuralNPC>())
        {
            if (npc == null || npc.GetComponent<Vendor>() == null)
                continue;
            CustomNPCHandler? handler = npc.GetComponent<CustomNPCHandler>();
            if (handler != null &&
                !string.IsNullOrWhiteSpace(handler.customContentName) &&
                IsVendorCustomNpc(handler.customContentName))
            {
                AddOrUpdate(
                    results,
                    "custom:" + handler.customContentName,
                    handler.customContentName,
                    isCustom: true,
                    isAvailable: true,
                    "Custom character with a live Vendor component");
            }
            else if (npc.npcName != NPCName.None &&
                     !IsCustomSlot(npc.npcName))
            {
                AddOrUpdate(
                    results,
                    "base:" + npc.npcName,
                    npc.GetFinalName(),
                    isCustom: false,
                    isAvailable: true,
                    "Base-game vendor");
            }
        }

        foreach (CustomContentDefinition_NPC definition in
                 CustomContentDefinition_NPC.loaded.Values)
        {
            if (definition == null ||
                !definition.enabled ||
                !HasVendorRoutine(definition))
                continue;

            if (definition.overrideExisting &&
                Enum.TryParse(
                    definition.customContentName,
                    ignoreCase: true,
                    out NPCName baseNpc))
            {
                AddOrUpdate(
                    results,
                    "base:" + baseNpc,
                    definition.customContentName,
                    isCustom: false,
                    isAvailable: IsBaseVendorAvailable(baseNpc),
                    "Base character with a vendor-flagged custom routine");
            }
            else if (!definition.overrideExisting)
            {
                bool live = CustomNPCHandler.customNPCHandlers.Any(handler =>
                    handler != null &&
                    handler.customContentName.Equals(
                        definition.customContentName,
                        StringComparison.OrdinalIgnoreCase));
                AddOrUpdate(
                    results,
                    "custom:" + definition.customContentName,
                    definition.customContentName,
                    isCustom: true,
                    isAvailable: live,
                    "Custom character with a vendor-flagged routine");
            }
        }

        foreach (VendorStockRule configured in
                 VendorStockStore.GetAllRules())
        {
            if (results.ContainsKey(configured.vendorId))
                continue;
            bool custom = configured.vendorId.StartsWith(
                "custom:",
                StringComparison.OrdinalIgnoreCase);
            string name = configured.vendorId.Contains(":")
                ? configured.vendorId.Substring(
                    configured.vendorId.IndexOf(':') + 1)
                : configured.vendorId;
            AddOrUpdate(
                results,
                configured.vendorId,
                name,
                custom,
                isAvailable: false,
                "Configured vendor is not currently discoverable");
        }

        return results.Values
            .OrderBy(value => value.IsCustom)
            .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool HasVendorRoutine(
        CustomContentDefinition_NPC definition) =>
        definition.possibleRoutines != null &&
        definition.possibleRoutines.Any(routine =>
            routine != null &&
            routine.routineArguments != null &&
            routine.routineArguments.Any(argument =>
                argument is RoutineArgument_BeVendor));

    internal static bool IsVendorCustomNpc(string customContentName)
    {
        if (string.IsNullOrWhiteSpace(customContentName))
            return false;

        CustomContentDefinition_NPC? definition = null;
        if (!CustomContentDefinition_NPC.loaded.TryGetValue(
                customContentName,
                out definition))
        {
            definition = CustomContentDefinition_NPC.loaded.Values
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    candidate.customContentName.Equals(
                        customContentName,
                        StringComparison.OrdinalIgnoreCase));
        }

        return definition != null &&
               definition.enabled &&
               !definition.overrideExisting &&
               HasVendorRoutine(definition);
    }

    private static bool IsBaseVendorAvailable(NPCName npcName) =>
        NeuralNPC.neuralNPCs.TryGetValue(npcName, out NeuralNPC npc) &&
        npc != null &&
        npc.GetComponent<Vendor>() != null;

    private static bool IsCustomSlot(NPCName npcName) =>
        (int)npcName >= (int)NPCName.CustomNPCSlot1;

    private static void AddOrUpdate(
        IDictionary<string, VendorDescriptor> results,
        string id,
        string name,
        bool isCustom,
        bool isAvailable,
        string detail)
    {
        if (!results.TryGetValue(id, out VendorDescriptor descriptor))
        {
            descriptor = new VendorDescriptor
            {
                Id = id,
                Name = name,
                IsCustom = isCustom,
                IsAvailable = isAvailable,
                Detail = detail
            };
            results.Add(id, descriptor);
            return;
        }

        descriptor.IsAvailable |= isAvailable;
        if (isAvailable || string.IsNullOrWhiteSpace(descriptor.Detail))
            descriptor.Detail = detail;
    }
}

internal static class VendorStockService
{
    private static readonly HashSet<string> MissingItemsLogged =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PredicateFailuresLogged =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnsupportedQualityLogged =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class RequestedStock
    {
        internal RequestedStock(Item template, Quality? quality)
        {
            Template = template;
            Quality = quality;
        }

        internal Item Template { get; }
        internal Quality? Quality { get; }
    }

    private sealed class RandomPoolRoll
    {
        internal VendorRandomStockRule Pool = null!;
        internal List<Item> Candidates = null!;
        internal Quality? Quality;
        internal int Target;
        internal int AllocationIndex;
    }

    [ThreadStatic]
    private static string? automaticRestockVendorId;
    [ThreadStatic]
    private static HashSet<string>? automaticRestockExclusions;

    internal static void ApplyAll(
        string reason,
        bool rollRandomForCustomVendors = false,
        bool rollRandomForAllVendors = false)
    {
        int vendors = 0;
        int items = 0;
        foreach (Vendor vendor in
                 UnityEngine.Object.FindObjectsOfType<Vendor>(
                     includeInactive: true))
        {
            if (vendor == null)
                continue;
            bool rollRandom = rollRandomForAllVendors;
            if (!rollRandom && rollRandomForCustomVendors)
            {
                NeuralNPC? npc = vendor.GetComponent<NeuralNPC>();
                string? vendorId = npc == null ? null : GetVendorId(npc);
                rollRandom = vendorId?.StartsWith(
                    "custom:",
                    StringComparison.OrdinalIgnoreCase) == true;
            }

            int added = Apply(
                vendor,
                reason,
                removeExcludedAutomaticStock: false,
                rollRandomStock: rollRandom);
            if (added <= 0)
                continue;
            vendors++;
            items += added;
        }

        if (items > 0)
            Plugin.Log.LogInfo(
                $"Added {items} configured stock item(s) to " +
                $"{vendors} vendor(s) after {reason}.");
    }

    internal static int Apply(
        Vendor vendor,
        string reason,
        bool removeExcludedAutomaticStock = false,
        bool rollRandomStock = false)
    {
        if (vendor == null)
            return 0;
        NeuralNPC? npc = vendor.GetComponent<NeuralNPC>();
        WorldInventory? worldInventory = vendor.GetComponent<WorldInventory>();
        if (npc == null || worldInventory == null)
            return 0;

        string? vendorId = GetVendorId(npc);
        if (vendorId == null)
            return 0;
        List<VendorStockEntry> effectiveStock =
            VendorStockStore.GetEffectiveEntries(vendorId);
        HashSet<string> exclusions =
            VendorStockStore.GetEffectiveAutomaticStockExclusions(vendorId);
        List<VendorRandomStockRule> randomPools = rollRandomStock
            ? VendorStockStore.GetEffectiveRandomPools(vendorId)
            : new List<VendorRandomStockRule>();
        if (effectiveStock.Count == 0 &&
            (!removeExcludedAutomaticStock || exclusions.Count == 0) &&
            randomPools.Count == 0)
            return 0;

        Inventory inventory = worldInventory.inventory;
        int removed = removeExcludedAutomaticStock
            ? RemoveExcludedAutomaticStock(inventory, exclusions, vendorId)
            : 0;
        var requestedStock = new List<RequestedStock>();
        CollectFixedStock(
            inventory,
            vendorId,
            effectiveStock,
            requestedStock);
        CollectRandomStock(
            inventory,
            vendorId,
            effectiveStock,
            randomPools,
            exclusions,
            requestedStock,
            reason);

        bool originalAllowEncumbered = inventory.allowEncumbered;
        int originalMaxItems = inventory.maxItems;
        inventory.allowEncumbered = true;
        if (originalMaxItems < inventory.items.Count + requestedStock.Count)
            inventory.maxItems = inventory.items.Count + requestedStock.Count;

        int added = 0;
        try
        {
            foreach (RequestedStock request in requestedStock)
            {
                Item template = request.Template;
                Item stock;
                try
                {
                    stock = template.DeepClone();
                }
                catch (Exception exception)
                {
                    Plugin.Log.LogWarning(
                        $"Could not clone '{template.name}' for " +
                        $"'{vendorId}' after {reason}: {exception.Message}");
                    continue;
                }

                if (request.Quality.HasValue)
                    stock.SetQuality(request.Quality.Value);
                stock.RemoveItemComponentsOfType<ItemComponent_VendorItem>();
                stock.AddItemComponent(
                    new ItemComponent_VendorItem(npc.npcName));
                if (!inventory.TryAddItem(stock))
                {
                    Plugin.Log.LogWarning(
                        $"Could not add '{template.name}' to " +
                        $"'{vendorId}' after {reason}.");
                    continue;
                }
                added++;
            }
        }
        finally
        {
            inventory.allowEncumbered = originalAllowEncumbered;
            inventory.maxItems = originalMaxItems;
            inventory.UpdateEncumbered();
        }

        if (added > 0 || removed > 0)
            inventory.Sort();
        if (reason.Equals(
                "daily restock",
                StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.LogInfo(
                $"Processed daily configured stock for '{vendorId}': " +
                $"{effectiveStock.Count} fixed entr" +
                $"{(effectiveStock.Count == 1 ? "y" : "ies")}, " +
                $"{randomPools.Count} enabled random pool" +
                $"{(randomPools.Count == 1 ? "" : "s")}, " +
                $"{added} item(s) added, {removed} excluded automatic " +
                "item(s) removed.");
        }
        return added;
    }

    internal static void BeginAutomaticRestock(Vendor vendor)
    {
        automaticRestockVendorId = null;
        automaticRestockExclusions = null;
        NeuralNPC? npc = vendor?.GetComponent<NeuralNPC>();
        if (npc == null)
            return;

        string? vendorId = GetVendorId(npc);
        if (vendorId == null)
            return;
        automaticRestockVendorId = vendorId;
        automaticRestockExclusions =
            VendorStockStore.GetEffectiveAutomaticStockExclusions(vendorId);
    }

    internal static void EndAutomaticRestock()
    {
        automaticRestockVendorId = null;
        automaticRestockExclusions = null;
    }

    internal static Predicate<Item> ProtectAutomaticStockPredicate(
        Predicate<Item> original)
    {
        if (automaticRestockVendorId == null)
            return original;

        string vendorId = automaticRestockVendorId;
        HashSet<string> exclusions = automaticRestockExclusions ??
                                     new HashSet<string>(
                                         StringComparer.OrdinalIgnoreCase);
        return item =>
        {
            if (item == null)
            {
                string nullKey = vendorId + "|<null>";
                if (PredicateFailuresLogged.Add(nullKey))
                    Plugin.Log.LogWarning(
                        $"Skipped a null ItemLibrary entry while generating " +
                        $"automatic stock for '{vendorId}'.");
                return false;
            }

            if (exclusions.Contains(item.name))
                return false;

            try
            {
                return original(item);
            }
            catch (NullReferenceException exception)
            {
                string failureKey = vendorId + "|" + item.name + "|" +
                                    original.Method.Name;
                if (PredicateFailuresLogged.Add(failureKey))
                    Plugin.Log.LogWarning(
                        $"Skipped malformed automatic-stock candidate " +
                        $"'{item.name}' for '{vendorId}' while evaluating " +
                        $"'{original.Method.Name}': {exception.Message}");
                return false;
            }
        };
    }

    private static int RemoveExcludedAutomaticStock(
        Inventory inventory,
        HashSet<string> exclusions,
        string vendorId)
    {
        if (exclusions.Count == 0)
            return 0;

        Item[] removed = inventory.items
            .Where(item => item != null && exclusions.Contains(item.name))
            .ToArray();
        inventory.RemoveItems(removed);
        if (removed.Length > 0)
            Plugin.Log.LogInfo(
                $"Removed {removed.Length} excluded automatic stock item(s) " +
                $"from '{vendorId}'.");
        return removed.Length;
    }

    private static void CollectFixedStock(
        Inventory inventory,
        string vendorId,
        IEnumerable<VendorStockEntry> entries,
        ICollection<RequestedStock> requestedStock)
    {
        foreach (VendorStockEntry entry in entries)
        {
            Item? template = ItemLibrary.Items.FirstOrDefault(item =>
                item != null &&
                item.name.Equals(
                    entry.itemName,
                    StringComparison.OrdinalIgnoreCase));
            if (template == null)
            {
                string missingKey = vendorId + "|" + entry.itemName;
                if (MissingItemsLogged.Add(missingKey))
                    Plugin.Log.LogWarning(
                        $"Vendor rule '{vendorId}' references missing item " +
                        $"'{entry.itemName}'.");
                continue;
            }

            Quality? quality = ParseQualityOverride(entry.quality);
            if (quality.HasValue && !TryGetItemQuality(template, out _))
            {
                string qualityKey = vendorId + "|" + template.name + "|" +
                                    quality.Value;
                if (UnsupportedQualityLogged.Add(qualityKey))
                    Plugin.Log.LogWarning(
                        $"Vendor rule '{vendorId}' requested quality " +
                        $"'{quality.Value}' for '{template.name}', which has " +
                        "no IQualityItemComponent. Template quality will be used.");
                quality = null;
            }

            int existing = CountVendorStock(
                inventory,
                template.name,
                quality);
            for (int index = existing; index < entry.quantity; index++)
                requestedStock.Add(new RequestedStock(template, quality));
        }
    }

    private static void CollectRandomStock(
        Inventory inventory,
        string vendorId,
        IReadOnlyList<VendorStockEntry> fixedEntries,
        IEnumerable<VendorRandomStockRule> pools,
        HashSet<string> exclusions,
        ICollection<RequestedStock> requestedStock,
        string reason)
    {
        // Allocate only stock already in the inventory. Pending fixed items
        // and picks made by another random pool cannot satisfy this pool.
        List<Item> existingStock = inventory.items.Where(item =>
            item != null &&
            item.GetItemComponent<ItemComponent_VendorItem>() != null).ToList();
        var targets = new List<int>();
        var eligibleItems = new List<bool[]>();
        foreach (VendorStockEntry entry in fixedEntries)
        {
            Item? template = ItemLibrary.Items.FirstOrDefault(item =>
                item != null && item.name.Equals(
                    entry.itemName, StringComparison.OrdinalIgnoreCase));
            if (template == null)
                continue;
            Quality? quality = ParseQualityOverride(entry.quality);
            if (quality.HasValue && !TryGetItemQuality(template, out _))
                quality = null;

            targets.Add(entry.quantity);
            eligibleItems.Add(existingStock.Select(item =>
                item.name.Equals(entry.itemName, StringComparison.OrdinalIgnoreCase) &&
                ItemMatchesQuality(item, quality)).ToArray());
        }

        var rolls = new List<RandomPoolRoll>();
        foreach (VendorRandomStockRule pool in pools)
        {
            if (pool.chancePercent <= 0f ||
                (pool.chancePercent < 100f &&
                 UnityEngine.Random.value * 100f >= pool.chancePercent))
            {
                Plugin.Log.LogInfo(
                    $"Random stock pool '{pool.id}' for '{vendorId}' after " +
                    $"{reason}: chance roll skipped ({pool.chancePercent:0.##}%).");
                continue;
            }

            List<Item> candidates = ItemLibrary.Items
                .Where(item => ItemMatchesRandomPool(item, pool, exclusions))
                .GroupBy(item => item.name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (candidates.Count == 0)
            {
                Plugin.Log.LogWarning(
                    $"Random stock pool '{pool.id}' for '{vendorId}' after " +
                    $"{reason} matches no registered items " +
                    $"(source: {pool.source}, category: {pool.category}, " +
                    $"quality: {pool.quality}).");
                continue;
            }

            int target = UnityEngine.Random.Range(
                pool.minQuantity,
                pool.maxQuantity + 1);
            Quality? quality = ParseQualityOverride(pool.quality);
            rolls.Add(new RandomPoolRoll
            {
                Pool = pool,
                Candidates = candidates,
                Quality = quality,
                Target = target,
                AllocationIndex = targets.Count
            });
            targets.Add(target);
            eligibleItems.Add(existingStock.Select(item =>
                ItemMatchesRandomPool(item, pool, exclusions) &&
                ItemMatchesQuality(item, quality)).ToArray());
        }

        // Each item can satisfy just one minimum. Reassigning compatible
        // items prevents a broad pool from taking the only Legendary item
        // needed by a later pool when another item could serve the broad one.
        int[] allocations = StockAllocation.Allocate(targets, eligibleItems);
        foreach (RandomPoolRoll roll in rolls)
        {
            VendorRandomStockRule pool = roll.Pool;
            List<Item> candidates = roll.Candidates;
            int existing = allocations[roll.AllocationIndex];
            int needed = roll.Target - existing;
            if (!pool.allowDuplicates)
            {
                var existingNames = inventory.items
                    .Where(item => item != null)
                    .Select(item => item.name)
                    .Concat(requestedStock.Select(request =>
                        request.Template.name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                candidates.RemoveAll(item => existingNames.Contains(item.name));
            }

            int queued = 0;
            for (int index = 0; index < needed && candidates.Count > 0; index++)
            {
                int candidateIndex = UnityEngine.Random.Range(
                    0,
                    candidates.Count);
                requestedStock.Add(new RequestedStock(
                    candidates[candidateIndex],
                    roll.Quality));
                queued++;
                if (!pool.allowDuplicates)
                    candidates.RemoveAt(candidateIndex);
            }

            Plugin.Log.LogInfo(
                $"Random stock pool '{pool.id}' for '{vendorId}' after " +
                $"{reason}: target {roll.Target}, existing stock allocated " +
                $"{existing}, queued {queued} (quality: {pool.quality}).");
            if (queued < needed)
                Plugin.Log.LogWarning(
                    $"Random stock pool '{pool.id}' for '{vendorId}' is " +
                    $"short by {needed - queued} item(s): no more candidates " +
                    "remain after duplicate-name restrictions.");
        }
    }

    private static bool ItemMatchesRandomPool(
        Item? item,
        VendorRandomStockRule pool,
        HashSet<string> exclusions)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.name) ||
            exclusions.Contains(item.name))
            return false;

        bool isCustom = item.spriteName?.StartsWith(
            "custom:",
            StringComparison.OrdinalIgnoreCase) == true;
        if (pool.source.StartsWith(
                "Pack:",
                StringComparison.OrdinalIgnoreCase))
        {
            string requiredPack = pool.source.Substring("Pack:".Length);
            if (!TryGetCustomPackId(item, out string itemPack) ||
                !itemPack.Equals(
                    requiredPack,
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else if (Enum.TryParse(
                     pool.source,
                     true,
                     out VendorRandomItemSource source) &&
                 ((source == VendorRandomItemSource.BaseGame && isCustom) ||
                  (source == VendorRandomItemSource.Custom && !isCustom)))
        {
            return false;
        }

        if (!pool.category.Equals("Any", StringComparison.OrdinalIgnoreCase) &&
            (!Enum.TryParse(pool.category, true, out ItemCategory category) ||
             item.itemCategory != category))
            return false;
        if (!string.IsNullOrWhiteSpace(pool.nameContains) &&
            item.name.IndexOf(
                pool.nameContains,
                StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        if (!string.IsNullOrWhiteSpace(pool.nameExcludes) &&
            item.name.IndexOf(
                pool.nameExcludes,
                StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (pool.minValue.HasValue && item.value < pool.minValue.Value)
            return false;
        if (pool.maxValue.HasValue && item.value > pool.maxValue.Value)
            return false;

        ItemComponent_Pickupable? pickupable =
            item.GetItemComponent<ItemComponent_Pickupable>();
        if (Enum.TryParse(
                pool.pickupable,
                true,
                out VendorPickupableFilter pickupableFilter) &&
            ((pickupableFilter == VendorPickupableFilter.Pickupable &&
              pickupable == null) ||
             (pickupableFilter == VendorPickupableFilter.VendorCarryable &&
              pickupable?.vendorCanCarry != true)))
            return false;
        if (ParseQualityOverride(pool.quality).HasValue &&
            !TryGetItemQuality(item, out _))
            return false;
        return true;
    }

    private static Quality? ParseQualityOverride(string? value)
    {
        return Enum.TryParse(value, true, out Quality quality)
            ? quality
            : null;
    }

    private static bool TryGetItemQuality(Item item, out Quality quality)
    {
        if (item.TryGetItemComponent<IQualityItemComponent>(
                out IQualityItemComponent component))
        {
            quality = component.Quality;
            return true;
        }

        quality = Quality.Normal;
        return false;
    }

    private static bool ItemMatchesQuality(Item item, Quality? quality) =>
        !quality.HasValue ||
        (TryGetItemQuality(item, out Quality actual) &&
         actual == quality.Value);

    internal static bool TryGetCustomPackId(Item? item, out string packId)
    {
        packId = "";
        string? spriteName = item?.spriteName;
        const string prefix = "custom:";
        if (spriteName?.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase) != true)
            return false;

        int separator = spriteName.IndexOf(':', prefix.Length);
        if (separator <= prefix.Length)
            return false;

        packId = spriteName.Substring(
            prefix.Length,
            separator - prefix.Length);
        return packId.Length > 0;
    }

    private static int CountVendorStock(
        Inventory inventory,
        string itemName,
        Quality? quality = null) =>
        inventory.items.Count(item =>
            item != null &&
            item.name.Equals(itemName, StringComparison.OrdinalIgnoreCase) &&
            item.GetItemComponent<ItemComponent_VendorItem>() != null &&
            ItemMatchesQuality(item, quality));

    internal static string? GetVendorId(NeuralNPC npc)
    {
        CustomNPCHandler? handler = npc.GetComponent<CustomNPCHandler>();
        if (handler != null &&
            !string.IsNullOrWhiteSpace(handler.customContentName))
        {
            return VendorCatalog.IsVendorCustomNpc(handler.customContentName)
                ? "custom:" + handler.customContentName
                : null;
        }
        if (npc.npcName == NPCName.None || (int)npc.npcName >= 11)
            return null;
        return "base:" + npc.npcName;
    }
}

[HarmonyPatch(typeof(Vendor), "OnNewDay")]
internal static class VendorRestockPatch
{
    private static void Prefix(Vendor __instance)
    {
        VendorStockService.BeginAutomaticRestock(__instance);
    }

    private static void Postfix(Vendor __instance)
    {
        VendorStockService.Apply(
            __instance,
            "daily restock",
            removeExcludedAutomaticStock: true,
            rollRandomStock: true);
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        VendorStockService.EndAutomaticRestock();
        return __exception;
    }
}

[HarmonyPatch(typeof(ItemLibrary), nameof(ItemLibrary.GetItemsByPredicate))]
internal static class AutomaticStockPredicateSafetyPatch
{
    private static void Prefix(ref Predicate<Item> predicate)
    {
        predicate = VendorStockService.ProtectAutomaticStockPredicate(
            predicate);
    }
}

[HarmonyPatch(typeof(CustomNPCManager), "SpawnInNPCs")]
internal static class CustomNpcVendorRegistrationPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        VendorStockService.ApplyAll(
            "custom NPC registration",
            rollRandomForCustomVendors: true);
    }
}
