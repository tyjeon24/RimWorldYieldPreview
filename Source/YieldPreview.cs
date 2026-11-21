using System;
using System.Collections.Generic;
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
        static IntVec3 dragStartCell;

        static readonly Func<Designator, DesignationDef> getDesignationDef =
            AccessTools.MethodDelegate<Func<Designator, DesignationDef>>(AccessTools.PropertyGetter(typeof(Designator), "Designation"));

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

                var cells = new List<IntVec3>();
                var dragRect = CellRect.FromLimits(dragKey.start, dragKey.end).ClipInsideMap(map);
                foreach (var cell in dragRect.Cells)
                {
                    if (!cell.Fogged(map))
                    {
                        cells.Add(cell);
                    }
                }

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

            var panelPosition = Event.current.mousePosition + new Vector2(22f, 40f);
            var panelSize = Text.CalcSize(panelText) + new Vector2(16f, 8f);
            var panelRect = new Rect(panelPosition, panelSize);
            Widgets.DrawBoxSolid(panelRect, new Color(0f, 0f, 0f, 0.7f));
            Widgets.Label(panelRect.ContractedBy(6f, 4f), panelText);
        }

        static string BuildPanelText(Designator designator, List<IntVec3> cells, Map map)
        {
            var yields = new List<(ThingDef product, float amount, float failChance)>();

            StatDef shownStat = null;
            float worstWorkerFactor = float.MaxValue;
            string worstWorkerName = null;

            Pawn FindWorstWorker(WorkTypeDef[] workTypes, StatDef stat, Func<Pawn, float> expectedFactor)
            {
                shownStat = stat;
                var workers = new List<Pawn>();
                foreach (var pawn in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
                {
                    if (DoesAnyOfWork(pawn, workTypes))
                    {
                        workers.Add(pawn);
                    }
                }
                if (workers.Count == 0)
                {
                    return null;
                }

                var worstPawn = GenCollection.MinBy(workers, expectedFactor);
                float worstFactor = expectedFactor(worstPawn);

                if (worstFactor < worstWorkerFactor)
                {
                    worstWorkerFactor = worstFactor;
                    worstWorkerName = worstPawn.LabelShort;
                }
                return worstPawn;
            }

            var plantFactorCache = new Dictionary<(WorkTypeDef[], ThingDef, bool), (float multiplier, float failChance)>();

            (float multiplier, float failChance) PlantFactor(WorkTypeDef[] workTypes, ThingDef plantDef, bool blighted)
            {
                var cacheKey = (workTypes, plantDef, blighted);
                if (plantFactorCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }

                var plantProps = plantDef.plant;
                StatDef stat;
                bool producesDrug = plantProps.harvestedThingDef.IsDrug;
                bool isDrugCrop = plantProps.drugForHarvestPurposes;
                bool useDrugHarvestStat = producesDrug || isDrugCrop;
                if (useDrugHarvestStat)
                {
                    stat = StatDefOf.DrugHarvestYield;
                }
                else
                {
                    stat = StatDefOf.PlantHarvestYield;
                }

                (float multiplier, float failChance) FactorFor(Pawn pawn)
                {
                    float statValue = pawn.GetStatValue(stat);
                    float multiplier = Mathf.Max(1f, statValue);
                    float failChance = 0f;
                    bool canFail = pawn.RaceProps.Humanlike && plantProps.harvestFailable && !blighted;
                    if (canFail)
                    {
                        failChance = Mathf.Max(0f, 1f - statValue);
                    }
                    return (multiplier, failChance);
                }

                float ExpectedFactorFor(Pawn pawn)
                {
                    var factor = FactorFor(pawn);
                    return factor.multiplier * (1f - factor.failChance);
                }

                var worstPawn = FindWorstWorker(workTypes, stat, ExpectedFactorFor);
                (float multiplier, float failChance) result;
                if (worstPawn == null)
                {
                    result = (1f, 0f);
                }
                else
                {
                    result = FactorFor(worstPawn);
                }
                plantFactorCache[cacheKey] = result;
                return result;
            }

            WorkTypeDef[] cutters = { WorkTypeDefOf.PlantCutting };
            WorkTypeDef[] cuttersOrGrowers = { WorkTypeDefOf.PlantCutting, WorkTypeDefOf.Growing };

            WorkTypeDef[] HarvestersOf(Plant plant)
            {
                bool inGrowingZone = map.zoneManager.ZoneAt(plant.Position) is Zone_Growing;
                bool inPlantPot = plant.Position.GetEdifice(map) is IPlantToGrowSettable;
                bool growersHarvestToo = inGrowingZone || inPlantPot;
                if (growersHarvestToo)
                {
                    return cuttersOrGrowers;
                }
                return cutters;
            }

            float MiningStatOf(Pawn pawn)
            {
                return pawn.GetStatValue(StatDefOf.MiningYield);
            }

            if (designator is Designator_ZoneAdd_Growing)
            {
                var zone = Find.Selector.SelectedZone as Zone_Growing;
                ThingDef crop = ThingDefOf.Plant_Potato;
                if (zone != null)
                {
                    crop = zone.GetPlantDefToGrow();
                }

                var zoneCells = new List<IntVec3>();
                foreach (var cell in cells)
                {
                    bool canAdd = designator.CanDesignateCell(cell).Accepted;
                    bool alreadyInZone = zone != null && map.zoneManager.ZoneAt(cell) == zone;
                    bool includeCell = canAdd || alreadyInZone;
                    if (includeCell)
                    {
                        zoneCells.Add(cell);
                    }
                }
                cells = zoneCells;

                var product = crop.plant.harvestedThingDef;
                if (product != null)
                {
                    var factor = PlantFactor(new[] { WorkTypeDefOf.Growing }, crop, false);
                    float difficulty = 1f;
                    if (crop.plant.harvestYieldAffectedByDifficulty)
                    {
                        difficulty = Find.Storyteller.difficulty.cropYieldFactor;
                    }
                    float amount = cells.Count * crop.plant.harvestYield * factor.multiplier * difficulty;
                    yields.Add((product, amount, factor.failChance));
                }
            }
            else
            {
                var designations = map.designationManager;

                var designationDef = getDesignationDef(designator);
                if (designationDef == null)
                {
                    if (designator is Designator_RemoveFoundation)
                    {
                        designationDef = DesignationDefOf.RemoveFoundation;
                    }
                    else if (designator is Designator_RemoveFloor)
                    {
                        designationDef = DesignationDefOf.RemoveFloor;
                    }
                }
                bool isCellDesignation = designationDef != null && designationDef.targetType == TargetType.Cell;

                bool IsThingAlreadyDesignated(Thing thing)
                {
                    return designationDef != null && !isCellDesignation && designations.DesignationOn(thing, designationDef) != null;
                }

                bool IsCellAlreadyDesignated(IntVec3 cell)
                {
                    if (designationDef == null)
                    {
                        return false;
                    }
                    if (isCellDesignation)
                    {
                        return designations.DesignationAt(cell, designationDef) != null;
                    }
                    foreach (var thing in cell.GetThingList(map))
                    {
                        if (IsThingAlreadyDesignated(thing))
                        {
                            return true;
                        }
                    }
                    return false;
                }

                var selectedCells = new List<IntVec3>();
                foreach (var cell in cells)
                {
                    bool canDesignateCell = designator.CanDesignateCell(cell).Accepted;
                    bool cellAlreadyDesignated = IsCellAlreadyDesignated(cell);
                    bool isSelectedCell = canDesignateCell || cellAlreadyDesignated;
                    if (isSelectedCell)
                    {
                        selectedCells.Add(cell);
                    }
                }
                cells = selectedCells;

                var things = new List<Thing>();
                var seenThings = new HashSet<Thing>();
                foreach (var cell in cells)
                {
                    foreach (var thing in cell.GetThingList(map))
                    {
                        if (!seenThings.Add(thing))
                        {
                            continue;
                        }
                        bool canDesignateThing = designator.CanDesignateThing(thing).Accepted;
                        bool thingAlreadyDesignated = IsThingAlreadyDesignated(thing);
                        bool isSelectedThing = canDesignateThing || thingAlreadyDesignated;
                        if (isSelectedThing)
                        {
                            things.Add(thing);
                        }
                    }
                }

                if (designator is Designator_Mine)
                {
                    var miner = FindWorstWorker(new[] { WorkTypeDefOf.Mining }, StatDefOf.MiningYield, MiningStatOf);
                    float miningFactor = 1f;
                    if (miner != null)
                    {
                        miningFactor = miner.GetStatValue(StatDefOf.MiningYield);
                    }
                    bool includeChunks = YieldPreviewMod.CurrentSettings.stoneChunks;

                    var seenRocks = new HashSet<Thing>();
                    foreach (var cell in cells)
                    {
                        var rock = cell.GetFirstMineable(map);
                        bool noRock = rock == null;
                        bool rockAlreadyCounted = rock != null && !seenRocks.Add(rock);
                        bool skipRock = noRock || rockAlreadyCounted;
                        if (skipRock)
                        {
                            continue;
                        }
                        var building = rock.def.building;
                        if (building.mineableThing == null)
                        {
                            continue;
                        }
                        if (!includeChunks && building.mineableThing.IsWithinCategory(ThingCategoryDefOf.StoneChunks))
                        {
                            continue;
                        }

                        float statFactor = 1f;
                        if (building.mineableYieldWasteable)
                        {
                            statFactor = miningFactor;
                        }
                        float amount = building.mineableDropChance * Mathf.Max(1f, building.EffectiveMineableYield * statFactor);
                        yields.Add((building.mineableThing, amount, 0f));
                    }
                }
                else if (designator is Designator_RemoveFloor)
                {
                    foreach (var cell in cells)
                    {
                        TerrainDef terrain;
                        if (designator is Designator_RemoveFoundation)
                        {
                            terrain = map.terrainGrid.FoundationAt(cell);
                        }
                        else
                        {
                            terrain = map.terrainGrid.TopTerrainAt(cell);
                        }
                        if (terrain == null)
                        {
                            continue;
                        }

                        foreach (var cost in terrain.CostListAdjusted(null))
                        {
                            yields.Add((cost.thingDef, cost.count * terrain.resourcesFractionWhenDeconstructed, 0f));
                        }
                    }
                }
                else if (designator is Designator_Deconstruct)
                {
                    foreach (var thing in things)
                    {
                        bool isFrame = thing is Frame;
                        bool leavesNoResources = !isFrame && !GenLeaving.CanBuildingLeaveResources(thing, DestroyMode.Deconstruct);
                        bool skipThing = isFrame || leavesNoResources;
                        if (skipThing)
                        {
                            continue;
                        }
                        foreach (var cost in thing.CostListAdjusted())
                        {
                            float refund = Mathf.Min(cost.count * thing.def.resourcesFractionWhenDeconstructed, cost.count);
                            yields.Add((cost.thingDef, refund, 0f));
                        }
                    }
                }
                else
                {
                    foreach (var thing in things)
                    {
                        var plant = thing as Plant;
                        bool notPlant = plant == null;
                        bool noProduct = plant != null && plant.def.plant.harvestedThingDef == null;
                        bool skipThing = notPlant || noProduct;
                        if (skipThing)
                        {
                            continue;
                        }
                        var factor = PlantFactor(HarvestersOf(plant), plant.def, plant.Blighted);
                        yields.Add((plant.def.plant.harvestedThingDef, BaseYield(plant) * factor.multiplier, factor.failChance));
                    }
                }
            }

            if (cells.Count == 0)
            {
                return null;
            }

            var totals = new Dictionary<ThingDef, float>();
            var failChances = new Dictionary<ThingDef, float>();
            var productOrder = new List<ThingDef>();
            foreach (var entry in yields)
            {
                if (!totals.ContainsKey(entry.product))
                {
                    totals[entry.product] = 0f;
                    failChances[entry.product] = 0f;
                    productOrder.Add(entry.product);
                }
                totals[entry.product] += entry.amount;
                failChances[entry.product] = Mathf.Max(failChances[entry.product], entry.failChance);
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
            SortByTotalDescending(rows);

            string botchedLabel = ((string)"TextMote_HarvestFailed".Translate()).Replace("\n", " ");
            var lines = new List<string>();
            if (shownStat != null)
            {
                string worker;
                if (worstWorkerName != null)
                {
                    worker = $"{worstWorkerFactor.ToStringPercent()} ({worstWorkerName})";
                }
                else
                {
                    worker = "NoneBrackets".Translate();
                }
                lines.Add($"{shownStat.LabelCap} {worker}");
            }
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

        static bool DoesAnyOfWork(Pawn pawn, WorkTypeDef[] workTypes)
        {
            bool noWorkSettings = pawn.workSettings == null;
            bool neverWorks = pawn.workSettings != null && !pawn.workSettings.EverWork;
            bool cannotWork = noWorkSettings || neverWorks;
            if (cannotWork)
            {
                return false;
            }
            foreach (var workType in workTypes)
            {
                if (!pawn.WorkTypeIsDisabled(workType) && pawn.workSettings.WorkIsActive(workType))
                {
                    return true;
                }
            }
            return false;
        }

        static void SortByTotalDescending(List<(ThingDef product, int total, float failChance)> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                var current = rows[i];
                int j = i - 1;
                while (j >= 0 && rows[j].total < current.total)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = current;
            }
        }

        static float BaseYield(Plant plant)
        {
            if (!plant.CanYieldNow())
            {
                return 0f;
            }
            var plantProps = plant.def.plant;
            float growthFactor = 0.5f + Mathf.InverseLerp(plantProps.harvestMinGrowth, 1f, plant.Growth) * 0.5f;
            float healthFactor = Mathf.Lerp(0.5f, 1f, (float)plant.HitPoints / plant.MaxHitPoints);
            float yield = plantProps.harvestYield * growthFactor * healthFactor;
            if (plantProps.harvestYieldAffectedByDifficulty)
            {
                yield = yield * Find.Storyteller.difficulty.cropYieldFactor;
            }
            return yield;
        }
    }
}
