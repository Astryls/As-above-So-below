using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Shared helpers for the V2 dev actions.
    ///
    /// RESCUED FROM V1. ABDevTools was a partial class whose "other half" lived in
    /// Source/Dev/ABDevTools.cs alongside V1's own dev actions. The V2 halves
    /// (ABDevTools.V2.cs, ABDevTools.V2Spike.cs) called ClearCell and Report unqualified,
    /// so deleting V1 took both out from under them. They are reproduced here verbatim -
    /// nothing about either is V1-specific, they were simply on the wrong side of the line.
    /// </summary>
    public static partial class ABDevTools
    {
        /// <summary>Empty a cell of buildings, items and plants so a dev action can place
        /// something there without fighting whatever the map generator left behind.</summary>
        private static void ClearCell(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return;
            }
            // Copied before iterating: Destroy mutates the cell's thing list.
            List<Thing> things = new List<Thing>(c.GetThingList(map));
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || t.Destroyed)
                {
                    continue;
                }
                ThingCategory cat = t.def.category;
                if (cat == ThingCategory.Building || cat == ThingCategory.Item || cat == ThingCategory.Plant)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
        }

        /// <summary>Write a self-test result to docs/SelfTest.log and surface it in the log
        /// and as a message. Appends so several tests in one session land together.</summary>
        private static void Report(string name, StringBuilder body, int pass, int fail)
        {
            int total = pass + fail;
            string header = "[As above, So below] SELF-TEST: " + name + "\n"
                + "when: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
                + "result: " + pass + "/" + total + " checks passed"
                + (fail > 0 ? " -- " + fail + " FAILED" : " -- ALL PASS") + "\n\n";
            string full = header + body;

            try
            {
                string root = ABMod.ModContent?.RootDir;
                if (!string.IsNullOrEmpty(root))
                {
                    string dir = Path.Combine(root, "docs");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "SelfTest.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 262144)
                    {
                        File.Delete(path);
                    }
                    File.AppendAllText(path, full + "\n----\n");
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " could not write docs/SelfTest.log: " + e.Message);
            }

            if (fail > 0)
            {
                Log.Error(ABLog.Tag + " SELFTEST '" + name + "': " + fail + " of " + total
                    + " checks FAILED (see docs/SelfTest.log):\n" + body);
            }
            else
            {
                Log.Warning(ABLog.Tag + " SELFTEST '" + name + "': all " + total + " checks passed.");
            }
            Messages.Message("AB self-test: " + pass + " pass / " + fail + " fail. See docs/SelfTest.log.",
                fail > 0 ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
