#nullable enable

using Silverpine.ModdingTools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace SilverpineMods.VendorInventoryManager;

internal sealed class VendorInventoryEditorUI : ModToolBehaviour
{
    private const float DesignWidth = 1920f;
    private const float DesignHeight = 1080f;
    private const float VendorPanelWidth = 390f;
    private const float StockPanelWidth = 600f;
    private const float PanelGap = 12f;
    private const float PollIntervalSeconds = 2f;

    private static VendorInventoryEditorUI? instance;

    private readonly List<VendorDescriptor> vendors = new();
    private readonly List<ItemBrowserEntry> browserItems = new();
    private readonly HashSet<string> expandedSources =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedCategories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedPacks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> quantityText =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> randomFieldText =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> collapsedRandomPools =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] RandomSources =
        Enum.GetNames(typeof(VendorRandomItemSource));
    private static readonly string[] RandomCategories =
        new[] { "Any" }
            .Concat(Enum.GetNames(typeof(ItemCategory)))
            .ToArray();
    private static readonly string[] PickupableFilters =
        Enum.GetNames(typeof(VendorPickupableFilter));
    private static readonly string[] QualityChoices =
        new[] { "Template" }
            .Concat(Enum.GetNames(typeof(Quality)))
            .ToArray();

    private string selectedPackId = "";
    private string selectedVendorId = "";
    private string deletePackConfirmationId = "";
    private string openRandomSourceDropdown = "";
    private string search = "";
    private string status = "";
    private Vector2 vendorScroll;
    private Vector2 stockScroll;
    private Vector2 browserScroll;
    private float nextVendorPoll;
    private bool open;
    private bool dirty;
    private bool fixedStockExpanded = true;
    private bool exclusionsExpanded = true;
    private bool randomPoolsExpanded = true;

    private sealed class ItemBrowserEntry
    {
        internal string Name = "";
        internal string Description = "";
        internal string Category = "";
        internal string PackId = "";
        internal int Value;
        internal bool IsCustom;
    }

    internal static void Open(ModToolSession session)
    {
        if (instance == null)
        {
            GameObject root = new("Vendor Inventory Manager IMGUI");
            instance = root.AddComponent<VendorInventoryEditorUI>();
        }
        else
        {
            instance.gameObject.SetActive(true);
        }

        instance.AttachSession(session);
        instance.open = true;
        instance.search = "";
        instance.status = "";
        instance.dirty = false;
        instance.deletePackConfirmationId = "";
        instance.quantityText.Clear();
        instance.randomFieldText.Clear();
        instance.collapsedRandomPools.Clear();
        instance.openRandomSourceDropdown = "";
        instance.EnsureSelectedPack();
        instance.RefreshItems();
        instance.RefreshVendors(report: true);
        instance.nextVendorPoll =
            Time.unscaledTime + PollIntervalSeconds;
    }

    private VendorDescriptor? SelectedVendor =>
        vendors.FirstOrDefault(candidate =>
            candidate.Id.Equals(
                selectedVendorId,
                StringComparison.OrdinalIgnoreCase));

    private VendorStockPack SelectedPack
    {
        get
        {
            VendorStockPack? selected =
                VendorStockStore.FindPack(selectedPackId);
            if (selected != null)
                return selected;
            EnsureSelectedPack();
            return VendorStockStore.FindPack(selectedPackId)!;
        }
    }

    private void Update()
    {
        if (!open)
            return;
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }
        if (Time.unscaledTime < nextVendorPoll)
            return;

        nextVendorPoll = Time.unscaledTime + PollIntervalSeconds;
        RefreshVendors(report: false);
    }

    private void OnGUI()
    {
        if (!open)
            return;

        GUI.enabled = true;
        GUI.color = Color.white;
        GUI.backgroundColor = Color.white;
        GUI.depth = -1000;

        using ModGuiScope scope = ModGui.BeginScaled(
            DesignWidth,
            DesignHeight);
        Color old = GUI.color;
        GUI.color = new Color(0.045f, 0.065f, 0.08f, 0.995f);
        GUI.DrawTexture(
            new Rect(0, 0, DesignWidth, DesignHeight),
            Texture2D.whiteTexture);
        GUI.color = old;

        GUILayout.BeginArea(
            new Rect(12, 10, DesignWidth - 24, DesignHeight - 20));
        DrawHeader();
        DrawPackBar();
        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        DrawVendorPanel();
        GUILayout.Space(PanelGap);
        DrawStockPanel();
        GUILayout.Space(PanelGap);
        DrawItemBrowser();
        GUILayout.EndHorizontal();
        if (!string.IsNullOrWhiteSpace(status))
            GUILayout.Label(status, GUI.skin.box);
        GUILayout.EndArea();
    }

    private void DrawHeader()
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label(
            "Vendor Inventory Manager",
            GUILayout.Width(340));
        GUILayout.Label(
            "Additive minimum stock for base and vendor-flagged custom NPCs");
        if (dirty)
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 0.78f, 0.32f);
            GUILayout.Label("Unsaved changes", GUILayout.Width(150));
            GUI.color = old;
        }
        if (GUILayout.Button("Poll Vendors", GUILayout.Width(120)))
            RefreshVendors(report: true);
        if (GUILayout.Button("Reload", GUILayout.Width(95)))
            Reload();
        if (GUILayout.Button("Save + Apply", GUILayout.Width(135)))
            SaveAndApply();
        if (GUILayout.Button("Close", GUILayout.Width(90)))
            Close();
        GUILayout.EndHorizontal();
    }

    private void DrawPackBar()
    {
        List<VendorStockPack> packs = VendorStockStore.Document.packs;
        VendorStockPack pack = SelectedPack;
        int index = packs.IndexOf(pack);

        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label("Stock pack", GUILayout.Width(105));
        GUI.enabled = packs.Count > 1;
        if (GUILayout.Button("◀", GUILayout.Width(38)))
            SelectPack(packs[(index - 1 + packs.Count) % packs.Count]);
        GUI.enabled = true;
        GUILayout.Label($"{index + 1}/{packs.Count}", GUILayout.Width(48));
        GUI.enabled = packs.Count > 1;
        if (GUILayout.Button("▶", GUILayout.Width(38)))
            SelectPack(packs[(index + 1) % packs.Count]);
        GUI.enabled = true;

        GUILayout.Label("Name", GUILayout.Width(48));
        string nextName = GUILayout.TextField(
            pack.name,
            GUILayout.MinWidth(220),
            GUILayout.MaxWidth(420));
        if (!string.Equals(nextName, pack.name, StringComparison.Ordinal))
        {
            pack.name = nextName;
            SetDirty();
        }

        bool nextEnabled = GUILayout.Toggle(
            pack.enabled,
            pack.enabled ? "Enabled" : "Disabled",
            GUILayout.Width(105));
        if (nextEnabled != pack.enabled)
        {
            pack.enabled = nextEnabled;
            deletePackConfirmationId = "";
            SetDirty();
            status = pack.enabled
                ? $"Enabled stock pack '{pack.name}'."
                : $"Disabled stock pack '{pack.name}'. Existing live stock " +
                  "is not removed until normal vendor turnover.";
        }

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("New Pack", GUILayout.Width(105)))
        {
            VendorStockPack created = VendorStockStore.CreatePack();
            SelectPack(created);
            SetDirty();
            status = $"Created stock pack '{created.name}'.";
        }

        GUI.enabled = packs.Count > 1;
        bool confirming = deletePackConfirmationId.Equals(
            pack.id,
            StringComparison.OrdinalIgnoreCase);
        if (GUILayout.Button(
                confirming ? "Confirm Delete" : "Delete Pack",
                GUILayout.Width(125)))
        {
            if (!confirming)
            {
                deletePackConfirmationId = pack.id;
                status = $"Choose Confirm Delete to remove '{pack.name}'.";
            }
            else
            {
                int removedIndex = packs.IndexOf(pack);
                packs.Remove(pack);
                SelectPack(packs[Mathf.Clamp(
                    removedIndex,
                    0,
                    packs.Count - 1)]);
                deletePackConfirmationId = "";
                SetDirty();
                status = $"Deleted stock pack '{pack.name}'.";
            }
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();
    }

    private void EnsureSelectedPack()
    {
        VendorStockStore.Normalize();
        VendorStockPack pack =
            VendorStockStore.FindPack(selectedPackId) ??
            VendorStockStore.Document.packs[0];
        selectedPackId = pack.id;
    }

    private void SelectPack(VendorStockPack pack)
    {
        selectedPackId = pack.id;
        deletePackConfirmationId = "";
        quantityText.Clear();
        randomFieldText.Clear();
        collapsedRandomPools.Clear();
        openRandomSourceDropdown = "";
        stockScroll = Vector2.zero;
    }

    private void DrawVendorPanel()
    {
        GUILayout.BeginVertical(
            GUI.skin.box,
            GUILayout.Width(VendorPanelWidth),
            GUILayout.ExpandHeight(true));
        GUILayout.Label("Vendors");
        GUILayout.Label(
            "Custom characters appear when an enabled possible routine " +
            "contains RoutineArgument_BeVendor.");

        vendorScroll = GUILayout.BeginScrollView(vendorScroll);
        DrawVendorGroup(
            "Base game",
            vendors.Where(candidate => !candidate.IsCustom));
        GUILayout.Space(8);
        DrawVendorGroup(
            "Custom vendor routines",
            vendors.Where(candidate => candidate.IsCustom));
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawVendorGroup(
        string title,
        IEnumerable<VendorDescriptor> source)
    {
        VendorDescriptor[] entries = source.ToArray();
        GUILayout.Label($"{title} ({entries.Length})");
        if (entries.Length == 0)
        {
            GUILayout.Label("None discovered.");
            return;
        }

        foreach (VendorDescriptor vendor in entries)
        {
            bool selected = vendor.Id.Equals(
                selectedVendorId,
                StringComparison.OrdinalIgnoreCase);
            VendorStockRule? rule = VendorStockStore.Find(
                SelectedPack,
                vendor.Id);
            int count = (rule?.items.Count ?? 0) +
                        (rule?.automaticStockExclusions.Count ?? 0) +
                        (rule?.randomPools.Count ?? 0);
            Color old = GUI.backgroundColor;
            if (selected)
                GUI.backgroundColor = new Color(0.48f, 0.73f, 0.9f);
            else if (!vendor.IsAvailable)
                GUI.backgroundColor = new Color(0.46f, 0.42f, 0.42f);

            string state = vendor.IsAvailable ? "●" : "○";
            if (GUILayout.Button(
                    $"{state} {vendor.Name}  [{count}]"))
            {
                selectedVendorId = vendor.Id;
                quantityText.Clear();
                randomFieldText.Clear();
                collapsedRandomPools.Clear();
                openRandomSourceDropdown = "";
            }
            GUI.backgroundColor = old;
        }
    }

    private void DrawStockPanel()
    {
        GUILayout.BeginVertical(
            GUI.skin.box,
            GUILayout.Width(StockPanelWidth),
            GUILayout.ExpandHeight(true));
        GUILayout.Label($"Configured stock · {SelectedPack.name}");
        if (!SelectedPack.enabled)
            GUILayout.Label("This pack is disabled and is not applied.");

        VendorDescriptor? vendor = SelectedVendor;
        if (vendor == null)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("Select a vendor.");
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            return;
        }

        GUILayout.Label(vendor.Name);
        GUILayout.Label(vendor.Id);
        GUILayout.Label(vendor.Detail);
        GUILayout.Label(vendor.IsAvailable
            ? "A live vendor is currently available."
            : "The rule can be edited now and will apply when this vendor loads.");
        GUILayout.Space(6);

        VendorStockRule? rule = VendorStockStore.Find(
            SelectedPack,
            vendor.Id);
        VendorStockEntry[] entries = rule?.items.ToArray() ??
                                     Array.Empty<VendorStockEntry>();
        string[] exclusions = rule?.automaticStockExclusions.ToArray() ??
                              Array.Empty<string>();
        VendorRandomStockRule[] randomPools = rule?.randomPools.ToArray() ??
                                               Array.Empty<VendorRandomStockRule>();
        stockScroll = GUILayout.BeginScrollView(stockScroll);

        if (DrawFoldout(
                $"Fixed minimum stock ({entries.Length})",
                ref fixedStockExpanded))
        {
            if (entries.Length == 0)
                GUILayout.Label("Add fixed items from the item tree.");
            foreach (VendorStockEntry entry in entries)
                DrawStockEntry(vendor, rule!, entry);
        }

        GUILayout.Space(6);
        if (DrawFoldout(
                $"Automatic-stock exclusions ({exclusions.Length})",
                ref exclusionsExpanded))
        {
            GUILayout.Label(
                "Excluded names are withheld from native restock and random " +
                "pools. Explicit fixed stock can still add them.");
            if (exclusions.Length == 0)
                GUILayout.Label("Choose Exclude Auto beside an item.");
            foreach (string itemName in exclusions)
                DrawAutomaticStockExclusion(vendor, rule!, itemName);
        }

        GUILayout.Space(6);
        if (DrawFoldout(
                $"Random stock pools ({randomPools.Length})",
                ref randomPoolsExpanded))
        {
            GUILayout.Label(
                "Each pool has a separate stock target. Fixed stock and " +
                "items allocated to another pool do not fill its target. " +
                "Rolls run on Save + Apply, daily restock, and custom " +
                "vendor registration.");
            foreach (VendorRandomStockRule pool in randomPools)
                DrawRandomPool(vendor, rule!, pool);
            if (GUILayout.Button("+ Add random stock pool"))
            {
                rule ??= VendorStockStore.GetOrCreate(
                    SelectedPack,
                    vendor.Id);
                VendorRandomStockRule created =
                    VendorStockStore.CreateRandomPool(rule);
                randomPoolsExpanded = true;
                SetDirty();
                status = $"Added random pool '{created.id}' to {vendor.Name}.";
            }
        }
        GUILayout.EndScrollView();

        GUI.enabled = entries.Length + exclusions.Length + randomPools.Length > 0;
        if (GUILayout.Button("Clear all configuration for this vendor"))
            ClearVendorRule(vendor.Id);
        GUI.enabled = true;
        GUILayout.Label(
            "Fixed quantities are minimum vendor-tagged stock. Random pools " +
            "roll on Save + Apply and on automatic stock cycles.");
        GUILayout.EndVertical();
    }

    private static bool DrawFoldout(string label, ref bool expanded)
    {
        if (GUILayout.Button(
                (expanded ? "▼  " : "▶  ") + label,
                GUI.skin.box))
            expanded = !expanded;
        return expanded;
    }

    private void DrawStockEntry(
        VendorDescriptor vendor,
        VendorStockRule rule,
        VendorStockEntry entry)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label(entry.itemName, GUILayout.MinWidth(210));
        Item? template = ItemLibrary.Items.FirstOrDefault(item =>
            item != null &&
            item.name.Equals(
                entry.itemName,
                StringComparison.OrdinalIgnoreCase));
        GUILayout.Label(
            template == null
                ? "Missing item"
                : $"{template.itemCategory} · {template.value}g",
            GUILayout.Width(155));

        if (GUILayout.Button("−", GUILayout.Width(34)))
        {
            entry.quantity = Mathf.Max(1, entry.quantity - 1);
            SetDirty();
        }

        string key = SelectedPack.id + "|" + vendor.Id + "|" +
                     entry.itemName;
        if (!quantityText.TryGetValue(key, out string text))
            text = entry.quantity.ToString(CultureInfo.InvariantCulture);
        string next = GUILayout.TextField(text, GUILayout.Width(48));
        quantityText[key] = next;
        if (int.TryParse(
                next,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            int clamped = Mathf.Clamp(parsed, 1, 99);
            if (entry.quantity != clamped)
            {
                entry.quantity = clamped;
                SetDirty();
            }
        }

        if (GUILayout.Button("+", GUILayout.Width(34)))
        {
            entry.quantity = Mathf.Min(99, entry.quantity + 1);
            quantityText[key] =
                entry.quantity.ToString(CultureInfo.InvariantCulture);
            SetDirty();
        }
        if (GUILayout.Button("Remove", GUILayout.Width(82)))
        {
            rule.items.Remove(entry);
            quantityText.Remove(key);
            RemoveRuleIfEmpty(rule);
            SetDirty();
        }
        GUILayout.EndHorizontal();

        bool supportsQuality = template != null &&
                               template.TryGetItemComponent<
                                   IQualityItemComponent>(out _);
        if (supportsQuality)
        {
            entry.quality = DrawCompactChoice(
                "Vendor quality",
                entry.quality,
                QualityChoices,
                275f);
            GUILayout.Label(
                entry.quality.Equals(
                    "Template",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Template keeps the item's registered quality."
                    : "Only vendor copies created by this fixed rule use " +
                      entry.quality + " quality.");
        }
        else if (!entry.quality.Equals(
                     "Template",
                     StringComparison.OrdinalIgnoreCase))
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(
                "This item does not expose quality; the saved override is ignored.");
            if (GUILayout.Button("Use Template", GUILayout.Width(110f)))
            {
                entry.quality = "Template";
                SetDirty();
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
    }

    private void DrawAutomaticStockExclusion(
        VendorDescriptor vendor,
        VendorStockRule rule,
        string itemName)
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label(itemName, GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Allow Auto", GUILayout.Width(105)))
        {
            rule.automaticStockExclusions.RemoveAll(value =>
                value.Equals(itemName, StringComparison.OrdinalIgnoreCase));
            RemoveRuleIfEmpty(rule);
            SetDirty();
            status = $"Allowed automatic stocking of {itemName} for " +
                     $"{vendor.Name}.";
        }
        GUILayout.EndHorizontal();
    }

    private void DrawRandomPool(
        VendorDescriptor vendor,
        VendorStockRule rule,
        VendorRandomStockRule pool)
    {
        string prefix = SelectedPack.id + "|" + vendor.Id + "|" + pool.id;
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        bool collapsed = collapsedRandomPools.Contains(prefix);
        if (GUILayout.Button(
                collapsed ? "▶" : "▼",
                GUILayout.Width(34f)))
        {
            if (!collapsedRandomPools.Add(prefix))
                collapsedRandomPools.Remove(prefix);
            collapsed = collapsedRandomPools.Contains(prefix);
            if (collapsed && openRandomSourceDropdown.Equals(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
                openRandomSourceDropdown = "";
        }
        bool enabled = GUILayout.Toggle(
            pool.enabled,
            pool.enabled ? "Enabled" : "Disabled",
            GUILayout.Width(90f));
        if (enabled != pool.enabled)
        {
            pool.enabled = enabled;
            SetDirty();
        }
        string quantity = pool.minQuantity == pool.maxQuantity
            ? pool.minQuantity.ToString(CultureInfo.InvariantCulture)
            : pool.minQuantity.ToString(CultureInfo.InvariantCulture) + "–" +
              pool.maxQuantity.ToString(CultureInfo.InvariantCulture);
        GUILayout.Label(
            $"{pool.id} · {FormatRandomSource(pool.source)}/" +
            $"{pool.category} · " +
            $"{quantity} @ {pool.chancePercent:0.##}%" +
            (pool.quality.Equals(
                "Template",
                StringComparison.OrdinalIgnoreCase)
                ? ""
                : " · " + pool.quality));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Remove Pool", GUILayout.Width(110)))
        {
            rule.randomPools.Remove(pool);
            RemoveRandomFieldText(prefix);
            collapsedRandomPools.Remove(prefix);
            if (openRandomSourceDropdown.Equals(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
                openRandomSourceDropdown = "";
            RemoveRuleIfEmpty(rule);
            SetDirty();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            return;
        }
        GUILayout.EndHorizontal();

        if (collapsed)
        {
            GUILayout.EndVertical();
            return;
        }

        GUILayout.BeginHorizontal();
        pool.source = DrawRandomSourceDropdown(
            prefix,
            pool.source,
            275f);
        pool.category = DrawCompactChoice(
            "Category",
            pool.category,
            RandomCategories,
            275f);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        pool.pickupable = DrawCompactChoice(
            "Pickup filter",
            pool.pickupable,
            PickupableFilters,
            275f);
        pool.quality = DrawCompactChoice(
            "Quality",
            pool.quality,
            QualityChoices,
            275f);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.Width(275f));
        bool duplicates = GUILayout.Toggle(
            pool.allowDuplicates,
            "Allow duplicate selections");
        GUILayout.Label(
            duplicates
                ? "The same item may be selected more than once."
                : "Each selected item name must be unique.");
        if (duplicates != pool.allowDuplicates)
        {
            pool.allowDuplicates = duplicates;
            SetDirty();
        }
        GUILayout.EndVertical();
        GUILayout.BeginVertical(GUILayout.Width(275f));
        GUILayout.Label(
            pool.quality.Equals(
                "Template",
                StringComparison.OrdinalIgnoreCase)
                ? "Template allows any matching item and preserves its quality."
                : "A selected quality limits this pool to quality-capable " +
                  "items and applies " + pool.quality + " to new stock.");
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.Width(275f));
        GUILayout.Label("Name contains (optional)");
        string contains = GUILayout.TextField(pool.nameContains ?? "");
        if (!string.Equals(contains, pool.nameContains, StringComparison.Ordinal))
        {
            pool.nameContains = contains;
            SetDirty();
        }
        GUILayout.EndVertical();
        GUILayout.BeginVertical(GUILayout.Width(275f));
        GUILayout.Label("Name excludes (optional)");
        string excludes = GUILayout.TextField(pool.nameExcludes ?? "");
        if (!string.Equals(excludes, pool.nameExcludes, StringComparison.Ordinal))
        {
            pool.nameExcludes = excludes;
            SetDirty();
        }
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        DrawOptionalIntField(
            "Minimum value",
            prefix + "|minValue",
            pool.minValue,
            value => pool.minValue = value,
            180f);
        DrawOptionalIntField(
            "Maximum value",
            prefix + "|maxValue",
            pool.maxValue,
            value => pool.maxValue = value,
            180f);
        DrawFloatField(
            "Chance %",
            prefix + "|chance",
            pool.chancePercent,
            0f,
            100f,
            value => pool.chancePercent = value,
            180f);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        DrawIntField(
            "Minimum quantity",
            prefix + "|minQuantity",
            pool.minQuantity,
            0,
            99,
            value =>
            {
                pool.minQuantity = value;
                if (pool.maxQuantity < value)
                {
                    pool.maxQuantity = value;
                    randomFieldText[prefix + "|maxQuantity"] =
                        value.ToString(CultureInfo.InvariantCulture);
                }
            },
            275f);
        DrawIntField(
            "Maximum quantity",
            prefix + "|maxQuantity",
            pool.maxQuantity,
            pool.minQuantity,
            99,
            value => pool.maxQuantity = value,
            275f);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private string DrawCompactChoice(
        string label,
        string current,
        string[] choices,
        float width)
    {
        int index = Array.FindIndex(choices, value => value.Equals(
            current,
            StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = 0;

        GUILayout.BeginVertical(GUILayout.Width(width));
        GUILayout.Label(label);
        GUILayout.BeginHorizontal();
        string next = choices[index];
        if (GUILayout.Button("◀", GUILayout.Width(34f)))
            next = choices[(index - 1 + choices.Length) % choices.Length];
        GUILayout.Label(next, GUILayout.Width(width - 80f));
        if (GUILayout.Button("▶", GUILayout.Width(34f)))
            next = choices[(Array.IndexOf(choices, next) + 1) % choices.Length];
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        if (!next.Equals(current, StringComparison.OrdinalIgnoreCase))
            SetDirty();
        return next;
    }

    private string DrawRandomSourceDropdown(
        string poolKey,
        string current,
        float width)
    {
        string[] choices = GetRandomSourceChoices(current);
        bool expanded = openRandomSourceDropdown.Equals(
            poolKey,
            StringComparison.OrdinalIgnoreCase);
        string next = current;

        GUILayout.BeginVertical(GUILayout.Width(width));
        GUILayout.Label("Source");
        if (GUILayout.Button(
                FormatRandomSource(current) + (expanded ? "  ▲" : "  ▼")))
            openRandomSourceDropdown = expanded ? "" : poolKey;

        if (expanded)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            foreach (string choice in choices)
            {
                bool selected = choice.Equals(
                    current,
                    StringComparison.OrdinalIgnoreCase);
                Color old = GUI.backgroundColor;
                if (selected)
                    GUI.backgroundColor = new Color(0.48f, 0.73f, 0.9f);
                if (GUILayout.Button(FormatRandomSource(choice)))
                {
                    next = choice;
                    openRandomSourceDropdown = "";
                }
                GUI.backgroundColor = old;
            }
            GUILayout.EndVertical();
        }
        GUILayout.EndVertical();

        if (!next.Equals(current, StringComparison.OrdinalIgnoreCase))
            SetDirty();
        return next;
    }

    private string[] GetRandomSourceChoices(string current)
    {
        var choices = new List<string>(RandomSources);
        choices.AddRange(browserItems
            .Where(item => item.IsCustom &&
                           !string.IsNullOrWhiteSpace(item.PackId))
            .Select(item => "Pack:" + item.PackId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        if (current.StartsWith("Pack:", StringComparison.OrdinalIgnoreCase) &&
            !choices.Contains(current, StringComparer.OrdinalIgnoreCase))
            choices.Add(current);
        return choices.ToArray();
    }

    private static string FormatRandomSource(string source)
    {
        if (source.Equals(
                nameof(VendorRandomItemSource.BaseGame),
                StringComparison.OrdinalIgnoreCase))
            return "Base game";
        if (source.Equals(
                nameof(VendorRandomItemSource.Custom),
                StringComparison.OrdinalIgnoreCase))
            return "All custom items";
        if (source.StartsWith("Pack:", StringComparison.OrdinalIgnoreCase))
            return "Pack · " + source.Substring("Pack:".Length);
        return "Any source";
    }

    private void DrawOptionalIntField(
        string label,
        string key,
        int? current,
        Action<int?> setter,
        float width)
    {
        GUILayout.BeginVertical(GUILayout.Width(width));
        GUILayout.Label(label);
        if (!randomFieldText.TryGetValue(key, out string text))
            text = current?.ToString(CultureInfo.InvariantCulture) ?? "";
        string next = GUILayout.TextField(text);
        randomFieldText[key] = next;
        int? parsed = null;
        bool valid = string.IsNullOrWhiteSpace(next) ||
                     (int.TryParse(
                          next,
                          NumberStyles.Integer,
                          CultureInfo.InvariantCulture,
                          out int value) &&
                      value >= 0);
        if (valid)
        {
            if (!string.IsNullOrWhiteSpace(next))
                parsed = int.Parse(next, CultureInfo.InvariantCulture);
            if (parsed != current)
            {
                setter(parsed);
                SetDirty();
            }
        }
        GUILayout.EndVertical();
    }

    private void DrawIntField(
        string label,
        string key,
        int current,
        int minimum,
        int maximum,
        Action<int> setter,
        float width)
    {
        GUILayout.BeginVertical(GUILayout.Width(width));
        GUILayout.Label(label);
        if (!randomFieldText.TryGetValue(key, out string text))
            text = current.ToString(CultureInfo.InvariantCulture);
        string next = GUILayout.TextField(text);
        randomFieldText[key] = next;
        if (int.TryParse(
                next,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            int clamped = Mathf.Clamp(parsed, minimum, maximum);
            if (clamped != current)
            {
                setter(clamped);
                SetDirty();
            }
        }
        GUILayout.EndVertical();
    }

    private void DrawFloatField(
        string label,
        string key,
        float current,
        float minimum,
        float maximum,
        Action<float> setter,
        float width)
    {
        GUILayout.BeginVertical(GUILayout.Width(width));
        GUILayout.Label(label);
        if (!randomFieldText.TryGetValue(key, out string text))
            text = current.ToString("0.##", CultureInfo.InvariantCulture);
        string next = GUILayout.TextField(text);
        randomFieldText[key] = next;
        if (float.TryParse(
                next,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsed) &&
            !float.IsNaN(parsed) &&
            !float.IsInfinity(parsed))
        {
            float clamped = Mathf.Clamp(parsed, minimum, maximum);
            if (!Mathf.Approximately(clamped, current))
            {
                setter(clamped);
                SetDirty();
            }
        }
        GUILayout.EndVertical();
    }

    private void RemoveRandomFieldText(string prefix)
    {
        foreach (string key in randomFieldText.Keys
                     .Where(key => key.StartsWith(
                         prefix,
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            randomFieldText.Remove(key);
    }

    private void RemoveRuleIfEmpty(VendorStockRule rule)
    {
        if (rule.items.Count == 0 &&
            rule.automaticStockExclusions.Count == 0 &&
            rule.randomPools.Count == 0)
            SelectedPack.vendors.Remove(rule);
    }

    private void DrawItemBrowser()
    {
        GUILayout.BeginVertical(
            GUI.skin.box,
            GUILayout.ExpandWidth(true),
            GUILayout.ExpandHeight(true));
        GUILayout.Label("Item tree");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", GUILayout.Width(70));
        string next = GUILayout.TextField(search);
        if (next != search)
        {
            search = next;
            browserScroll = Vector2.zero;
        }
        GUI.enabled = !string.IsNullOrEmpty(search);
        if (GUILayout.Button("×", GUILayout.Width(38)))
            search = "";
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        int matches = FilteredItems().Count();
        GUILayout.Label(
            $"{matches} matching item{(matches == 1 ? "" : "s")}. " +
            "Choose Fixed or Exclude Auto for the selected vendor.");
        browserScroll = GUILayout.BeginScrollView(browserScroll);
        DrawSourceBranch("Base game", isCustom: false);
        GUILayout.Space(5);
        DrawSourceBranch("Custom items", isCustom: true);
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawSourceBranch(string label, bool isCustom)
    {
        ItemBrowserEntry[] items = FilteredItems()
            .Where(item => item.IsCustom == isCustom)
            .ToArray();
        if (items.Length == 0)
            return;

        bool forcedOpen = !string.IsNullOrWhiteSpace(search);
        bool expanded = forcedOpen || expandedSources.Contains(label);
        if (GUILayout.Button(
                $"{(expanded ? "▼" : "▶")} {label} ({items.Length})"))
        {
            if (!expandedSources.Add(label))
                expandedSources.Remove(label);
        }
        if (!expanded)
            return;

        if (!isCustom)
        {
            DrawCategoryBranches(
                label,
                items,
                forcedOpen,
                categoryIndent: 18f,
                itemIndent: 38f);
            return;
        }

        foreach (IGrouping<string, ItemBrowserEntry> pack in items
                     .GroupBy(item => string.IsNullOrWhiteSpace(item.PackId)
                         ? "Unknown pack"
                         : item.PackId)
                     .OrderBy(group => group.Key,
                         StringComparer.OrdinalIgnoreCase))
        {
            string packKey = label + "|pack|" + pack.Key;
            bool packOpen = forcedOpen || expandedPacks.Contains(packKey);
            GUILayout.BeginHorizontal();
            GUILayout.Space(18f);
            if (GUILayout.Button(
                    $"{(packOpen ? "▼" : "▶")} {pack.Key} " +
                    $"({pack.Count()})"))
            {
                if (!expandedPacks.Add(packKey))
                    expandedPacks.Remove(packKey);
            }
            GUILayout.EndHorizontal();
            if (!packOpen)
                continue;

            DrawCategoryBranches(
                packKey,
                pack,
                forcedOpen,
                categoryIndent: 36f,
                itemIndent: 56f);
        }
    }

    private void DrawCategoryBranches(
        string parentKey,
        IEnumerable<ItemBrowserEntry> items,
        bool forcedOpen,
        float categoryIndent,
        float itemIndent)
    {
        foreach (IGrouping<string, ItemBrowserEntry> category in items
                     .GroupBy(item => item.Category)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            string key = parentKey + "|category|" + category.Key;
            bool categoryOpen =
                forcedOpen || expandedCategories.Contains(key);
            GUILayout.BeginHorizontal();
            GUILayout.Space(categoryIndent);
            if (GUILayout.Button(
                    $"{(categoryOpen ? "▼" : "▶")} {category.Key} " +
                    $"({category.Count()})"))
            {
                if (!expandedCategories.Add(key))
                    expandedCategories.Remove(key);
            }
            GUILayout.EndHorizontal();
            if (!categoryOpen)
                continue;

            foreach (ItemBrowserEntry item in category.OrderBy(
                         value => value.Name,
                         StringComparer.OrdinalIgnoreCase))
                DrawBrowserItem(item, itemIndent);
        }
    }

    private void DrawBrowserItem(ItemBrowserEntry item, float indent)
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Space(indent);
        GUILayout.Label(item.Name, GUILayout.MinWidth(260));
        GUILayout.Label($"{item.Value}g", GUILayout.Width(60));

        VendorDescriptor? vendor = SelectedVendor;
        bool alreadyAdded =
            vendor != null &&
            VendorStockStore.Find(SelectedPack, vendor.Id)?.items.Any(entry =>
                entry.itemName.Equals(
                    item.Name,
                    StringComparison.OrdinalIgnoreCase)) == true;
        bool alreadyExcluded =
            vendor != null &&
            VendorStockStore.Find(
                SelectedPack,
                vendor.Id)?.automaticStockExclusions.Any(itemName =>
                itemName.Equals(
                    item.Name,
                    StringComparison.OrdinalIgnoreCase)) == true;
        GUI.enabled = vendor != null && !alreadyAdded;
        if (GUILayout.Button(
                alreadyAdded ? "Fixed" : "Add Fixed",
                GUILayout.Width(90)))
            AddItem(vendor!, item.Name);
        GUI.enabled = vendor != null && !alreadyExcluded;
        if (GUILayout.Button(
                alreadyExcluded ? "Excluded" : "Exclude Auto",
                GUILayout.Width(112)))
            AddAutomaticStockExclusion(vendor!, item.Name);
        GUI.enabled = true;
        GUILayout.EndHorizontal();
    }

    private IEnumerable<ItemBrowserEntry> FilteredItems()
    {
        if (string.IsNullOrWhiteSpace(search))
            return browserItems;
        string term = search.Trim();
        return browserItems.Where(item =>
            item.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
            item.Category.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
            item.Description.IndexOf(
                term,
                StringComparison.OrdinalIgnoreCase) >= 0 ||
            item.PackId.IndexOf(
                term,
                StringComparison.OrdinalIgnoreCase) >= 0 ||
            (item.IsCustom ? "custom" : "base game").IndexOf(
                term,
                StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void AddItem(VendorDescriptor vendor, string itemName)
    {
        VendorStockRule rule = VendorStockStore.GetOrCreate(
            SelectedPack,
            vendor.Id);
        if (rule.items.Any(entry =>
                entry.itemName.Equals(
                    itemName,
                    StringComparison.OrdinalIgnoreCase)))
            return;

        rule.items.Add(new VendorStockEntry
        {
            itemName = itemName,
            quantity = 1
        });
        rule.items = rule.items
            .OrderBy(entry => entry.itemName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SetDirty();
        status = $"Added {itemName} to {vendor.Name} in pack " +
                 $"'{SelectedPack.name}'.";
    }

    private void AddAutomaticStockExclusion(
        VendorDescriptor vendor,
        string itemName)
    {
        VendorStockRule rule = VendorStockStore.GetOrCreate(
            SelectedPack,
            vendor.Id);
        if (rule.automaticStockExclusions.Any(value => value.Equals(
                itemName,
                StringComparison.OrdinalIgnoreCase)))
            return;

        rule.automaticStockExclusions.Add(itemName);
        rule.automaticStockExclusions = rule.automaticStockExclusions
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        exclusionsExpanded = true;
        SetDirty();
        status = $"Excluded {itemName} from automatic stock for " +
                 $"{vendor.Name} in pack '{SelectedPack.name}'.";
    }

    private void ClearVendorRule(string vendorId)
    {
        VendorStockRule? rule = VendorStockStore.Find(
            SelectedPack,
            vendorId);
        if (rule == null)
            return;
        SelectedPack.vendors.Remove(rule);
        quantityText.Clear();
        randomFieldText.Clear();
        collapsedRandomPools.Clear();
        openRandomSourceDropdown = "";
        SetDirty();
    }

    private void RefreshVendors(bool report)
    {
        string previous = selectedVendorId;
        List<VendorDescriptor> discovered = VendorCatalog.Discover();
        vendors.Clear();
        vendors.AddRange(discovered);
        if (vendors.Any(vendor =>
                vendor.Id.Equals(
                    previous,
                    StringComparison.OrdinalIgnoreCase)))
            selectedVendorId = previous;
        else
            selectedVendorId = vendors.FirstOrDefault()?.Id ?? "";

        if (report)
        {
            int custom = vendors.Count(vendor => vendor.IsCustom);
            status =
                $"Polled {vendors.Count} vendors; {custom} custom " +
                $"vendor-routine definition{(custom == 1 ? "" : "s")}.";
        }
    }

    private void RefreshItems()
    {
        browserItems.Clear();
        foreach (Item item in ItemLibrary.Items
                     .Where(value => value != null)
                     .OrderBy(value => value.itemCategory)
                     .ThenBy(value => value.name, StringComparer.OrdinalIgnoreCase))
        {
            if (item == null || string.IsNullOrWhiteSpace(item.name))
                continue;
            bool isCustom = item.spriteName?.StartsWith(
                "custom:",
                StringComparison.OrdinalIgnoreCase) == true;
            VendorStockService.TryGetCustomPackId(
                item,
                out string packId);
            browserItems.Add(new ItemBrowserEntry
            {
                Name = item.name,
                Description = item.description ?? "",
                Category = item.itemCategory.ToString(),
                PackId = packId,
                Value = item.value,
                IsCustom = isCustom
            });
        }
    }

    private void Reload()
    {
        VendorStockStore.Load();
        EnsureSelectedPack();
        dirty = false;
        quantityText.Clear();
        randomFieldText.Clear();
        collapsedRandomPools.Clear();
        openRandomSourceDropdown = "";
        RefreshItems();
        RefreshVendors(report: true);
        status = "Reloaded vendor-stock.json; unsaved editor changes were discarded.";
    }

    private void SaveAndApply()
    {
        try
        {
            VendorStockStore.Save();
            VendorStockService.ApplyAll(
                "editor save",
                rollRandomForAllVendors: true);
            dirty = false;
            RefreshVendors(report: false);
            status =
                $"Saved {VendorStockStore.Document.packs.Count} stock pack(s) " +
                "and applied enabled fixed and random stock to live vendors. " +
                "Exclusions apply on the next automatic stock cycle.";
        }
        catch (Exception exception)
        {
            status = "Save failed: " + exception.Message;
            Plugin.Log.LogError("Vendor stock editor save failed: " + exception);
        }
    }

    private void SetDirty()
    {
        dirty = true;
    }

    private void Close()
    {
        open = false;
        quantityText.Clear();
        randomFieldText.Clear();
        collapsedRandomPools.Clear();
        openRandomSourceDropdown = "";
        gameObject.SetActive(false);
        ReleaseSession();
    }
}
