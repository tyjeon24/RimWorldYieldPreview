using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace YieldPreview
{
    public class Settings : ModSettings
    {
        public bool chopWood = true;
        public bool cutPlants = true;
        public bool harvest = true;
        public bool mine = true;
        public bool stoneChunks = false;
        public bool deconstruct = true;
        public bool removeFloor = true;
        public bool growingZone = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref chopWood, "chopWood", true);
            Scribe_Values.Look(ref cutPlants, "cutPlants", true);
            Scribe_Values.Look(ref harvest, "harvest", true);
            Scribe_Values.Look(ref mine, "mine", true);
            Scribe_Values.Look(ref stoneChunks, "stoneChunks", false);
            Scribe_Values.Look(ref deconstruct, "deconstruct", true);
            Scribe_Values.Look(ref removeFloor, "removeFloor", true);
            Scribe_Values.Look(ref growingZone, "growingZone", true);
        }
    }

    public class YieldPreviewMod : Mod
    {
        public static Settings CurrentSettings;

        public YieldPreviewMod(ModContentPack content) : base(content)
        {
            CurrentSettings = GetSettings<Settings>();
            var harmony = new Harmony(content.PackageIdPlayerFacing);
            harmony.PatchAll(typeof(YieldPreviewMod).Assembly);
        }

        // $ IsEnabled(MineTool)         (settings.mine = true)
        // true
        // $ IsEnabled(RemoveFoundationTool)   (a RemoveFloor subclass, settings.removeFloor = false)
        // false
        // $ IsEnabled(BuildTool)
        // false
        public static bool IsEnabled(Designator designator)
        {
            var settings = CurrentSettings;
            if (designator is Designator_PlantsHarvestWood) { return settings.chopWood; }
            if (designator is Designator_PlantsCut) { return settings.cutPlants; }
            if (designator is Designator_PlantsHarvest) { return settings.harvest; }
            if (designator is Designator_Mine) { return settings.mine; }
            if (designator is Designator_Deconstruct) { return settings.deconstruct; }
            if (designator is Designator_RemoveFloor) { return settings.removeFloor; }
            if (designator is Designator_ZoneAdd_Growing) { return settings.growingZone; }
            return false;
        }

        public override string SettingsCategory()
        {
            return "Yield Preview";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            var settings = CurrentSettings;
            string mineLabel = DesignatorLabel(typeof(Designator_Mine));
            listing.CheckboxLabeled(DesignatorLabel(typeof(Designator_PlantsHarvestWood)), ref settings.chopWood);
            listing.CheckboxLabeled(DesignatorLabel(typeof(Designator_PlantsCut)), ref settings.cutPlants);
            listing.CheckboxLabeled(DesignatorLabel(typeof(Designator_PlantsHarvest)), ref settings.harvest);
            listing.CheckboxLabeled(mineLabel, ref settings.mine);
            listing.CheckboxLabeled(mineLabel + " - " + ThingCategoryDefOf.StoneChunks.LabelCap, ref settings.stoneChunks);
            listing.CheckboxLabeled(DesignatorLabel(typeof(Designator_Deconstruct)), ref settings.deconstruct);
            listing.CheckboxLabeled(DesignatorLabel(typeof(Designator_RemoveFloor)), ref settings.removeFloor);
            listing.CheckboxLabeled(DesignatorLabel(typeof(Designator_ZoneAdd_Growing)), ref settings.growingZone);
            listing.End();
        }

        // $ DesignatorLabel(typeof(Designator_Mine))
        // Mine                 (Korean game: 채굴)
        // $ DesignatorLabel(typeof(SomeUnknownDesignator))
        // SomeUnknownDesignator
        static string DesignatorLabel(Type designatorType)
        {
            foreach (var category in DefDatabase<DesignationCategoryDef>.AllDefs)
            {
                foreach (var designator in category.AllResolvedDesignators)
                {
                    if (designator.GetType() == designatorType)
                    {
                        return designator.LabelCap;
                    }
                }
            }
            return designatorType.Name;
        }
    }

    [HarmonyPatch(typeof(DesignationDragger), nameof(DesignationDragger.DraggerOnGUI))]
    public static class YieldPreview
    {
        static readonly YieldCalculator[] calculators =
        {
            new PlantCalculator(),
            new MineCalculator(),
            new DeconstructCalculator(),
            new RemoveFloorCalculator(),
            new GrowingZoneCalculator(),
        };

        static IntVec3 dragStartCell;
        static (Designator designator, IntVec3 start, IntVec3 end) lastDragKey;
        static string panelText;

        [HarmonyPatch(typeof(DesignationDragger), nameof(DesignationDragger.StartDrag)), HarmonyPostfix]
        static void OnStartDrag()
        {
            dragStartCell = UI.MouseCell();
            lastDragKey = default;
            panelText = null;
        }

        static void Postfix(DesignationDragger __instance)
        {
            var designator = Find.DesignatorManager.SelectedDesignator;
            bool notDragging = !__instance.Dragging;
            bool noDesignator = designator == null;
            bool previewTurnedOff = designator != null && !YieldPreviewMod.IsEnabled(designator);
            bool skipPreview = notDragging || noDesignator || previewTurnedOff;
            if (skipPreview)
            {
                lastDragKey = default;
                return;
            }

            var dragKey = (designator: designator, start: dragStartCell, end: UI.MouseCell());
            if (dragKey != lastDragKey)
            {
                lastDragKey = dragKey;
                var map = Find.CurrentMap;

                var dragRect = CellRect.FromLimits(dragKey.start, dragKey.end).ClipInsideMap(map);
                var cells = dragRect.Cells.Where(cell => !cell.Fogged(map)).ToList();

                try
                {
                    panelText = BuildPanelText(designator, cells, map);
                }
                catch (Exception exception)
                {
                    panelText = null;
                    Log.ErrorOnce("[Yield Preview] " + exception, designator.GetType().GetHashCode());
                }
            }

            if (panelText == null)
            {
                return;
            }

            var panelSize = Text.CalcSize(panelText) + new Vector2(16f, 8f);
            var mouse = Event.current.mousePosition;
            float panelX = Mathf.Max(0f, mouse.x - 22f - panelSize.x);
            var panelRect = new Rect(new Vector2(panelX, mouse.y + 40f), panelSize);
            Widgets.DrawBoxSolid(panelRect, new Color(0f, 0f, 0f, 0.7f));
            Widgets.Label(panelRect.ContractedBy(6f, 4f), panelText);
        }

        // $ BuildPanelText(MineTool, [steel, steel], map)
        // Mining yield 80% (Hana)
        // Steel x64
        // $ BuildPanelText(GrowingZoneTool, [10 soil cells], map)
        // Plant harvest yield 70% (Ru)
        // Potatoes x110 (Harvest botched 30%)
        // $ BuildPanelText(BuildTool, [any cells], map)
        // null                 (no calculator, no panel)
        // $ BuildPanelText(MineTool, [bare soil], map)
        // null                 (nothing to mine, no panel)
        static string BuildPanelText(Designator designator, List<IntVec3> cells, Map map)
        {
            var calculator = calculators.FirstOrDefault(c => c.Handles(designator));
            if (calculator == null)
            {
                return null;
            }
            var yields = calculator.Run(designator, cells, map);
            if (yields == null)
            {
                return null;
            }
            return FormatPanel(yields, calculator);
        }

        // $ FormatPanel([Berries 10, Berries 10, Berries 9.4], PlantCalculator)
        // Plant harvest yield 70% (Ru)
        // Berries x29 (Harvest botched 30%)
        //
        // $ FormatPanel([Steel 32, Steel 32, Silver 32], MineCalculator)
        // Mining yield 80% (Hana)
        // Steel x64
        // Silver x32
        static string FormatPanel(List<YieldEntry> yields, YieldCalculator calculator)
        {
            var totals = new Dictionary<ThingDef, float>();
            var failChances = new Dictionary<ThingDef, float>();
            var productOrder = new List<ThingDef>();
            foreach (var entry in yields)
            {
                if (!totals.ContainsKey(entry.Product))
                {
                    totals[entry.Product] = 0f;
                    failChances[entry.Product] = 0f;
                    productOrder.Add(entry.Product);
                }
                totals[entry.Product] += entry.Amount;
                failChances[entry.Product] = Mathf.Max(failChances[entry.Product], entry.FailChance);
            }

            var rows = new List<(ThingDef product, int total, float failChance)>();
            foreach (var product in productOrder)
            {
                int total = Mathf.RoundToInt(totals[product]);
                if (total > 0)
                {
                    rows.Add((product, total, failChances[product]));
                }
            }
            rows = rows.OrderByDescending(row => row.total).ToList();

            var lines = new List<string>();
            if (calculator.ShownStat != null)
            {
                string worker;
                if (calculator.WorstPawn != null)
                {
                    worker = $"{calculator.WorstFactor.ToStringPercent()} ({calculator.WorstPawn.LabelShort})";
                }
                else
                {
                    worker = "NoneBrackets".Translate();
                }
                lines.Add($"{calculator.ShownStat.LabelCap} {worker}");
            }

            string botchedLabel = ((string)"TextMote_HarvestFailed".Translate()).Replace("\n", " ");
            foreach (var row in rows)
            {
                string line = $"{row.product.LabelCap} x{row.total}";
                if (row.failChance > 0f)
                {
                    line = line + $" ({botchedLabel} {row.failChance.ToStringPercent()})";
                }
                lines.Add(line);
            }
            if (rows.Count == 0)
            {
                lines.Add("None".Translate());
            }
            return string.Join("\n", lines);
        }
    }
}
