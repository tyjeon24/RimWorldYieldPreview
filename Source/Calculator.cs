using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace YieldPreview
{
    public class YieldEntry
    {
        public ThingDef Product;
        public float Amount;
        public float FailChance;

        public YieldEntry(ThingDef product, float amount, float failChance)
        {
            Product = product;
            Amount = amount;
            FailChance = failChance;
        }
    }

    public abstract class YieldCalculator
    {
        static readonly Func<Designator, DesignationDef> getDesignationDef =
            AccessTools.MethodDelegate<Func<Designator, DesignationDef>>(AccessTools.PropertyGetter(typeof(Designator), "Designation"));

        public StatDef ShownStat { get; private set; }
        public Pawn WorstPawn { get; private set; }
        public float WorstFactor { get; private set; } = float.MaxValue;
        protected Map Map { get; private set; }

        public abstract bool Handles(Designator designator);

        protected abstract bool IsAlreadyApplied(IntVec3 cell, Designator designator);

        protected abstract List<YieldEntry> Calculate(Designator designator, List<IntVec3> cells);

        // $ mineCalculator.Run(MineTool, [steel, steel, bare soil], map)
        // [Steel 32, Steel 32]            (soil dropped by SelectCells)
        // $ mineCalculator.Run(MineTool, [bare soil], map)
        // null
        public List<YieldEntry> Run(Designator designator, List<IntVec3> cells, Map map)
        {
            StartCalculation(map);
            var selectedCells = SelectCells(cells, designator);
            if (selectedCells.Count == 0)
            {
                return null;
            }
            return Calculate(designator, selectedCells);
        }

        void StartCalculation(Map map)
        {
            Map = map;
            ShownStat = null;
            WorstPawn = null;
            WorstFactor = float.MaxValue;
        }

        // $ SelectCells([granite, bare soil, steel already designated], MineTool)
        // [granite, steel already designated]
        protected List<IntVec3> SelectCells(List<IntVec3> cells, Designator designator)
        {
            return cells.Where(cell => designator.CanDesignateCell(cell).Accepted || IsAlreadyApplied(cell, designator)).ToList();
        }

        // $ FindWorstPawn(MiningYield, Mining)        (miners: Hana 80%, Tom 110%)
        // (Hana, 0.8)
        // $ FindWorstPawn(MiningYield, Mining)        (nobody has Mining on)
        // (null, 1)
        protected (Pawn pawn, float factor) FindWorstPawn(StatDef stat, params WorkTypeDef[] workTypes)
        {
            float StatValueOf(Pawn pawn)
            {
                return pawn.GetStatValue(stat);
            }
            return FindWorstPawn(stat, StatValueOf, workTypes);
        }

        // $ FindWorstPawn(PlantHarvestYield, ExpectedFactorFor, PlantCutting)   (Ru human 70% -> 0.7, mech 50% -> 1.0)
        // (Ru, 0.7)
        protected (Pawn pawn, float factor) FindWorstPawn(StatDef stat, Func<Pawn, float> expectedFactor, params WorkTypeDef[] workTypes)
        {
            ShownStat = stat;

            var worstPawn = Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer)
                .Where(pawn => DoesAnyOfWork(pawn, workTypes))
                .OrderBy(expectedFactor)
                .FirstOrDefault();
            if (worstPawn == null)
            {
                return (null, 1f);
            }
            float worstFactor = expectedFactor(worstPawn);

            if (worstFactor < WorstFactor)
            {
                WorstPawn = worstPawn;
                WorstFactor = worstFactor;
            }
            return (worstPawn, worstFactor);
        }

        // $ PlantHarvestFactor(Plant_Potato, blighted: false, Growing)          (worst: Ru, human, 70%)
        // (1.0, 0.3)
        // $ PlantHarvestFactor(Plant_TreeOak, blighted: false, PlantCutting)    (tree: never botched)
        // (1.0, 0)
        // $ PlantHarvestFactor(Plant_Potato, blighted: false, Growing)          (only grower: Tom, 120%)
        // (1.2, 0)
        // $ PlantHarvestFactor(Plant_Potato, blighted: false, Growing)          (nobody assigned)
        // (1.0, 0)
        protected (float multiplier, float failChance) PlantHarvestFactor(ThingDef plantDef, bool blighted, params WorkTypeDef[] workTypes)
        {
            var plantProps = plantDef.plant;
            bool producesDrug = plantProps.harvestedThingDef.IsDrug;
            bool isDrugCrop = plantProps.drugForHarvestPurposes;
            bool useDrugHarvestStat = producesDrug || isDrugCrop;
            StatDef stat = StatDefOf.PlantHarvestYield;
            if (useDrugHarvestStat)
            {
                stat = StatDefOf.DrugHarvestYield;
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
                var (multiplier, failChance) = FactorFor(pawn);
                return multiplier * (1f - failChance);
            }

            var (worstPawn, _) = FindWorstPawn(stat, ExpectedFactorFor, workTypes);
            if (worstPawn == null)
            {
                return (1f, 0f);
            }
            return FactorFor(worstPawn);
        }

        // $ IsAlreadyDesignated(steel cell with a Mine designation, MineTool)
        // true
        // $ IsAlreadyDesignated(cell with a tree marked for chopping, ChopTool)
        // true
        // $ IsAlreadyDesignated(untouched granite cell, MineTool)
        // false
        protected bool IsAlreadyDesignated(IntVec3 cell, Designator designator)
        {
            var designationDef = DesignationOf(designator);
            if (designationDef == null)
            {
                return false;
            }
            if (designationDef.targetType == TargetType.Cell)
            {
                return Map.designationManager.DesignationAt(cell, designationDef) != null;
            }
            return cell.GetThingList(Map).Any(thing => Map.designationManager.DesignationOn(thing, designationDef) != null);
        }

        // $ SelectThings([cell A: oak + grass, cell B: oak], ChopTool)
        // [oak A, oak B]                  (grass can't be chopped)
        // $ SelectThings([2 cells of one 2x1 table], DeconstructTool)
        // [table]                         (counted once)
        protected List<Thing> SelectThings(List<IntVec3> cells, Designator designator)
        {
            var designationDef = DesignationOf(designator);
            bool isThingDesignation = designationDef != null && designationDef.targetType != TargetType.Cell;

            var things = new List<Thing>();
            var seenThings = new HashSet<Thing>();
            foreach (var cell in cells)
            {
                foreach (var thing in cell.GetThingList(Map))
                {
                    if (!seenThings.Add(thing))
                    {
                        continue;
                    }
                    bool canDesignateThing = designator.CanDesignateThing(thing).Accepted;
                    bool thingAlreadyDesignated = isThingDesignation && Map.designationManager.DesignationOn(thing, designationDef) != null;
                    bool isSelectedThing = canDesignateThing || thingAlreadyDesignated;
                    if (isSelectedThing)
                    {
                        things.Add(thing);
                    }
                }
            }
            return things;
        }

        // $ DesignationOf(MineTool)
        // Mine
        // $ DesignationOf(RemoveFloorTool)          (game returns null for this tool)
        // RemoveFloor
        // $ DesignationOf(RemoveFoundationTool)
        // RemoveFoundation
        protected static DesignationDef DesignationOf(Designator designator)
        {
            var designationDef = getDesignationDef(designator);
            if (designationDef != null)
            {
                return designationDef;
            }
            if (designator is Designator_RemoveFoundation)
            {
                return DesignationDefOf.RemoveFoundation;
            }
            if (designator is Designator_RemoveFloor)
            {
                return DesignationDefOf.RemoveFloor;
            }
            return null;
        }

        // $ DoesAnyOfWork(Hana, [Mining])            (Mining checked in the work tab)
        // true
        // $ DoesAnyOfWork(Hana, [PlantCutting, Growing])   (both unchecked)
        // false
        // $ DoesAnyOfWork(muffalo, [Mining])         (animal: no work settings)
        // false
        static bool DoesAnyOfWork(Pawn pawn, WorkTypeDef[] workTypes)
        {
            bool noWorkSettings = pawn.workSettings == null;
            bool neverWorks = pawn.workSettings != null && !pawn.workSettings.EverWork;
            bool cannotWork = noWorkSettings || neverWorks;
            if (cannotWork)
            {
                return false;
            }
            return workTypes.Any(workType => !pawn.WorkTypeIsDisabled(workType) && pawn.workSettings.WorkIsActive(workType));
        }
    }

    public class GrowingZoneCalculator : YieldCalculator
    {
        public override bool Handles(Designator designator)
        {
            return designator is Designator_ZoneAdd_Growing;
        }

        // $ IsAlreadyApplied(cell inside the selected potato zone, GrowingZoneTool)
        // true
        // $ IsAlreadyApplied(cell outside it, GrowingZoneTool)
        // false
        protected override bool IsAlreadyApplied(IntVec3 cell, Designator designator)
        {
            var zone = Find.Selector.SelectedZone as Zone_Growing;
            return zone != null && Map.zoneManager.ZoneAt(cell) == zone;
        }

        // $ Calculate(GrowingZoneTool, [10 soil cells])     (potato zone selected, worst grower Ru 70%)
        // [Potatoes 110, botched 30%]                         (10 cells x 11 per harvest)
        protected override List<YieldEntry> Calculate(Designator designator, List<IntVec3> cells)
        {
            var yields = new List<YieldEntry>();

            var zone = Find.Selector.SelectedZone as Zone_Growing;
            ThingDef crop = ThingDefOf.Plant_Potato;
            if (zone != null)
            {
                crop = zone.GetPlantDefToGrow();
            }

            var product = crop.plant.harvestedThingDef;
            if (product == null)
            {
                return yields;
            }

            var (multiplier, failChance) = PlantHarvestFactor(crop, false, WorkTypeDefOf.Growing);
            float difficulty = 1f;
            if (crop.plant.harvestYieldAffectedByDifficulty)
            {
                difficulty = Find.Storyteller.difficulty.cropYieldFactor;
            }
            float amount = cells.Count * crop.plant.harvestYield * multiplier * difficulty;
            yields.Add(new YieldEntry(product, amount, failChance));
            return yields;
        }
    }

    public class MineCalculator : YieldCalculator
    {
        public override bool Handles(Designator designator)
        {
            return designator is Designator_Mine;
        }

        protected override bool IsAlreadyApplied(IntVec3 cell, Designator designator)
        {
            return IsAlreadyDesignated(cell, designator);
        }

        // $ Calculate(MineTool, [steel, steel, silver, granite])   (worst miner Hana 80%, stone chunks off)
        // [Steel 32, Steel 32, Silver 32]                           (40 x 80% each; granite chunks skipped)
        protected override List<YieldEntry> Calculate(Designator designator, List<IntVec3> cells)
        {
            var yields = new List<YieldEntry>();

            var (_, miningFactor) = FindWorstPawn(StatDefOf.MiningYield, WorkTypeDefOf.Mining);
            bool includeChunks = YieldPreviewMod.CurrentSettings.stoneChunks;

            var seenRocks = new HashSet<Thing>();
            foreach (var cell in cells)
            {
                var rock = cell.GetFirstMineable(Map);
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
                bool isChunk = building.mineableThing.IsWithinCategory(ThingCategoryDefOf.StoneChunks);
                if (isChunk && !includeChunks)
                {
                    continue;
                }

                float statFactor = 1f;
                if (building.mineableYieldWasteable)
                {
                    statFactor = miningFactor;
                }
                float amount = building.mineableDropChance * Mathf.Max(1f, building.EffectiveMineableYield * statFactor);
                yields.Add(new YieldEntry(building.mineableThing, amount, 0f));
            }
            return yields;
        }
    }

    public class PlantCalculator : YieldCalculator
    {
        public override bool Handles(Designator designator)
        {
            return designator is Designator_Plants;
        }

        protected override bool IsAlreadyApplied(IntVec3 cell, Designator designator)
        {
            return IsAlreadyDesignated(cell, designator);
        }

        // $ Calculate(HarvestTool, [3 grown berry bushes, one slightly damaged])   (worst Ru 70%)
        // [Berries 10, botched 30%], [Berries 10, botched 30%], [Berries 9.4, botched 30%]
        // $ Calculate(ChopTool, [2 grown oaks])                                    (trees never botch)
        // [Wood 25], [Wood 25]
        protected override List<YieldEntry> Calculate(Designator designator, List<IntVec3> cells)
        {
            var yields = new List<YieldEntry>();
            var factorCache = new Dictionary<(ThingDef, bool, bool), (float multiplier, float failChance)>();

            foreach (var thing in SelectThings(cells, designator))
            {
                var plant = thing as Plant;
                bool notPlant = plant == null;
                bool noProduct = plant != null && plant.def.plant.harvestedThingDef == null;
                bool skipThing = notPlant || noProduct;
                if (skipThing)
                {
                    continue;
                }

                bool growersHarvestToo = GrowersHarvestToo(plant);
                var cacheKey = (plant.def, plant.Blighted, growersHarvestToo);
                if (!factorCache.TryGetValue(cacheKey, out var factor))
                {
                    if (growersHarvestToo)
                    {
                        factor = PlantHarvestFactor(plant.def, plant.Blighted, WorkTypeDefOf.PlantCutting, WorkTypeDefOf.Growing);
                    }
                    else
                    {
                        factor = PlantHarvestFactor(plant.def, plant.Blighted, WorkTypeDefOf.PlantCutting);
                    }
                    factorCache[cacheKey] = factor;
                }

                float amount = BaseYield(plant) * factor.multiplier;
                yields.Add(new YieldEntry(plant.def.plant.harvestedThingDef, amount, factor.failChance));
            }
            return yields;
        }

        // $ GrowersHarvestToo(potato in a growing zone)
        // true
        // $ GrowersHarvestToo(wild berry bush)
        // false
        bool GrowersHarvestToo(Plant plant)
        {
            bool inGrowingZone = Map.zoneManager.ZoneAt(plant.Position) is Zone_Growing;
            bool inPlantPot = plant.Position.GetEdifice(Map) is IPlantToGrowSettable;
            return inGrowingZone || inPlantPot;
        }

        // $ BaseYield(tree, harvestYield 25, fully grown, full HP)
        // 25
        // $ BaseYield(same tree at half HP)
        // 18.75                (health factor 0.75)
        // $ BaseYield(same tree at the minimum harvestable growth)
        // 12.5                 (growth factor 0.5)
        // $ BaseYield(seedling, not harvestable yet)
        // 0
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

    public class DeconstructCalculator : YieldCalculator
    {
        public override bool Handles(Designator designator)
        {
            return designator is Designator_Deconstruct;
        }

        protected override bool IsAlreadyApplied(IntVec3 cell, Designator designator)
        {
            return IsAlreadyDesignated(cell, designator);
        }

        // $ Calculate(DeconstructTool, [wood wall])          (costs 5 wood, refund rate 0.5)
        // [Wood 2.5]
        // $ Calculate(DeconstructTool, [ancient engine block])   (no cost list)
        // []
        protected override List<YieldEntry> Calculate(Designator designator, List<IntVec3> cells)
        {
            var yields = new List<YieldEntry>();
            foreach (var thing in SelectThings(cells, designator))
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
                    yields.Add(new YieldEntry(cost.thingDef, refund, 0f));
                }
            }
            return yields;
        }
    }

    public class RemoveFloorCalculator : YieldCalculator
    {
        public override bool Handles(Designator designator)
        {
            return designator is Designator_RemoveFloor;
        }

        protected override bool IsAlreadyApplied(IntVec3 cell, Designator designator)
        {
            return IsAlreadyDesignated(cell, designator);
        }

        // $ Calculate(RemoveFloorTool, [2 wood floor cells])   (costs 3 wood each, refund rate 0.5)
        // [Wood 1.5], [Wood 1.5]
        // $ Calculate(RemoveFloorTool, [smoothed marble])      (natural floor, no cost list)
        // []
        protected override List<YieldEntry> Calculate(Designator designator, List<IntVec3> cells)
        {
            var yields = new List<YieldEntry>();
            bool isFoundation = designator is Designator_RemoveFoundation;
            foreach (var cell in cells)
            {
                TerrainDef terrain = Map.terrainGrid.TopTerrainAt(cell);
                if (isFoundation)
                {
                    terrain = Map.terrainGrid.FoundationAt(cell);
                }
                if (terrain == null)
                {
                    continue;
                }
                foreach (var cost in terrain.CostListAdjusted(null))
                {
                    yields.Add(new YieldEntry(cost.thingDef, cost.count * terrain.resourcesFractionWhenDeconstructed, 0f));
                }
            }
            return yields;
        }
    }
}
