using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Lumina;
using OmniToolbox.Items;
using OmniToolbox.Notifications;
using OmniToolbox.UI;

namespace OmniToolbox.LocalGlamourPreview;

public sealed unsafe partial class OmniGlamourPreview
{
    private static BetterGlamourItemSearchResult[]? ItemSearchIndex;
    private static BetterGlamourItemSearchResult[]? HairstyleSearchIndex;
    private static BetterGlamourItemSearchResult[]? GlassesSearchIndex;

    internal static readonly GlamourPart[] Parts =
    [
        new("Feature.BetterGlamourManagement.Part.Head", 2),
        new("Feature.BetterGlamourManagement.Part.Body", 3),
        new("Feature.BetterGlamourManagement.Part.Hands", 4),
        new("Feature.BetterGlamourManagement.Part.Legs", 6),
        new("Feature.BetterGlamourManagement.Part.Feet", 7),
        new("Feature.BetterGlamourManagement.Part.Ears", 8),
        new("Feature.BetterGlamourManagement.Part.Neck", 9),
        new("Feature.BetterGlamourManagement.Part.Wrists", 10),
        new("Feature.BetterGlamourManagement.Part.RingRight", 11),
        new("Feature.BetterGlamourManagement.Part.RingLeft", 12)
    ];

    private static List<GlamourDyeOption>? CachedDyeOptions;

    private void NormalizeSelection()
    {
        if (config.Presets.Count == 0)
        {
            selectedPresetIndex = -1;
        }
        else if (selectedPresetIndex < 0 || selectedPresetIndex >= config.Presets.Count)
        {
            selectedPresetIndex = 0;
        }
    }

