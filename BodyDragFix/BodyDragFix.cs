using HarmonyLib;
using UnityEngine;
using static PhysicsGlobals;

namespace KspAeroTweak
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    class KspAeroTweakPatcher : MonoBehaviour
    {
        void Awake()
        {
            Harmony harmony = new Harmony(nameof(KspAeroTweakPatcher));

            var original = typeof(PhysicsGlobals).GetMethod("DragCurveValue");
            var replacement = typeof(DragCurvePatchClass).GetMethod("DragCurveValue");

            harmony.Patch(original, prefix: new HarmonyMethod(replacement));

            // KSP Community Fixes rewrites _a lot_ of the original FlightIntegrator.AeroUpdate code
            // annoyingly it inlines the method we want to target completely...
            // detect that here, and rewrite that _again_
            // if (Harmony.HasAnyPatches())
            // TODO
        }
    }

    class DragCurvePatchClass
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
}
