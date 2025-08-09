using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ApparelScoreViewer
{
    [StaticConstructorOnStartup]
    public static class PawnRightClickMenuPatcher
    {
        static PawnRightClickMenuPatcher()
        {
            Harmony harmony = new Harmony(ModConstant.ModId);
            harmony.PatchAll();
        }

        [HarmonyPatch(typeof(Pawn), "GetFloatMenuOptions")]
        public static class FloatMenuMakerMap_ChoicesAtFor_Patch
        {
            public static IEnumerable<FloatMenuOption> Postfix(IEnumerable<FloatMenuOption> values, Pawn selPawn)
            {
                // 先返回所有原始选项
                foreach (var option in values)
                {
                    yield return option;
                }

                // 添加自定义选项
                if (selPawn?.Faction == Faction.OfPlayer && selPawn.RaceProps.Humanlike)
                {
                    yield return new FloatMenuOption("查看衣服评分",
                        () =>
                        {
                            Find.WindowStack.Add(
                                new ApparelScoreWindow(selPawn, ShowScore.GetApparelScoreList(selPawn)));
                        });
                }
            }
        }
    }

    public class ApparelScoreWindow : Window
    {
        private Pawn pawn;

        private Dictionary<Apparel, Tuple<float, List<string>>> apparel2EvaluatingDetail;

        private Vector2 scrollPosition = Vector2.zero;

        public ApparelScoreWindow(Pawn pawn, Dictionary<Apparel, Tuple<float, List<string>>> apparel2EvaluatingDetail)
        {
            this.pawn = pawn;
            this.apparel2EvaluatingDetail = apparel2EvaluatingDetail;

            // 窗口可关闭，大小可调
            this.doCloseButton = true;
            this.doCloseX = true;
            this.preventCameraMotion = true;
            this.resizeable = true;
            this.absorbInputAroundWindow = true;

            this.forcePause = true; // 打开时暂停游戏
            this.closeOnClickedOutside = true;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            // 屏幕大小
            Vector2 screenSize = new Vector2(UI.screenWidth, UI.screenHeight);

            // 计算居中后的位置
            float x = (screenSize.x - InitialSize.x) / 2f;
            float y = (screenSize.y - InitialSize.y) / 2f;

            this.windowRect = new Rect(x, y, InitialSize.x, InitialSize.y);
        }

        public override Vector2 InitialSize =>
            new Vector2(Math.Min(UI.screenWidth * 0.6f, 1000f), UI.screenHeight * 0.4f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), $"Apparel Scores for {pawn.Name}");
            Text.Font = GameFont.Small;
            int line = 1;
            float lineHeight = 20f;
            StringBuilder content = new StringBuilder();
            foreach (var pair in apparel2EvaluatingDetail)
            {
                Tuple<float, List<string>> details = pair.Value;
                content.Append($"{pair.Key.LabelCap} : {details.Item1}\n");
                line++;
                for (int index = 0; index < details.Item2.Count; index++)
                {
                    string prefix = (index == details.Item2.Count - 1) ? "  └─ " : "  ├─ ";
                    content.Append(prefix + details.Item2[index] + "\n");
                    line++;
                }

                line++;
            }

            Rect viewRect = new Rect(0, 0, inRect.width - 20, line * lineHeight);

            Widgets.BeginScrollView(
                new Rect(0, 40, inRect.width, inRect.height - 40), // 显示区域
                ref scrollPosition, // 滚动位置引用
                viewRect // 内容区域
            );
            Widgets.Label(viewRect, content.ToString());
            Widgets.EndScrollView();
        }
    }

    public class ShowScore
    {
        private static readonly SimpleCurve InsulationColdScoreFactorCurve_NeedWarm = new SimpleCurve()
        {
            {
                new CurvePoint(0.0f, 1f),
                true
            },
            {
                new CurvePoint(30f, 8f),
                true
            }
        };

        private static readonly SimpleCurve HitPointsPercentScoreFactorCurve = new SimpleCurve()
        {
            {
                new CurvePoint(0.0f, 0.0f),
                true
            },
            {
                new CurvePoint(0.2f, 0.2f),
                true
            },
            {
                new CurvePoint(0.22f, 0.3f),
                true
            },
            {
                new CurvePoint(0.5f, 0.3f),
                true
            },
            {
                new CurvePoint(0.52f, 1f),
                true
            }
        };

        public static Tuple<float, List<string>> ApparelScoreRaw(Pawn pawn, Apparel ap, NeededWarmth neededWarmth)
        {
            List<string> logic = new List<string>();
            float resultScore = Single.NaN;
            if (!ap.PawnCanWear(pawn, true) || ap.def.apparel.blocksVision ||
                ap.def.apparel.slaveApparel && !pawn.IsSlave ||
                ap.def.apparel.mechanitorApparel && pawn.mechanitor == null)
            {
                resultScore = -10f;
                logic.Add($"无法穿戴 {ap.LabelCap}，评分为 -10 结果为{resultScore}");
                if (!ap.PawnCanWear(pawn, true)) logic.Add("- 殖民者无法穿戴此装备");
                if (ap.def.apparel.blocksVision) logic.Add("- 装备遮挡视野");
                if (ap.def.apparel.slaveApparel && !pawn.IsSlave) logic.Add("- 奴隶装备但穿戴者不是奴隶");
                if (ap.def.apparel.mechanitorApparel && pawn.mechanitor == null) logic.Add("- 机械师装备但穿戴者不是机械师");
                return new Tuple<float, List<string>>(resultScore, logic);
            }

            float num1 = 0.1f + ap.def.apparel.scoreOffset + (ap.GetStatValue(StatDefOf.ArmorRating_Sharp) +
                                                              ap.GetStatValue(StatDefOf.ArmorRating_Blunt));
            logic.Add(
                $"基础分数：0.1 + 装备分数偏移({ap.def.apparel.scoreOffset}) + 锐器护甲({ap.GetStatValue(StatDefOf.ArmorRating_Sharp)}) + 钝器护甲({ap.GetStatValue(StatDefOf.ArmorRating_Blunt)}) = {num1}");
            // 耐久度影响
            if (ap.def.useHitPoints)
            {
                float x = (float)ap.HitPoints / (float)ap.MaxHitPoints;
                float factor = HitPointsPercentScoreFactorCurve.Evaluate(x);
                float oldNum1 = num1;
                num1 *= factor;
                logic.Add(
                    $"耐久度影响: {ap.HitPoints}/{ap.MaxHitPoints} ({x:P0})，系数 {factor:F2}，分数 {oldNum1:F2} × {factor:F2} = {num1:F2}");
            }

            // 特殊加成
            float specialOffset = ap.GetSpecialApparelScoreOffset();
            float num2 = num1 + specialOffset;
            if (specialOffset != 0)
            {
                logic.Add($"特殊装备加成 {specialOffset:F2}，分数 {num1:F2} + {specialOffset:F2} = {num2:F2}");
            }

            // 保暖需求
            float num3 = 1f;
            if (neededWarmth == NeededWarmth.Warm)
            {
                float statValue = ap.GetStatValue(StatDefOf.Insulation_Cold);
                num3 *= InsulationColdScoreFactorCurve_NeedWarm.Evaluate(statValue);
                logic.Add($"需要保暖，防寒值 {statValue}，保暖系数 {num3:F2}，应用保暖系数，分数 {num2:F2} × {num3:F2} = {num2 * num3:F2}");
            }

            float num4 = num2 * num3;
            // 尸体穿过的衣物
            if (ap.WornByCorpse &&
                (pawn == null || ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.DeadMansApparel, true)))
            {
                float oldNum4 = num4;
                num4 -= 0.5f;
                if ((double)num4 > 0.0)
                {
                    float tempNum4 = num4;
                    num4 *= 0.1f;
                    logic.Add($"为死人衣物，分数 {oldNum4:F2} - 0.5 = {tempNum4:F2}，再 × 0.1 = {num4:F2}");
                }
                else
                {
                    logic.Add($"为死人衣物，分数 {oldNum4:F2} - 0.5 = {num4:F2}");
                }
            }

            // 人皮材料
            if (ap.Stuff == ThingDefOf.Human.race.leatherDef)
            {
                if (pawn.Ideo != null && pawn.Ideo.LikesHumanLeatherApparel)
                {
                    float oldNum4 = num4;
                    num4 += 0.12f;
                    logic.Add($"为人皮制品且意识形态喜欢，分数 {oldNum4:F2} + 0.12 = {num4:F2}");
                }
                else
                {
                    if (pawn == null || ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.HumanLeatherApparelSad, true))
                    {
                        float oldNum4 = num4;
                        num4 -= 0.5f;
                        if (num4 > 0.0)
                        {
                            float tempNum4 = num4;
                            num4 *= 0.1f;
                            logic.Add(
                                $"为人皮制品且厌恶，分数 {oldNum4:F2} - 0.5 = {tempNum4:F2}，再 × 0.1 = {num4:F2}");
                        }
                        else
                        {
                            logic.Add($"为人皮制品且厌恶，分数 {oldNum4:F2} - 0.5 = {num4:F2}");
                        }
                    }

                    if (pawn != null && ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.HumanLeatherApparelHappy, true))
                    {
                        float oldNum4 = num4;
                        num4 += 0.12f;
                        logic.Add($"为人皮制品且喜欢，分数 {oldNum4:F2} + 0.12 = {num4:F2}");
                    }
                }
            }

            // 性别限制
            if (pawn != null && !ap.def.apparel.CorrectGenderForWearing(pawn.gender))
            {
                float oldNum4 = num4;
                num4 *= 0.01f;
                logic.Add($"性别不符（{pawn.gender}），分数 {oldNum4:F2} × 0.01 = {num4:F2}");
            }

            // 服装需求检查
            bool flag1 = false;
            if (pawn != null)
            {
                foreach (ApparelRequirementWithSource allRequirement in pawn.apparel.AllRequirements)
                {
                    foreach (BodyPartGroupDef bodyPartGroupDef in allRequirement.requirement.bodyPartGroupsMatchAny)
                    {
                        if (ap.def.apparel.bodyPartGroups.Contains(bodyPartGroupDef))
                        {
                            flag1 = true;
                            break;
                        }
                    }

                    if (flag1)
                        break;
                }
            }

            if (flag1)
            {
                bool flag2 = false;
                bool flag3 = false;
                foreach (ApparelRequirementWithSource allRequirement in pawn.apparel.AllRequirements)
                {
                    if (allRequirement.requirement.RequiredForPawn(pawn, ap.def))
                        flag2 = true;
                    if (allRequirement.requirement.AllowedForPawn(pawn, ap.def))
                        flag3 = true;
                }

                if (flag2)
                {
                    float oldNum4 = num4;
                    num4 *= 25f;
                    logic.Add($"为必需装备，分数 {oldNum4:F2} × 25 = {num4:F2}");
                }
                else if (flag3)
                {
                    float oldNum4 = num4;
                    num4 *= 10f;
                    logic.Add($"为允许装备，分数 {oldNum4:F2} × 10 = {num4:F2}");
                }
            }

            // 皇家品质需求
            if (pawn != null && pawn.royalty != null && pawn.royalty.AllTitlesInEffectForReading.Count > 0)
            {
                QualityCategory qualityCategory = QualityCategory.Awful;
                foreach (RoyalTitle royalTitle in pawn.royalty.AllTitlesInEffectForReading)
                {
                    if (royalTitle.def.requiredMinimumApparelQuality > qualityCategory)
                        qualityCategory = royalTitle.def.requiredMinimumApparelQuality;
                }

                QualityCategory qc;
                if (ap.TryGetQuality(out qc) && qc < qualityCategory)
                {
                    float oldNum4 = num4;
                    num4 *= 0.25f;
                    logic.Add($"品质({qc})低于皇家要求({qualityCategory})，分数 {oldNum4:F2} × 0.25 = {num4:F2}");
                }
                else if (ap.TryGetQuality(out qc))
                {
                    logic.Add($"品质({qc})满足皇家要求({qualityCategory})");
                }
            }

            resultScore = num4;
            logic.Add($"最终评分：{resultScore:F2}");
            return new Tuple<float, List<string>>(resultScore, logic);
        }

        public static Tuple<float, List<string>> ApparelScoreGain(Pawn pawn, Apparel ap, List<Tuple<float, List<string>>> wornScoresCache, NeededWarmth neededWarmth)
        {
            List<string> logic = new List<string>();
            float resultScore = Single.NaN;

            // 护盾腰带和远程武器冲突检查
            if (ap.def == ThingDefOf.Apparel_ShieldBelt && pawn.equipment.Primary != null &&
                pawn.equipment.Primary.def.IsWeaponUsingProjectiles)
            {
                resultScore = -1000f;
                logic.Add($"是护盾腰带且装备了远程武器 {pawn.equipment.Primary.LabelCap}，评分为 -1000");
                return new Tuple<float, List<string>>(resultScore, logic);
            }

            // 非暴力工作者检查
            if (ap.def.apparel.ignoredByNonViolent && pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                resultScore = -1000f;
                logic.Add($"被非暴力工作者忽略，评分为 -1000");
                return new Tuple<float, List<string>>(resultScore, logic);
            }

            // 获取基础评分
            var apScore = ApparelScoreRaw(pawn, ap,neededWarmth);
            float num = apScore.Item1;
            logic.Add($"基础评分: {num:F2}");
            List<Apparel> wornApparel = pawn.apparel.WornApparel;
            bool flag = false;

            // 检查与已穿戴装备的冲突
            for (int index = 0; index < wornApparel.Count; ++index)
            {
                if (!ApparelUtility.CanWearTogether(wornApparel[index].def, ap.def, pawn.RaceProps.body))
                {
                    // 检查是否可以脱下冲突装备
                    if (!pawn.outfits.forcedHandler.AllowedToAutomaticallyDrop(wornApparel[index]))
                    {
                        resultScore = -1000f;
                        logic.Add($"与已装备的 {wornApparel[index].LabelCap} 冲突，且不允许自动脱下，评分为 -1000");
                        return new Tuple<float, List<string>>(resultScore, logic);
                    }

                    if (pawn.apparel.IsLocked(wornApparel[index]))
                    {
                        resultScore = -1000f;
                        logic.Add($"与已装备的 {wornApparel[index].LabelCap} 冲突，且该装备被锁定，评分为 -1000");
                        return new Tuple<float, List<string>>(resultScore, logic);
                    }

                    float oldNum = num;
                    num -= wornScoresCache[index].Item1;
                    logic.Add(
                        $"与 {wornApparel[index].LabelCap}(评分:{wornScoresCache[index].Item1:F2}) 冲突，需要替换，分数 {oldNum:F2} - {wornScoresCache[index].Item1:F2} = {num:F2}");
                    logic.AddRange(wornScoresCache[index].Item2.Select(logicStr => "\t" + logicStr));
                    flag = true;
                }
            }

            // 如果不需要替换任何装备，给予额外加成
            if (!flag)
            {
                float oldNum = num;
                num *= 10f;
                logic.Add($"无需替换任何装备，获得加成，分数 {oldNum:F2} × 10 = {num:F2}");
            }

            resultScore = num;
            logic.Add($"最终收益评分：{resultScore:F2}");

            return new Tuple<float, List<string>>(resultScore, logic);
        }


        public static string GetColorMark(bool condition)
        {
            return condition ? "<color=green>√</color>" : "<color=red>×</color>";
        }

        public static Dictionary<Apparel, Tuple<float, List<string>>> GetApparelScoreList(Pawn pawn)
        {
            var apparel2ScoreDict = new Dictionary<Apparel, float>();
            var apparel2EvaluatingDetail = new Dictionary<Apparel, List<string>>();
            List<Thing> tmpApparelList = new List<Thing>();
            ApparelPolicy currentApparelPolicy = pawn.outfits.CurrentApparelPolicy;
            //目前穿的衣服的评分
            List<Apparel> wornApparel = pawn.apparel.WornApparel;
            List<Tuple<float, List<string>>> wornApparelScores = new List<Tuple<float, List<string>>>();
            pawn.Map.listerThings.GetAllThings(tmpApparelList, ThingRequestGroup.Apparel, null,
                true);
            foreach (IHaulSource haulSource in pawn.Map.haulDestinationManager.AllHaulSourcesListForReading)
            {
                foreach (Thing thing2 in haulSource.GetDirectlyHeldThings())
                {
                    Apparel apparel = thing2 as Apparel;
                    if (apparel != null)
                    {
                        tmpApparelList.Add(apparel);
                    }
                }
            }

            Log.Message("为 pawn: " + pawn.Name + " 找到衣服 " + tmpApparelList.Count + " 件");
            //反射修改private字段
            //在计算分数之前需要先判断当前Pawn的保暖需求
            NeededWarmth neededWarmth =
                PawnApparelGenerator.CalculateNeededWarmth(pawn, pawn.Map.TileInfo.tile, GenLocalDate.Twelfth(pawn));
            // Type type = typeof(JobGiver_OptimizeApparel);
            // FieldInfo field = type.GetField("neededWarmth", BindingFlags.NonPublic | BindingFlags.Static);
            // field.SetValue(null, neededWarmth);
            for (int j = 0; j < wornApparel.Count; j++)
            {
                var apparelScoreRaw = ApparelScoreRaw(pawn, wornApparel[j], neededWarmth);
                wornApparelScores.Add(apparelScoreRaw);
            }

            for (int k = 0; k < tmpApparelList.Count; k++)
            {
                Apparel apparel = (Apparel)tmpApparelList[k];
                if (apparel2ScoreDict.ContainsKey(apparel))
                {
                    continue;
                }

                apparel2ScoreDict.Add(apparel, Single.NaN);
                apparel2EvaluatingDetail.Add(apparel, new List<string>());

                apparel2EvaluatingDetail[apparel].Add(
                    $"当前衣服被允许: {GetColorMark(currentApparelPolicy.filter.Allows(apparel))}");
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                apparel2EvaluatingDetail[apparel].Add(
                    $"在任意存储中: {GetColorMark(apparel.IsInAnyStorage())}");
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                apparel2EvaluatingDetail[apparel].Add(
                    $"没有被禁止: {GetColorMark(!apparel.IsForbidden(pawn))}");
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                apparel2EvaluatingDetail[apparel].Add(
                    $"该衣服不是生物编码的: {GetColorMark(!apparel.IsBurning())}");
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                apparel2EvaluatingDetail[apparel].Add(
                    $"衣服没有性别要求或者性别符合: {GetColorMark((apparel.def.apparel.gender == Gender.None || apparel.def.apparel.gender == pawn.gender))}");
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                if (currentApparelPolicy.filter.Allows(apparel) && apparel.IsInAnyStorage() &&
                    !apparel.IsForbidden(pawn) && !apparel.IsBurning() &&
                    (apparel.def.apparel.gender == Gender.None || apparel.def.apparel.gender == pawn.gender))
                {
                    var apparelScoreGain = ApparelScoreGain(pawn, apparel,
                        wornApparelScores,neededWarmth);
                    float currentThingScore = apparelScoreGain.Item1;
                    apparel2ScoreDict[apparel] = currentThingScore;
                    apparel2EvaluatingDetail[apparel].Add(
                        $"衣服评分大于等于 0.05: {GetColorMark(currentThingScore >= 0.05f)}");
                    Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                    //输出具体的逻辑
                    apparel2EvaluatingDetail[apparel].AddRange(apparelScoreGain.Item2.Select(logicStr => "\t" + logicStr));
                    apparel2EvaluatingDetail[apparel].Add(
                        $"该衣服不是生物编码的，或者是被该小人授权的生物编码: {GetColorMark(!CompBiocodable.IsBiocoded(apparel) || CompBiocodable.IsBiocodedFor(apparel, pawn))}");
                    Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                    apparel2EvaluatingDetail[apparel].Add(
                        $"小人拥有穿戴这件衣服所需的身体部位: {GetColorMark(ApparelUtility.HasPartsToWear(pawn, apparel.def))}");
                    Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                    if (currentThingScore >= 0.05f &&
                        (!CompBiocodable.IsBiocoded(apparel) || CompBiocodable.IsBiocodedFor(apparel, pawn)) &&
                        ApparelUtility.HasPartsToWear(pawn, apparel.def))
                    {
                        LocalTargetInfo target = apparel;
                        IApparelSource apparelSource = apparel.ParentHolder as IApparelSource;
                        if (apparelSource != null)
                        {
                            if (apparelSource is Thing thing3)
                            {
                                apparel2EvaluatingDetail[apparel].Add(
                                    $"容器可用: {GetColorMark(apparelSource.ApparelSourceEnabled)}");
                                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                                if (!apparelSource.ApparelSourceEnabled)
                                {
                                    //容器不可用,略过当前衣服,跳到下一个
                                    continue;
                                }

                                target = thing3;
                            }
                        }

                        var canReserveAndReach = (pawn.CanReserveAndReach(target, PathEndMode.OnCell,
                            pawn.NormalMaxDanger(), 1, -1, null,
                            false) && apparel.def.apparel.developmentalStageFilter.Has(pawn.DevelopmentalStage));
                        apparel2EvaluatingDetail[apparel].Add(
                            $"地点可达: {GetColorMark(canReserveAndReach)}");
                        Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                        if (canReserveAndReach)
                        {
                            Log.Message("为 pawn: " + pawn.Name + "找到衣服: " + apparel.LabelCap +
                                        " with score: " + currentThingScore);
                        }
                    }
                }
            }

            //返回一个字典，包含Apparel和对应的评分
            Dictionary<Apparel, Tuple<float, List<string>>> result =
                new Dictionary<Apparel, Tuple<float, List<string>>>();
            foreach (var pair in apparel2ScoreDict)
            {
                //添加到结果字典中
                result.Add(pair.Key, new Tuple<float, List<string>>(pair.Value, apparel2EvaluatingDetail[pair.Key]));
            }

            Log.Message(
                $"为 pawn: {pawn.Name} 计算衣服评分完成, 共找到 {result.Count} 件衣服");
            return result;
        }
    }
}