using RimWorld;
using RimWorld.QuestGen;
using Verse;

namespace SpaceServices
{
    [DefOf]
    public static class SpaceServicesDefOf
    {
        public static QuestScriptDef JDB_SpaceServices_DelayLodgerQuest;

        static SpaceServicesDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(SpaceServicesDefOf));
        }
    }

    public class QuestNode_Root_ServiceDelayLodger : QuestNode
    {
        protected override void RunInt()
        {
        }

        protected override bool TestRunInt(Slate slate)
        {
            return true;
        }
    }
}
