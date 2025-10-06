using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using UnityEngine;
using static DragCubeList;
using static PhysicsGlobals;
// I'd love to use these shortcuts, unfortunately they are very broken in the version that HarmonyKSP currently ships
// using static HarmonyLib.Code;

namespace BodyDragFix
{
    // if we start up at KSPAddon.Startup.Instantly, we cannot detect KSPCommunityFixes as loaded yet.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class BodyDragFixPatcher : MonoBehaviour
    {
        void Awake()
        {
            Harmony harmony = new Harmony(nameof(BodyDragFixPatcher));
            // Uncomment this for easier debugging of the patching.
            // Harmony.DEBUG = true;

            var original = typeof(PhysicsGlobals).GetMethod("DragCurveValue");
            var replacement = typeof(DragCurvePatches).GetMethod("DragCurveValue");

            harmony.Patch(original, prefix: new HarmonyMethod(replacement));

            // KSP Community Fixes rewrites _a lot_ of the original FlightIntegrator.AeroUpdate code
            // annoyingly it inlines the method we want to target completely...
            // detect that here, and rewrite that _again_
            if (AssemblyLoader.loadedAssemblies.Contains("KSPCommunityFixes"))
            {
                var SetDragCubeDrag = AccessTools.Method("KSPCommunityFixes.Performance.FlightIntegratorPerf:SetDragCubeDrag");
                if (SetDragCubeDrag != null)
                {
                    var SetDragCubeDragTranspiler = typeof(CommunityFixesPatches).GetMethod("SetDragCubeDragTranspiler");
                    harmony.Patch(SetDragCubeDrag, transpiler: new HarmonyMethod(SetDragCubeDragTranspiler));
                } else
                {
                    throw new MemberAccessException("KSPCommunityFixes was loaded but FlightIntegratorPerf:SetDragCubeDrag is not present, did KSPCommunityFixes change something?");
                }
            }
        }
    }

    public static class DragCurvePatches
    {
        public static bool DragCurveValue(ref float __result, SurfaceCurvesList cuves, float dotNormalized, float mach)
        {
            float dot = (dotNormalized - 0.5f) * 2f;
            float a = cuves.dragCurveSurface.Evaluate(mach);

            float b;
            if (dot <= 0.0f)
            {
                b = cuves.dragCurveTail.Evaluate(mach);
            }
            else
            {
                b = cuves.dragCurveTip.Evaluate(mach);
            }
            float num = cuves.dragCurveMultiplier.Evaluate(mach);

            // we want to completely override the original result
            __result = Mathf.Lerp(a, b, dot * dot) * num;
            return false;
        }
    }

    public static class CommunityFixesPatches
    {
        public static IEnumerable<CodeInstruction> SetDragCubeDragTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codeMatcher = new CodeMatcher(instructions, generator);

            // find our start point: the call to UnityEngine.Vector3::Dot
            codeMatcher.Start();
            codeMatcher.MatchEndForward(
                new CodeMatch(OpCodes.Call, typeof(Vector3).GetMethod("Dot")),
                new CodeMatch(OpCodes.Conv_R8),
                new CodeMatch(OpCodes.Stloc_S)
            ).ThrowIfInvalid("Could not find dot product calculation");
            var dot_location = codeMatcher.Operand; // why is this not giving the right result ree

            codeMatcher.Advance(1)
                .RemoveUntilForwardExt(new CodeMatch(OpCodes.Bgt_Un_S))
                .ThrowIfInvalid("Could not find branch on dot")
                .RemoveUntilForwardExt(new CodeMatch(OpCodes.Br_S))
                .ThrowIfInvalid("Could not find reunification branch");

            // find our end point: where drag is added to the drag accumulator.
            codeMatcher.RemoveUntilForwardExt(
                new CodeMatch(OpCodes.Ldloc_S),
                new CodeMatch(OpCodes.Ldloc_S), // this loads the drag coefficient calculation that we're swapping out.
                new CodeMatch(OpCodes.Mul),
                new CodeMatch(OpCodes.Stloc_S),
                new CodeMatch(OpCodes.Ldloc_1),
                new CodeMatch(OpCodes.Ldloc_S),
                new CodeMatch(OpCodes.Add),
                new CodeMatch(OpCodes.Stloc_1)
            ).ThrowIfInvalid("Could not find addition to total drag");

            // figure out the operand to Ldloc_S, which we'll need to store to.
            codeMatcher.Advance(1);
            object result_location = codeMatcher.Operand;
            codeMatcher.Advance(-1);

            // get references for some fields we'll need to access
            Type FlightIntegratorPerf = AccessTools.TypeByName("KSPCommunityFixes.Performance.FlightIntegratorPerf");
            Type FIFixedUpdateIntegrationData = AccessTools.TypeByName("KSPCommunityFixes.Performance.FlightIntegratorPerf+FIFixedUpdateIntegrationData");
            Type Numerics = AccessTools.TypeByName("KSPCommunityFixes.Library.Numerics");