    internal static bool TryGetGearsetName(int index, out string name)
    {
        name = string.Empty;
        var module = RaptureGearsetModule.Instance();
        var gearset = module != null ? module->GetGearset(index) : null;
        if (gearset == null ||
            gearset->Id != index ||
            !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
        {
            return false;
        }

        name = gearset->NameString;
        return !string.IsNullOrWhiteSpace(name);
    }

    internal static BetterGlamourItem GetOrCreateItem(BetterGlamourPreset preset, int slot)
    {
        var item = preset.Items.Find(entry => entry.Slot == slot);
        if (item is not null)
        {
            return item;
        }

        item = new()
        {
            Slot = slot
        };
        preset.Items.Add(item);
        return item;
    }

    private void CopyPreset(BetterGlamourPreset preset)
    {
        ImGui.SetClipboardText(GlamourClipboard.Export(preset));
        OmniNotifier.Popup(
            Info.Title,
            OmniLoc.Get("Feature.BetterGlamourManagement.Copied"),
            Dalamud.Interface.ImGuiNotification.NotificationType.Success);
    }

    private void ImportClipboard()
    {
        try
        {
            if (config.Presets.Count >= MaxPresetCount) throw new InvalidOperationException($"最多保存 {MaxPresetCount} 个条目。");
            var preset = GlamourClipboard.Import(ImGui.GetClipboardText());
            // Resolve IDs before saving anything; a failed import must leave the collection unchanged.
            foreach (var item in preset.Items.Where(x => x.ItemID != 0))
            {
                uint id = item.ItemID > 1000000 ? item.ItemID - 1000000 : item.ItemID > 500000 ? item.ItemID - 500000 : item.ItemID;
                if (!LuminaGetter.TryGetRow<Item>(id, out _)) throw new InvalidDataException($"物品 ID 不存在：{item.ItemID}");
            }
            foreach (var dye in preset.Items.SelectMany(x => new[] {x.Stain0, x.Stain1}).Distinct().Where(x=>x!=0))
                if (!LuminaGetter.TryGetRow<Stain>(dye, out _)) throw new InvalidDataException($"染色 ID 不存在：{dye}");
            if (preset.HairstyleID != 0 && !LuminaGetter.TryGetRow<CharaMakeCustomize>(preset.HairstyleID, out _)) throw new InvalidDataException("发型 ID 不存在。");
            if (preset.GlassesID != 0 && !LuminaGetter.TryGetRow<Glasses>(preset.GlassesID, out _)) throw new InvalidDataException("面部装饰 ID 不存在。");
            int oldSelection = selectedPresetIndex;
            config.Presets.Add(preset);
            selectedPresetIndex = config.Presets.Count - 1;
            try { saveConfig(); }
            catch { config.Presets.Remove(preset); selectedPresetIndex = oldSelection; throw; }
            managerNativeUI?.UpdateData(config.Presets, selectedPresetIndex);
            QueuePortraitPreview(preset);
            OmniNotifier.Popup(Info.Title, $"已导入新条目：{preset.Name}", Dalamud.Interface.ImGuiNotification.NotificationType.Success);
        }
        catch (Exception error)
        {
            OmniNotifier.Popup(Info.Title, "导入失败：" + error.Message, Dalamud.Interface.ImGuiNotification.NotificationType.Error);
        }
    }

    private static void AppendPair(
        StringBuilder builder,
        BetterGlamourPreset preset,
        GlamourPart left,
        GlamourPart right) => builder.AppendLine(string.Format(
        OmniLoc.Get("Feature.BetterGlamourManagement.Export.Pair"),
        OmniLoc.Get(left.TextKey),
        FormatItem(GetOrCreateItem(preset, left.Slot)),
        OmniLoc.Get(right.TextKey),
        FormatItem(GetOrCreateItem(preset, right.Slot))));

    private static string FormatWeapon(BetterGlamourPreset preset)
    {
        var mainHand = FormatItem(GetOrCreateItem(preset, 0));
        var offHand = GetOrCreateItem(preset, 1);
        return offHand.ItemID == 0
            ? mainHand
            : $"{OmniLoc.Get("Feature.BetterGlamourManagement.MainHand")}：{mainHand}；{OmniLoc.Get("Feature.BetterGlamourManagement.OffHand")}：{FormatItem(offHand)}";
    }

    private static string FormatItem(BetterGlamourItem item)
    {
        if (item.ItemID == 0)
        {
            return OmniLoc.Get("Feature.BetterGlamourManagement.None");
        }

        if (!LuminaGetter.TryGetRow<Item>(item.ItemID, out var row))
        {
            return string.Format(OmniLoc.Get("Feature.BetterGlamourManagement.UnknownItem"), item.ItemID);
        }

        var name = row.Name.ExtractText();
        return row.DyeCount switch
        {
            0 => name,
            1 => $"{name} {GetDyeName(item.Stain0)}",
            _ => $"{name} {GetDyeName(item.Stain0)} {GetDyeName(item.Stain1)}"
        };
    }

    internal static string GetItemName(uint itemID)
    {
        if (itemID == 0)
        {
            return OmniLoc.Get("Feature.BetterGlamourManagement.None");
        }

        var name = LuminaWrapper.GetItemName(itemID);
        return string.IsNullOrWhiteSpace(name)
            ? string.Format(OmniLoc.Get("Feature.BetterGlamourManagement.UnknownItem"), itemID)
            : name;
    }

    internal static List<BetterGlamourItemSearchResult> SearchItems(string query)
    {
        ItemSearchIndex ??= BuildItemSearchIndex();
        return SearchItems(query, ItemSearchIndex);
    }

    internal static List<BetterGlamourItemSearchResult> SearchHairstyles(string query)
    {
        HairstyleSearchIndex ??= BuildHairstyleSearchIndex();
        return SearchItems(query, HairstyleSearchIndex);
    }

    internal static List<BetterGlamourItemSearchResult> SearchGlasses(string query)
    {
        GlassesSearchIndex ??= BuildGlassesSearchIndex();
        return SearchItems(query, GlassesSearchIndex);
    }

    private static List<BetterGlamourItemSearchResult> SearchItems(
        string query,
        BetterGlamourItemSearchResult[] searchIndex)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        var results = new List<BetterGlamourItemSearchResult>();
        for (var index = 0; index < searchIndex.Length; index++)
        {
            if (searchIndex[index].Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(searchIndex[index]);
            }
        }

        for (var index = 0; index < searchIndex.Length; index++)
        {
            if (!searchIndex[index].Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                searchIndex[index].Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(searchIndex[index]);
        }

        return results;
    }

    private static BetterGlamourItemSearchResult[] BuildItemSearchIndex()
    {
        var items = new List<BetterGlamourItemSearchResult>
        {
            new(0, OmniLoc.Get("Feature.BetterGlamourManagement.None"), 0)
        };
        foreach (var item in LuminaGetter.Get<Item>())
        {
            if (item.RowId == 0 ||
                item.Name.IsEmpty ||
                item.EquipSlotCategory.RowId == 0 ||
                item.ModelMain == 0)
            {
                continue;
            }

            items.Add(new(item.RowId, item.Name.ExtractText(), (uint)item.Icon));
        }

        return [.. items];
    }

    private static BetterGlamourItemSearchResult[] BuildHairstyleSearchIndex()
    {
        var hairstyles = new List<BetterGlamourItemSearchResult>
        {
            new(0, OmniLoc.Get("Feature.BetterGlamourManagement.None"), 0)
        };
        foreach (var hairstyle in LuminaGetter.Get<CharaMakeCustomize>().Where(static row => row.HintItem.RowId != 0))
        {
            var name = GetHairstyleName(hairstyle.RowId);
            if (string.IsNullOrWhiteSpace(name) || name == OmniLoc.Get("Feature.BetterGlamourManagement.None"))
            {
                continue;
            }

            hairstyles.Add(new(hairstyle.RowId, name, (uint)hairstyle.Icon));
        }

        return [.. hairstyles];
    }

    private static BetterGlamourItemSearchResult[] BuildGlassesSearchIndex()
    {
        var glasses = new List<BetterGlamourItemSearchResult>
        {
            new(0, OmniLoc.Get("Feature.BetterGlamourManagement.None"), 0)
        };
        foreach (var row in LuminaGetter.Get<Glasses>())
        {
            if (row.RowId == 0 || row.Name.IsEmpty)
            {
                continue;
            }

            glasses.Add(new(row.RowId, row.Name.ExtractText(), (uint)row.Icon));
        }

        return [.. glasses];
    }

    internal static string GetGlassesName(ushort glassesID)
    {
        if (glassesID == 0)
        {
            return OmniLoc.Get("Feature.BetterGlamourManagement.None");
        }

        if (LuminaGetter.TryGetRow<Glasses>(glassesID, out var glasses))
        {
            var name = glasses.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return string.Format(OmniLoc.Get("Feature.BetterGlamourManagement.UnknownGlasses"), glassesID);
    }

    internal static string GetHairstyleName(uint hairstyleID)
    {
        if (hairstyleID == 0)
        {
            return OmniLoc.Get("Feature.BetterGlamourManagement.None");
        }

        if (LuminaGetter.TryGetRow<CharaMakeCustomize>(hairstyleID, out var hairstyle))
        {
            var name = hairstyle.HintItem.Value.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return $"{OmniLoc.Get("Feature.BetterGlamourManagement.Hairstyle")} {hairstyle.FeatureID}";
        }

        return string.Format(OmniLoc.Get("Feature.BetterGlamourManagement.UnknownHairstyle"), hairstyleID);
    }

    internal static uint GetGlassesIcon(ushort glassesID) =>
        LuminaGetter.TryGetRow<Glasses>(glassesID, out var glasses) ? (uint)glasses.Icon : 0;

    internal static uint GetHairstyleIcon(uint hairstyleID) =>
        LuminaGetter.TryGetRow<CharaMakeCustomize>(hairstyleID, out var hairstyle) ? (uint)hairstyle.Icon : 0;

    internal static uint ResolveInspectHairstyleID(CustomizeData customizeData)
    {
        foreach (var hairMakeType in LuminaGetter.Get<HairMakeType>())
        {
            if (hairMakeType.Tribe.RowId != customizeData.Tribe || hairMakeType.Gender != customizeData.Sex)
            {
                continue;
            }

            foreach (var rowID in hairMakeType.CharaMakeStruct[0].SubMenuParam)
            {
                if (LuminaGetter.TryGetRow<CharaMakeCustomize>(rowID, out var hairstyle) &&
                    hairstyle.FeatureID == customizeData.Hairstyle)
                {
                    return hairstyle.RowId;
                }
            }

            break;
        }

        return 0;
    }

    internal static string GetDyeName(byte stainID) => stainID == 0
        ? OmniLoc.Get("Feature.BetterGlamourManagement.NoDye")
        : DyeOptions.Find(option => option.ID == stainID) is { } dye
            ? dye.Name
            : stainID.ToString();

    internal static List<GlamourDyeOption> DyeOptions
    {
        get
        {
            if (CachedDyeOptions is not null)
            {
                return CachedDyeOptions;
            }

            CachedDyeOptions = [new(0, OmniLoc.Get("Feature.BetterGlamourManagement.NoDye"), Vector4.Zero)];
            foreach (var stain in LuminaGetter.Get<Stain>())
            {
                if (stain.RowId is 0 or > byte.MaxValue)
                {
                    continue;
                }

                var name = stain.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    CachedDyeOptions.Add(new((byte)stain.RowId, name, stain.Color.ReverseToVector4()));
                }
            }

            return CachedDyeOptions;
        }
    }

    internal readonly record struct GlamourPart(string TextKey, int Slot);

    internal readonly record struct GlamourDyeOption(byte ID, string Name, Vector4 Color);
}

internal readonly record struct BetterGlamourItemSearchResult(uint ItemID, string Name, uint IconID);
