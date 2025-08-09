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
                if (selPawn != null 
                    && selPawn.IsColonistPlayerControlled 
                    && selPawn.RaceProps.Humanlike)
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
            Widgets.Label(new Rect(0, 0, inRect.width, 30), I18Constant.WidgetsLabel.Translate(pawn.Name));
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
                // logic.Add($"无法穿戴 {ap.LabelCap}，评分为 -10 结果为{resultScore}");
                logic.Add(I18Constant.CannotWear.Translate(ap.LabelCap,resultScore));
                if (!ap.PawnCanWear(pawn, true)) logic.Add(I18Constant.CannotWearReason1.Translate());
                if (ap.def.apparel.blocksVision) logic.Add(I18Constant.CannotWearReason2.Translate());
                if (ap.def.apparel.slaveApparel && !pawn.IsSlave) logic.Add(I18Constant.CannotWearReason3.Translate());
                if (ap.def.apparel.mechanitorApparel && pawn.mechanitor == null) logic.Add(I18Constant.CannotWearReason4.Translate());
                return new Tuple<float, List<string>>(resultScore, logic);
            }

            float num1 = 0.1f + ap.def.apparel.scoreOffset + (ap.GetStatValue(StatDefOf.ArmorRating_Sharp) +
                                                              ap.GetStatValue(StatDefOf.ArmorRating_Blunt));
            
            logic.Add(I18Constant.BasicScoreWithDetails.Translate(0.1,ap.def.apparel.scoreOffset,
                ap.GetStatValue(StatDefOf.ArmorRating_Sharp),
                ap.GetStatValue(StatDefOf.ArmorRating_Blunt), num1));
            // 耐久度影响
            if (ap.def.useHitPoints)
            {
                float x = (float)ap.HitPoints / (float)ap.MaxHitPoints;
                float factor = HitPointsPercentScoreFactorCurve.Evaluate(x);
                float oldNum1 = num1;
                num1 *= factor;
                logic.Add(I18Constant.HitPointsEffect.Translate(ap.HitPoints, ap.MaxHitPoints, $"{x:P0}", $"{factor:F2}",
                    $"{oldNum1:F2}", $"{factor:F2}",$"{num1:F2}"));
                   
            }

            // 特殊加成
            float specialOffset = ap.GetSpecialApparelScoreOffset();
            float num2 = num1 + specialOffset;
            if (specialOffset != 0)
            {
                logic.Add(I18Constant.SpecialOffset.Translate($"{specialOffset:F2}", $"{num1:F2}", $"{specialOffset:F2}", $"{num2:F2}"));
            }

            // 保暖需求
            float num3 = 1f;
            if (neededWarmth == NeededWarmth.Warm)
            {
                float statValue = ap.GetStatValue(StatDefOf.Insulation_Cold);
                num3 *= InsulationColdScoreFactorCurve_NeedWarm.Evaluate(statValue);
                logic.Add(I18Constant.NeededWarmth.Translate(statValue, $"{num3:F2}",$"{num2:F2}", $"{num3:F2}", $"{num2 * num3:F2}"));
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
                    logic.Add(I18Constant.WornByCorpse1.Translate( $"{oldNum4:F2}", 0.5,$"{tempNum4:F2}", 0.1,$"{num4:F2}"));
                }
                else
                {
                    logic.Add(I18Constant.WornByCorpse2.Translate( $"{oldNum4:F2}", 0.5, $"{num4:F2}"));
                }
            }

            // 人皮材料
            if (ap.Stuff == ThingDefOf.Human.race.leatherDef)
            {
                if (pawn.Ideo != null && pawn.Ideo.LikesHumanLeatherApparel)
                {
                    float oldNum4 = num4;
                    num4 += 0.12f;
                    logic.Add(I18Constant.IdeoLikeHumanLeatherApparel.Translate($"{oldNum4:F2}", 0.12, $"{num4:F2}"));
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
                           logic.Add(I18Constant.ThoughtHumanLeatherApparelSad1.Translate($"{oldNum4:F2}", 0.5, $"{tempNum4:F2}", 0.1, $"{num4:F2}"));
                            
                        }
                        else
                        {
                            logic.Add(I18Constant.ThoughtHumanLeatherApparelSad2.Translate($"{oldNum4:F2}", 0.5, $"{num4:F2}"));
                        }
                    }

                    if (pawn != null && ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.HumanLeatherApparelHappy, true))
                    {
                        float oldNum4 = num4;
                        num4 += 0.12f;
                        logic.Add(I18Constant.ThoughtHumanLeatherApparelHappy.Translate($"{oldNum4:F2}", 0.12, $"{num4:F2}"));
                        
                        
                    }
                }
            }

            // 性别限制
            if (pawn != null && !ap.def.apparel.CorrectGenderForWearing(pawn.gender))
            {
                float oldNum4 = num4;
                num4 *= 0.01f;
                logic.Add(I18Constant.GenderMismatch.Translate(pawn.gender, $"{oldNum4:F2}", 0.01, $"{num4:F2}"));
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
                    logic.Add(I18Constant.RequiredForPawn.Translate($"{oldNum4:F2}", 25 , $"{num4:F2}"));
                }
                else if (flag3)
                {
                    float oldNum4 = num4;
                    num4 *= 10f;
                    logic.Add(I18Constant.AllowedForPawn.Translate($"{oldNum4:F2}", 10 , $"{num4:F2}"));
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
                    logic.Add(I18Constant.QualityBelowRoyalRequirement.Translate(qc.ToString().Translate(), qualityCategory.ToString().Translate()
                        , $"{oldNum4:F2}", 0.25, $"{num4:F2}"));
                }
                else if (ap.TryGetQuality(out qc))
                {
                    logic.Add(I18Constant.QualityMeetsRoyalRequirement.Translate(qc.ToString().Translate(), qualityCategory.ToString().Translate()));
                }
            }

            resultScore = num4;
            logic.Add(I18Constant.ApparelScoreRaw.Translate($"{resultScore:F2}"));
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
                logic.Add(I18Constant.ConflictWithRangedWeapon.Translate(pawn.equipment.Primary.LabelCap,-1000));
                    
                return new Tuple<float, List<string>>(resultScore, logic);
            }

            // 非暴力工作者检查
            if (ap.def.apparel.ignoredByNonViolent && pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                resultScore = -1000f;
                logic.Add(I18Constant.IgnoredByNonViolent.Translate(-1000));
                return new Tuple<float, List<string>>(resultScore, logic);
            }

            // 获取基础评分
            var apScore = ApparelScoreRaw(pawn, ap,neededWarmth);
            float num = apScore.Item1;
            logic.Add(I18Constant.BasicScore.Translate($"{num:F2}"));
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
                        logic.Add(I18Constant.ConflictWithExistingApparelAndCannotDrop.Translate(wornApparel[index].LabelCap,-1000));
                        return new Tuple<float, List<string>>(resultScore, logic);
                    }

                    if (pawn.apparel.IsLocked(wornApparel[index]))
                    {
                        resultScore = -1000f;
                        logic.Add(I18Constant.ConflictWithLockedApparel.Translate(wornApparel[index].LabelCap,-1000));
                        return new Tuple<float, List<string>>(resultScore, logic);
                    }

                    float oldNum = num;
                    num -= wornScoresCache[index].Item1;
                    logic.Add(I18Constant.ConflictWithExistingApparelAndNeedReplace.Translate(wornApparel[index].LabelCap,$"{wornScoresCache[index].Item1:F2}",
                        $"{oldNum:F2}",$"{wornScoresCache[index].Item1:F2}",$"{num:F2}"));
                    logic.AddRange(wornScoresCache[index].Item2.Select(logicStr => "\t" + logicStr));
                    flag = true;
                }
            }

            // 如果不需要替换任何装备，给予额外加成
            if (!flag)
            {
                float oldNum = num;
                num *= 10f;
                logic.Add(I18Constant.NoReplacementBonus.Translate($"{oldNum:F2}", 10, $"{num:F2}"));
            }

            resultScore = num;
            logic.Add(I18Constant.FinalScore.Translate($"{resultScore:F2}"));
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
            NeededWarmth neededWarmth =
                PawnApparelGenerator.CalculateNeededWarmth(pawn, pawn.Map.TileInfo.tile, GenLocalDate.Twelfth(pawn));
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
                    I18Constant.ApparelIsAllowedByPolicy.Translate(GetColorMark(currentApparelPolicy.filter.Allows(apparel))));
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                apparel2EvaluatingDetail[apparel].Add(
                    I18Constant.IsInAnyStorage.Translate(GetColorMark(apparel.IsInAnyStorage())));
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                apparel2EvaluatingDetail[apparel].Add(
                    I18Constant.IsNotForbidden.Translate(GetColorMark(!apparel.IsForbidden(pawn))));
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                apparel2EvaluatingDetail[apparel].Add(
                    I18Constant.IsNotBurning.Translate(GetColorMark(!apparel.IsBurning())));
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                apparel2EvaluatingDetail[apparel].Add(
                    I18Constant.NoGenderLimitOrGenderMatches.Translate(
                        GetColorMark((apparel.def.apparel.gender == Gender.None ||
                                      apparel.def.apparel.gender == pawn.gender))));
                
                Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                if (currentApparelPolicy.filter.Allows(apparel) && apparel.IsInAnyStorage() &&
                    !apparel.IsForbidden(pawn) && !apparel.IsBurning() &&
                    (apparel.def.apparel.gender == Gender.None || apparel.def.apparel.gender == pawn.gender))
                {
                    var apparelScoreGain = ApparelScoreGain(pawn, apparel,
                        wornApparelScores,neededWarmth);
                    float currentThingScore = apparelScoreGain.Item1;
                    apparel2ScoreDict[apparel] = currentThingScore;
                    apparel2EvaluatingDetail[apparel].Add(I18Constant.ApparelScoreInRange.Translate(0.05,GetColorMark(currentThingScore >= 0.05f)));
                    Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                    //输出具体的逻辑
                    apparel2EvaluatingDetail[apparel].AddRange(apparelScoreGain.Item2.Select(logicStr => "\t" + logicStr));
                    apparel2EvaluatingDetail[apparel].Add(I18Constant.ApparelIsNotBiocodedOrAuthorized.Translate(
                        GetColorMark(!CompBiocodable.IsBiocoded(apparel) || CompBiocodable.IsBiocodedFor(apparel, pawn))));
                    Log.Message(apparel2EvaluatingDetail[apparel].GetLast());
                    apparel2EvaluatingDetail[apparel].Add(I18Constant.HasPartsToWear.Translate(
                        GetColorMark(ApparelUtility.HasPartsToWear(pawn, apparel.def))));
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
                                apparel2EvaluatingDetail[apparel].Add(I18Constant.ApparelSourceEnabled.Translate(GetColorMark(apparelSource.ApparelSourceEnabled)));
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