            if (FlightIntegratorPerf == null)
                throw new TypeLoadException("could not load KSPCommunityFixes.Performance.FlightIntegratorPerf");
            if (FIFixedUpdateIntegrationData == null)
                throw new TypeLoadException("could not load KSPCommunityFixes.Performance.FlightIntegratorPerf+FIFixedUpdateIntegrationData");
            if (Numerics == null)
                throw new TypeLoadException("could not load KSPCommunityFixes.Library.Numerics");

            var fiData = AccessTools.Field(FlightIntegratorPerf, "fiData") ?? throw new MemberAccessException("FlightIntegratorPerf.fiData");
            var dragTip = AccessTools.Field(FIFixedUpdateIntegrationData, "dragTip") ?? throw new MemberAccessException("fiData.dragTip");
            var dragSurf = AccessTools.Field(FIFixedUpdateIntegrationData, "dragSurf") ?? throw new MemberAccessException("fiData.dragSurf");
            var dragTail = AccessTools.Field(FIFixedUpdateIntegrationData, "dragTail") ?? throw new MemberAccessException("fiData.dragTail");
            var dragMult = AccessTools.Field(FIFixedUpdateIntegrationData, "dragMult") ?? throw new MemberAccessException("fiData.dragMult");
            var Lerp = AccessTools.Method(Numerics, "Lerp") ?? throw new MemberAccessException("Numerics.Lerp");

            // create labels we need
            Label first_label = generator.DefineLabel();
            Label second_label = generator.DefineLabel();

            // emit the code that we need to replace. Yes this sequence could probably be shortened quite a bit
            // but it is simply what the compiler spat out for the IL.
            codeMatcher.InsertAndAdvance(
                // branch if dot > 0.0
                new CodeInstruction(OpCodes.Ldloc_S, dot_location),
                new CodeInstruction(OpCodes.Ldc_R8, 0.0),
                new CodeInstruction(OpCodes.Bgt_Un_S, first_label),
                // load FlightIntegratorPerf::fiData.dragSurf
                new CodeInstruction(OpCodes.Ldsfld, fiData),
                new CodeInstruction(OpCodes.Ldfld, dragSurf),
                // load FlightIntegratorPerf::fiData.dragTail
                new CodeInstruction(OpCodes.Ldsfld, fiData),
                new CodeInstruction(OpCodes.Ldfld, dragTail),
                // load dot * dot
                new CodeInstruction(OpCodes.Ldloc_S, dot_location),
                new CodeInstruction(OpCodes.Ldloc_S, dot_location),
                new CodeInstruction(OpCodes.Mul),
                // lerp
                new CodeInstruction(OpCodes.Call, Lerp),
                // multiply by dragMult
                new CodeInstruction(OpCodes.Ldsfld, fiData),
                new CodeInstruction(OpCodes.Ldfld, dragMult),
                new CodeInstruction(OpCodes.Mul),
                // store as a local and branch
                new CodeInstruction(OpCodes.Stloc_S, result_location),
                new CodeInstruction(OpCodes.Br_S, second_label)
            ).Insert(
                // same dance, but now with fiData.dragTip. We need to set a label here too.
                // load FlightIntegratorPerf::fiData.dragSurf
                new CodeInstruction(OpCodes.Ldsfld, fiData)
            ).AddLabels(
                new[] { first_label }
            ).Advance(1
            ).InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldfld, dragSurf),
                // load FlightIntegratorPerf::fiData.dragTip
                new CodeInstruction(OpCodes.Ldsfld, fiData),
                new CodeInstruction(OpCodes.Ldfld, dragTip),
                // load dot * dot
                new CodeInstruction(OpCodes.Ldloc_S, dot_location),
                new CodeInstruction(OpCodes.Ldloc_S, dot_location),
                new CodeInstruction(OpCodes.Mul),
                // lerp
                new CodeInstruction(OpCodes.Call, Lerp),
                // multiply by dragMult
                new CodeInstruction(OpCodes.Ldsfld, fiData),
                new CodeInstruction(OpCodes.Ldfld, dragMult),
                new CodeInstruction(OpCodes.Mul),
                // store as a local
                new CodeInstruction(OpCodes.Stloc_S, result_location)
            ).AddLabels(
                // insert second label
                new[] { second_label }
            );

            return codeMatcher.Instructions();
        }

        // HarmonyKSP ships an old version so we have to add some extension methods ourselves.
        private static CodeMatcher RemoveUntilForwardExt(this CodeMatcher code_matcher, params CodeMatch[] matches)
        {
            if (code_matcher.IsInvalid)
                throw new InvalidOperationException("Cannot remove instructions from an invalid position.");

            var originalPos = code_matcher.Pos;
            var finder = code_matcher.Clone().MatchStartForward(matches);
            if (finder.IsInvalid)
            {
                // mark us as invalid
                code_matcher.End();
                code_matcher.Advance(1);
            }

            var end = finder.Pos - 1;
            if (end >= originalPos)
                _ = code_matcher.RemoveInstructionsInRange(originalPos, end);
            return code_matcher;
        }
    }
}
