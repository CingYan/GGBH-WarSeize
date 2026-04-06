using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MelonLoader;
using HarmonyLib;
using UnhollowerBaseLib;

namespace MOD_nV039M
{
    public class ModMain
    {
        private static HarmonyLib.Harmony harmony;
        private static ModMain _instance;

        private static bool postWarHandled = false;
        private static long lastBatchSec = 0;
        private static int batchCount = 0;
        private static bool attackHandled = false;
        private static bool openDramaPatched = false;

        private const int ANGER_LUCK_ID = 1425424295;
        private const int DRAMA_INTRO = 1106494995;
        private const int DRAMA_DECLARE_1 = 813057337;
        private const int DRAMA_LOOP = 917312688;
        private const int DRAMA_PRISONER = 820728301;
        private const int DRAMA_FINISH = 1572050475;
        private const int PRISONER_TABLE_ID = -1642035727;
        private const string VERSION = "v25";

        private static HashSet<string> savedPrisonerIds = new HashSet<string>();
        private static List<string> prisonerQueue = new List<string>();
        private static int prisonerIndex = 0;
        private static string currentPrisonerId = null;
        private static bool sequenceRunning = false;
        private static string targetSchoolID = null;
        private static bool testRan = false;
        private static bool keyCheckRunning = false;
        private static bool worldEnterHooked = false;
        private static bool arrestPatched = false;
        private static string modDir = null;

        private static void Log(string msg)
        {
            MelonLogger.Msg("[MOD " + VERSION + "] " + msg);
        }

        public void Init()
        {
            _instance = this;
            postWarHandled = false;
            lastBatchSec = 0;
            batchCount = 0;
            attackHandled = false;
            prisonerQueue.Clear();
            prisonerIndex = 0;
            currentPrisonerId = null;
            sequenceRunning = false;
            testRan = false;
            Log("=== Init start ===");

            try
            {
                harmony = new HarmonyLib.Harmony("MOD_nV039M");
                Type swType = typeof(SchoolWar);

                MethodInfo clearMethod = swType.GetMethod("SchoolWarClear",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (clearMethod != null)
                {
                    harmony.Patch(clearMethod,
                        prefix: new HarmonyMethod(typeof(ModMain).GetMethod("Patch_SchoolWarClear",
                            BindingFlags.Public | BindingFlags.Static)));
                    Log("OK SchoolWarClear patch");
                }

                MethodInfo attackMethod = swType.GetMethod("AttackSchool",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (attackMethod != null)
                {
                    harmony.Patch(attackMethod,
                        prefix: new HarmonyMethod(typeof(ModMain).GetMethod("Patch_AttackSchool",
                            BindingFlags.Public | BindingFlags.Static)));
                    Log("OK AttackSchool patch");
                }

                // Hook ArrestNpcCondition to force unitRight
                try
                {
                    Type dfType = typeof(DramaFunction);
                    MethodInfo arrestMethod = dfType.GetMethod("ArrestNpcCondition",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (arrestMethod != null)
                    {
                        harmony.Patch(arrestMethod,
                            prefix: new HarmonyMethod(typeof(ModMain).GetMethod("Patch_ArrestNpcCondition",
                                BindingFlags.Public | BindingFlags.Static)));
                        arrestPatched = true;
                        Log("OK ArrestNpcCondition patch");
                    }
                    else
                    {
                        Log("WARN ArrestNpcCondition not found");
                    }
                }
                catch (Exception ex)
                {
                    Log("FAIL ArrestNpcCondition patch: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Log("FAIL Harmony: " + ex.Message);
            }

            // mod root dir for save files
            modDir = @"D:\SteamLibrary\steamapps\common\鬼谷八荒\ModExportData\Mod_nV039M\ModCode\dll";
            try
            {
                string asmLoc = typeof(ModMain).Assembly.Location;
                if (!string.IsNullOrEmpty(asmLoc))
                {
                    string asmDir = Path.GetDirectoryName(asmLoc);
                    if (!string.IsNullOrEmpty(asmDir))
                        modDir = asmDir;
                }
            }
            catch { }
            Log("modDir=" + modDir);

            // hook IntoWorld event for save-load recovery
            if (!worldEnterHooked)
            {
                try
                {
                    g.events.On(EGameType.IntoWorld, new Action(() => { OnWorldEnter(); }));
                    worldEnterHooked = true;
                    Log("OK IntoWorld hook");
                }
                catch (Exception ex)
                {
                    Log("FAIL IntoWorld hook: " + ex.Message);
                }
            }

            Log("=== Init done (" + VERSION + ") ===");
            StartKeyCheck();
        }

        public void Destroy()
        {
            _instance = null;
            postWarHandled = false;
            batchCount = 0;
            attackHandled = false;
            prisonerQueue.Clear();
            prisonerIndex = 0;
            currentPrisonerId = null;
            sequenceRunning = false;
            testRan = false;
            Log("Destroy (" + VERSION + ")");
        }

        private static void StartKeyCheck()
        {
            if (keyCheckRunning) return;
            keyCheckRunning = true;
            g.timer.Frame(new Action(() =>
            {
                try
                {
                    if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F9) && !testRan)
                    {
                        testRan = true;
                        Log("[F9] pressed");
                        ProbeLuckAPIs();
                    }
                    StartKeyCheck();
                }
                catch { StartKeyCheck(); }
            }), 1, false);
            keyCheckRunning = false;
        }

        private static Il2CppStringArray MakeIl2CppArray(params string[] values)
        {
            var arr = new Il2CppStringArray(values.Length);
            for (int i = 0; i < values.Length; i++)
                arr[i] = values[i];
            return arr;
        }

        private static bool HasLuckId(dynamic pd, int targetId)
        {
            try
            {
                var addLuck = pd.addLuck;
                if (addLuck != null)
                {
                    for (int i = 0; i < addLuck.Count; i++)
                    {
                        try
                        {
                            if (addLuck[i] != null && addLuck[i].id == targetId)
                                return true;
                        }
                        catch { break; }
                    }
                }
            }
            catch { }
            try
            {
                var bornLuck = pd.bornLuck;
                if (bornLuck != null)
                {
                    for (int i = 0; i < bornLuck.Length; i++)
                    {
                        try
                        {
                            if (bornLuck[i] != null && bornLuck[i].id == targetId)
                                return true;
                        }
                        catch { break; }
                    }
                }
            }
            catch { }
            return false;
        }

        // =============================================================
        // F9: Probe luck-related APIs
        // =============================================================
        private static void ProbeLuckAPIs()
        {
            Log("[PROBE] === luck API probe ===");

            // 找一個目標 NPC
            string testUid = null;
            WorldUnitBase testUnit = null;

            try
            {
                var player = g.world.playerUnit;
                if (player != null)
                    Log("[PROBE] player id=" + player.data.unitData.unitID);
            }
            catch { }

            // 找第一個女性 NPC（從 savedPrisonerIds 或隨機）
            if (savedPrisonerIds.Count > 0)
            {
                foreach (string uid in savedPrisonerIds)
                {
                    try
                    {
                        testUnit = g.world.unit.allUnit[uid];
                        if (testUnit != null)
                        {
                            testUid = uid;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (testUnit == null)
            {
                Log("[PROBE] no saved prisoner, finding any female NPC");
                try
                {
                    var enumerator = g.world.unit.allUnit.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        try
                        {
                            var pair = enumerator.Current;
                            if (pair.Value == null) continue;
                            if ((int)pair.Value.data.unitData.propertyData.sex == 2)
                            {
                                testUnit = pair.Value;
                                testUid = pair.Key;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            if (testUnit == null)
            {
                Log("[PROBE] no test NPC found, abort");
                return;
            }

            string testName = testUnit.data.unitData.propertyData.GetName();
            Log("[PROBE] test subject: " + testName + " id=" + testUid);

            // Part 1: Probe WorldUnitBase methods with "luck" or "Luck"
            Log("[PROBE] --- WorldUnitBase methods ---");
            Type wubType = typeof(WorldUnitBase);
            foreach (MethodInfo m in wubType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string mn = m.Name.ToLower();
                if (mn.Contains("luck") || mn.Contains("buff") || mn.Contains("addluck"))
                {
                    string parms = "";
                    foreach (var p in m.GetParameters())
                    {
                        if (parms.Length > 0) parms += ", ";
                        parms += p.ParameterType.Name + " " + p.Name;
                    }
                    Log("[PROBE] " + m.Name + "(" + parms + ") ret=" + m.ReturnType.Name);
                }
            }

            // Part 2: Probe PropertyData methods
            Log("[PROBE] --- PropertyData methods ---");
            Type pdType = testUnit.data.unitData.propertyData.GetType();
            foreach (MethodInfo m in pdType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string mn = m.Name.ToLower();
                if (mn.Contains("luck") || mn.Contains("addluck") || mn.Contains("removeluck"))
                {
                    string parms = "";
                    foreach (var p in m.GetParameters())
                    {
                        if (parms.Length > 0) parms += ", ";
                        parms += p.ParameterType.Name + " " + p.Name;
                    }
                    Log("[PROBE] pd." + m.Name + "(" + parms + ") ret=" + m.ReturnType.Name);
                }
            }

            // Part 3: Probe UnitData methods
            Log("[PROBE] --- UnitData methods ---");
            Type udType = testUnit.data.unitData.GetType();
            foreach (MethodInfo m in udType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string mn = m.Name.ToLower();
                if (mn.Contains("luck"))
                {
                    string parms = "";
                    foreach (var p in m.GetParameters())
                    {
                        if (parms.Length > 0) parms += ", ";
                        parms += p.ParameterType.Name + " " + p.Name;
                    }
                    Log("[PROBE] ud." + m.Name + "(" + parms + ") ret=" + m.ReturnType.Name);
                }
            }

            // Part 4: Check current game time
            Log("[PROBE] --- game time ---");
            try
            {
                var run = g.world.run;
                Type runType = run.GetType();
                foreach (PropertyInfo p in runType.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    string pn = p.Name.ToLower();
                    if (pn.Contains("month") || pn.Contains("year") || pn.Contains("day")
                        || pn.Contains("round") || pn.Contains("time"))
                    {
                        try
                        {
                            object val = p.GetValue(run);
                            Log("[PROBE] run." + p.Name + " = " + val);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Part 5: Check before add luck
            bool hadBefore = HasLuckId(testUnit.data.unitData.propertyData, ANGER_LUCK_ID);
            Log("[PROBE] before: hasAnger=" + hadBefore);

            // Part 6: Try WorldUnitBase.AddLuck if exists
            Log("[PROBE] --- try AddLuck methods ---");
            bool tried = false;
            foreach (MethodInfo m in wubType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name.Contains("AddLuck") || m.Name.Contains("addLuck"))
                {
                    var parms = m.GetParameters();
                    if (parms.Length == 1 && parms[0].ParameterType == typeof(int))
                    {
                        try
                        {
                            m.Invoke(testUnit, new object[] { ANGER_LUCK_ID });
                            Log("[PROBE] called " + m.Name + "(" + ANGER_LUCK_ID + ") OK");
                            tried = true;
                        }
                        catch (Exception ex)
                        {
                            Log("[PROBE] " + m.Name + " error: " + ex.Message);
                        }
                        break;
                    }
                }
            }

            if (!tried)
            {
                Log("[PROBE] no direct AddLuck(int) found, trying manual add");
                try
                {
                    var pd = testUnit.data.unitData.propertyData;
                    var luckData = new DataUnit.LuckData();
                    luckData.id = ANGER_LUCK_ID;
                    luckData.duration = -1;
                    luckData.createTime = 1;
                    pd.addLuck.Add(luckData);
                    Log("[PROBE] manual add OK");
                }
                catch (Exception ex)
                {
                    Log("[PROBE] manual add error: " + ex.Message);
                }
            }

            // Part 7: Check after add
            bool hadAfter = HasLuckId(testUnit.data.unitData.propertyData, ANGER_LUCK_ID);
            Log("[PROBE] after: hasAnger=" + hadAfter);

            Log("[PROBE] === done ===");
        }

        // =============================================================
        // AttackSchool prefix — 回到手動加（v19.1 方式）
        // =============================================================
        public static void Patch_AttackSchool(SchoolWar __instance, MapBuildSchool target)
        {
            if (attackHandled) return;
            attackHandled = true;
            Log("[ATTACK] triggered");

            savedPrisonerIds.Clear();
            targetSchoolID = null;

            if (target == null) { OpenDeclareDrama(); return; }

            try
            {
                dynamic bd = target.buildData;
                if (bd != null)
                {
                    string rid = bd.id;
                    if (!string.IsNullOrEmpty(rid))
                    {
                        targetSchoolID = rid;
                        Log("[ATTACK] targetSchoolID=" + targetSchoolID);
                    }
                }
            }
            catch { }

            if (targetSchoolID == null) { OpenDeclareDrama(); return; }

            HashSet<string> allMembers = new HashSet<string>();
            try
            {
                dynamic bd = target.buildData;
                CollectList(bd.npcIn, allMembers);
                CollectList(bd.npcElders, allMembers);
                CollectList(bd.npcBigElders, allMembers);
                CollectList(bd.npcInherit, allMembers);
                try
                {
                    string leader = bd.npcSchoolMain;
                    if (!string.IsNullOrEmpty(leader)) allMembers.Add(leader);
                }
                catch { }
            }
            catch { }

            Log("[ATTACK] total members=" + allMembers.Count);

            int femaleCount = 0;
            int angerAdded = 0;

            foreach (string uid in allMembers)
            {
                try
                {
                    WorldUnitBase unit = null;
                    try { unit = g.world.unit.allUnit[uid]; } catch { continue; }
                    if (unit == null) continue;

                    var pd = unit.data.unitData.propertyData;
                    if ((int)pd.sex != 2) continue;

                    femaleCount++;
                    savedPrisonerIds.Add(uid);
                    string name = pd.GetName();

                    // 宣戰時不加氣運，只存 ID（氣運改在戰後加）
                    Log("[SAVE] " + name + " id=" + uid);
                }
                catch { }
            }

            Log("[ATTACK] females=" + femaleCount + " saved=" + savedPrisonerIds.Count);

            // Add new war record
            LoadAllRecords();
            var newRec = new WarRecord();
            newRec.schoolID = targetSchoolID ?? "unknown";
            newRec.phase = "pre-war";
            newRec.gameTime = GetGameTime();
            newRec.index = 0;
            newRec.prisoners = new List<string>(savedPrisonerIds);
            allRecords.Add(newRec);
            SaveAllRecords();
            Log("[ATTACK] record added: school=" + newRec.schoolID + " gameTime=" + newRec.gameTime);

            OpenDeclareDrama();
        }

        private static void CollectList(dynamic list, HashSet<string> target)
        {
            if (list == null) return;
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    try
                    {
                        string uid = list[i];
                        if (!string.IsNullOrEmpty(uid)) target.Add(uid);
                    }
                    catch { break; }
                }
            }
            catch { }
        }

        private static void OpenDeclareDrama()
        {
            try
            {
                DramaTool.OpenDrama(DRAMA_DECLARE_1, new DramaData()
                {
                    unitLeft = g.world.playerUnit
                });
                Log("[ATTACK] drama " + DRAMA_DECLARE_1 + " OK");
            }
            catch (Exception ex)
            {
                Log("[ATTACK] drama FAIL: " + ex.Message);
            }
        }

        // =============================================================
        // File persistence (JSON records)
        // =============================================================
        private static string GetSaveFilePath()
        {
            string playerID = "unknown";
            try { playerID = g.world.playerUnit.data.unitData.unitID; } catch { }
            return Path.Combine(modDir ?? ".", "warhistory_" + playerID + ".json");
        }

        private static int GetGameTime()
        {
            try { return g.world.run.roundMonth; } catch { return 0; }
        }

        // --- Minimal JSON serializer (no external deps) ---
        private static string RecordsToJson(List<WarRecord> records)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n  \"records\": [\n");
            for (int r = 0; r < records.Count; r++)
            {
                var rec = records[r];
                sb.Append("    {\n");
                sb.Append("      \"schoolID\": \"" + EscapeJson(rec.schoolID) + "\",\n");
                sb.Append("      \"phase\": \"" + EscapeJson(rec.phase) + "\",\n");
                sb.Append("      \"gameTime\": " + rec.gameTime + ",\n");
                sb.Append("      \"index\": " + rec.index + ",\n");
                sb.Append("      \"prisoners\": [");
                for (int p = 0; p < rec.prisoners.Count; p++)
                {
                    if (p > 0) sb.Append(", ");
                    sb.Append("\"" + EscapeJson(rec.prisoners[p]) + "\"");
                }
                sb.Append("]\n");
                sb.Append("    }" + (r < records.Count - 1 ? "," : "") + "\n");
            }
            sb.Append("  ]\n}");
            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static List<WarRecord> ParseRecordsFromJson(string json)
        {
            var result = new List<WarRecord>();
            // Simple line-based parser for our known format
            WarRecord current = null;
            foreach (string rawLine in json.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("\"schoolID\""))
                {
                    current = new WarRecord();
                    current.schoolID = ExtractJsonString(line);
                }
                else if (line.StartsWith("\"phase\"") && current != null)
                {
                    current.phase = ExtractJsonString(line);
                }
                else if (line.StartsWith("\"gameTime\"") && current != null)
                {
                    current.gameTime = ExtractJsonInt(line);
                }
                else if (line.StartsWith("\"index\"") && current != null)
                {
                    current.index = ExtractJsonInt(line);
                }
                else if (line.StartsWith("\"prisoners\"") && current != null)
                {
                    current.prisoners = ExtractJsonStringArray(line);
                }
                else if ((line == "}" || line == "},") && current != null)
                {
                    if (!string.IsNullOrEmpty(current.schoolID))
                        result.Add(current);
                    current = null;
                }
            }
            return result;
        }

        private static string ExtractJsonString(string line)
        {
            // "key": "value" or "key": "value",
            int colon = line.IndexOf(':');
            if (colon < 0) return "";
            string val = line.Substring(colon + 1).Trim().TrimEnd(',');
            if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2)
                return val.Substring(1, val.Length - 2);
            return val;
        }

        private static int ExtractJsonInt(string line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0) return 0;
            string val = line.Substring(colon + 1).Trim().TrimEnd(',');
            int result = 0;
            int.TryParse(val, out result);
            return result;
        }

        private static List<string> ExtractJsonStringArray(string line)
        {
            var result = new List<string>();
            int bracket = line.IndexOf('[');
            int end = line.LastIndexOf(']');
            if (bracket < 0 || end < 0 || end <= bracket) return result;
            string inner = line.Substring(bracket + 1, end - bracket - 1);
            foreach (string part in inner.Split(','))
            {
                string s = part.Trim();
                if (s.StartsWith("\"") && s.EndsWith("\"") && s.Length >= 2)
                    result.Add(s.Substring(1, s.Length - 2));
            }
            return result;
        }

        // --- WarRecord class ---
        private class WarRecord
        {
            public string schoolID = "";
            public string phase = "pre-war";  // pre-war, post-war, done
            public int gameTime = 0;
            public int index = 0;
            public List<string> prisoners = new List<string>();
        }

        private static List<WarRecord> allRecords = new List<WarRecord>();

        private static void LoadAllRecords()
        {
            allRecords.Clear();
            try
            {
                string path = GetSaveFilePath();
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path);
                allRecords = ParseRecordsFromJson(json);
                Log("[FILE] loaded " + allRecords.Count + " records from " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Log("[FILE] load error: " + ex.Message);
            }
        }

        private static void SaveAllRecords()
        {
            try
            {
                string path = GetSaveFilePath();
                string json = RecordsToJson(allRecords);
                File.WriteAllText(path, json);
                Log("[FILE] saved " + allRecords.Count + " records -> " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Log("[FILE] save error: " + ex.Message);
            }
        }

        private static WarRecord GetCurrentRecord()
        {
            // Find the last non-done record
            for (int i = allRecords.Count - 1; i >= 0; i--)
            {
                if (allRecords[i].phase != "done")
                    return allRecords[i];
            }
            return null;
        }

        private static void PruneRecordsByGameTime(int currentGameTime)
        {
            // Remove records from the future (player loaded an older save)
            int removed = 0;
            for (int i = allRecords.Count - 1; i >= 0; i--)
            {
                if (allRecords[i].gameTime > currentGameTime)
                {
                    Log("[FILE] pruned future record: school=" + allRecords[i].schoolID
                        + " gameTime=" + allRecords[i].gameTime + " > current=" + currentGameTime);
                    allRecords.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0)
            {
                Log("[FILE] pruned " + removed + " future records");
                SaveAllRecords();
            }
        }

        private static void SavePrisonerFile()
        {
            // Update current record's index and save
            var rec = GetCurrentRecord();
            if (rec != null)
            {
                rec.index = prisonerIndex;
                SaveAllRecords();
            }
        }

        private static void OnWorldEnter()
        {
            Log("[WORLD] IntoWorld fired");

            LoadAllRecords();
            int currentTime = GetGameTime();
            Log("[WORLD] gameTime=" + currentTime + " records=" + allRecords.Count);

            // Prune future records (player loaded older save)
            PruneRecordsByGameTime(currentTime);

            // Find incomplete record
            var rec = GetCurrentRecord();
            if (rec == null)
            {
                Log("[WORLD] no pending record");
                return;
            }

            if (rec.phase == "pre-war")
            {
                Log("[WORLD] found pre-war record for school=" + rec.schoolID + ", waiting for battle");
                // Restore savedPrisonerIds so SchoolWarClear can use them
                savedPrisonerIds.Clear();
                foreach (string uid in rec.prisoners)
                    savedPrisonerIds.Add(uid);
                return;
            }

            if (rec.phase == "post-war")
            {
                Log("[WORLD] resuming post-war sequence for school=" + rec.schoolID
                    + " idx=" + rec.index + "/" + rec.prisoners.Count);
                prisonerQueue.Clear();
                prisonerQueue.AddRange(rec.prisoners);
                prisonerIndex = rec.index;
                sequenceRunning = true;
                currentPrisonerId = null;
                // delay to let the world fully stabilize
                g.timer.Frame(new Action(() => { OpenNextPrisonerDrama(); }), 120, false);
            }
        }

        private static void RebuildPrisonerList()
        {
            savedPrisonerIds.Clear();
            try
            {
                var allUnit = g.world.unit.allUnit;
                var enumerator = allUnit.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    try
                    {
                        var pair = enumerator.Current;
                        WorldUnitBase unit = pair.Value;
                        if (unit == null) continue;
                        var ud = unit.data.unitData;
                        var pd = ud.propertyData;
                        if ((int)pd.sex != 2) continue;
                        if (!string.IsNullOrEmpty(ud.schoolID)) continue;
                        if (!HasLuckId(pd, ANGER_LUCK_ID)) continue;
                        savedPrisonerIds.Add(pair.Key);
                        Log("[REBUILD] " + pd.GetName() + " id=" + pair.Key);
                    }
                    catch { }
                }
            }
            catch { }
            Log("[REBUILD] found " + savedPrisonerIds.Count);
        }

        private static void RemoveAngerLuck(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            try
            {
                WorldUnitBase unit = null;
                try { unit = g.world.unit.allUnit[uid]; } catch { }
                if (unit == null) return;

                try
                {
                    var worldLuck = unit.GetLuck(ANGER_LUCK_ID);
                    if (worldLuck != null)
                    {
                        unit.DestroyLuck(worldLuck);
                        Log("[LUCK-DEL] DestroyLuck OK id=" + uid);
                    }
                }
                catch (Exception ex)
                {
                    Log("[LUCK-DEL] DestroyLuck fail id=" + uid + ": " + ex.Message);
                }

                try
                {
                    unit.data.unitData.propertyData.DelAddLuck(ANGER_LUCK_ID);
                    Log("[LUCK-DEL] DelAddLuck OK id=" + uid);
                }
                catch (Exception ex)
                {
                    Log("[LUCK-DEL] DelAddLuck fail id=" + uid + ": " + ex.Message);
                }
            }
            catch { }
        }

        private static void AddAngerLuck(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            try
            {
                WorldUnitBase unit = null;
                try { unit = g.world.unit.allUnit[uid]; } catch { return; }
                if (unit == null) return;

                var pd = unit.data.unitData.propertyData;
                if (!HasLuckId(pd, ANGER_LUCK_ID))
                {
                    var luckData = new DataUnit.LuckData();
                    luckData.id = ANGER_LUCK_ID;
                    luckData.duration = -1;
                    try { luckData.createTime = g.world.run.roundMonth; } catch { luckData.createTime = 1; }
                    unit.CreateLuck(luckData);
                    string name = pd.GetName();
                    Log("[LUCK-ADD] CreateLuck: " + name + " id=" + uid);
                }
            }
            catch { }
        }

        // =============================================================
        // ArrestNpcCondition prefix — 強制設置 unitRight
        // =============================================================
        public static bool Patch_ArrestNpcCondition(DramaFunction __instance)
        {
            // 只在我們的戰俅序列運行中才放行
            if (!sequenceRunning || string.IsNullOrEmpty(currentPrisonerId)) return true;

            try
            {
                WorldUnitBase unit = null;
                try { unit = g.world.unit.allUnit[currentPrisonerId]; } catch { }
                if (unit == null)
                {
                    Log("[ARREST] prisoner " + currentPrisonerId + " not found, skip override");
                    return true;
                }

                // 強制設置 DramaFunction._data.unitRight
                var dataField = typeof(DramaFunction).GetField("_data",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (dataField != null)
                {
                    var data = dataField.GetValue(__instance);
                    if (data != null)
                    {
                        var urField = data.GetType().GetField("unitRight",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (urField != null)
                        {
                            urField.SetValue(data, unit);
                            string name = unit.data.unitData.propertyData.GetName();
                            Log("[ARREST] forced unitRight = " + name + " id=" + currentPrisonerId);
                        }

                        // 也設 unitA (NPC-B 在遊戲裡可能是 unitA)
                        var uaField = data.GetType().GetField("unitA",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (uaField != null)
                        {
                            uaField.SetValue(data, unit);
                            Log("[ARREST] forced unitA too");
                        }
                    }
                }

                // 也設 lastDramaData
                try
                {
                    var lastData = DramaTool.lastDramaData;
                    if (lastData != null)
                    {
                        lastData.unitRight = unit;
                    }
                }
                catch { }

                // 跳過原始 ArrestNpcCondition（不讓它查抓取表）
                return false;
            }
            catch (Exception ex)
            {
                Log("[ARREST] error: " + ex.Message);
                return true;
            }
        }

        private static void ClearNpcCache()
        {
            try
            {
                DramaFunction df = new DramaFunction();
                try { df.ClearNpcConditionSave(MakeIl2CppArray(PRISONER_TABLE_ID.ToString())); } catch { }
                try { df.ClearNpcConditionSaveGroup(MakeIl2CppArray(PRISONER_TABLE_ID.ToString())); } catch { }
                Log("[CACHE] cleared");
            }
            catch { }
        }

        private static void OpenNextPrisonerDrama()
        {
            if (!sequenceRunning) return;

            if (prisonerIndex >= prisonerQueue.Count)
            {
                Log("[SEQ] all prisoners done, opening finish drama");
                sequenceRunning = false;
                currentPrisonerId = null;

                // Mark record as done
                var doneRec = GetCurrentRecord();
                if (doneRec != null)
                {
                    doneRec.phase = "done";
                    doneRec.index = prisonerQueue.Count;
                    SaveAllRecords();
                    Log("[FILE] record marked done");
                }
                try
                {
                    DramaTool.OpenDrama(DRAMA_FINISH, new DramaData()
                    {
                        unitLeft = g.world.playerUnit,
                        onDramaEndCall = new Action(() =>
                        {
                            Log("[SEQ] finish drama ended");
                        })
                    });
                    Log("[SEQ] finish drama " + DRAMA_FINISH + " OK");
                }
                catch (Exception ex)
                {
                    Log("[SEQ] finish drama FAIL: " + ex.Message);
                }
                return;
            }

            string uid = prisonerQueue[prisonerIndex];
            WorldUnitBase unit = null;
            try { unit = g.world.unit.allUnit[uid]; } catch { }
            if (unit == null)
            {
                Log("[SEQ] missing prisoner uid=" + uid + ", skip");
                prisonerIndex++;
                g.timer.Frame(new Action(() => { OpenNextPrisonerDrama(); }), 5, false);
                return;
            }

            currentPrisonerId = uid;
            string name = unit.data.unitData.propertyData.GetName();
            Log("[SEQ] opening prisoner #" + (prisonerIndex + 1) + "/" + prisonerQueue.Count + " : " + name + " id=" + uid);

            // 只給當前這個人加氣運，然後清快取讓抓取表重新查
            AddAngerLuck(uid);
            ClearNpcCache();

            // 更新進度到檔案
            SavePrisonerFile();

            try
            {
                DramaTool.OpenDrama(DRAMA_PRISONER, new DramaData()
                {
                    unitLeft = g.world.playerUnit,
                    unitRight = unit,
                    onDramaEndCall = new Action(() =>
                    {
                        Log("[SEQ] prisoner drama ended uid=" + uid);
                        RemoveAngerLuck(uid);
                        prisonerIndex++;
                        currentPrisonerId = null;
                        SavePrisonerFile();
                        g.timer.Frame(new Action(() => { OpenNextPrisonerDrama(); }), 10, false);
                    })
                });
                Log("[SEQ] prisoner drama " + DRAMA_PRISONER + " OK uid=" + uid);
            }
            catch (Exception ex)
            {
                Log("[SEQ] prisoner drama FAIL uid=" + uid + ": " + ex.Message);
            }
        }

        private static void StartPrisonerSequence()
        {
            prisonerQueue.Clear();
            prisonerIndex = 0;
            currentPrisonerId = null;

            foreach (string uid in savedPrisonerIds)
            {
                try
                {
                    WorldUnitBase unit = null;
                    try { unit = g.world.unit.allUnit[uid]; } catch { continue; }
                    if (unit == null) continue;
                    prisonerQueue.Add(uid);
                }
                catch { }
            }

            Log("[SEQ] queue=" + prisonerQueue.Count);
            if (prisonerQueue.Count == 0)
            {
                Log("[SEQ] empty queue");
                return;
            }

            sequenceRunning = true;
            SavePrisonerFile();
            g.timer.Frame(new Action(() => { OpenNextPrisonerDrama(); }), 30, false);
        }

        // =============================================================
        // SchoolWarClear prefix
        // =============================================================
        public static void Patch_SchoolWarClear(SchoolWar __instance)
        {
            long now = DateTime.Now.Ticks / TimeSpan.TicksPerSecond;
            if (now == lastBatchSec) return;
            lastBatchSec = now;
            batchCount++;

            Log("[CLEAR] batch #" + batchCount);

            if (batchCount == 1) return;

            if (batchCount == 2 && !postWarHandled)
            {
                postWarHandled = true;
                if (_instance == null) return;

                if (savedPrisonerIds.Count == 0)
                {
                    Log("[CLEAR] rebuilding prisoner list");
                    RebuildPrisonerList();
                }

                int found = 0;
                int revived = 0;
                int angerVerified = 0;

                foreach (string uid in savedPrisonerIds)
                {
                    try
                    {
                        WorldUnitBase unit = null;
                        try { unit = g.world.unit.allUnit[uid]; } catch { continue; }
                        if (unit == null) continue;

                        found++;
                        var pd = unit.data.unitData.propertyData;
                        string name = pd.GetName();
                        bool dead = unit.isDie;
                        bool hasAnger = HasLuckId(pd, ANGER_LUCK_ID);
                        string schoolID = unit.data.unitData.schoolID;

                        Log("[POST] " + name + " id=" + uid
                            + " isDie=" + dead + " anger=" + hasAnger
                            + " school=" + (string.IsNullOrEmpty(schoolID) ? "NONE" : schoolID));

                        if (hasAnger) angerVerified++;

                        if (dead)
                        {
                            int hp = pd.hpMax / 2;
                            if (hp < 1) hp = 1;
                            pd.hp = hp;
                            try { new DramaFunction().Revive(MakeIl2CppArray(uid)); revived++; }
                            catch { }
                        }
                    }
                    catch { }
                }

                Log("[POST] found=" + found + " revived=" + revived + " angerVerified=" + angerVerified);

                // 不再一次全部加氣運 — 改由 sequence 逐個加
                if (found > 0)
                {
                    // Update record to post-war
                    LoadAllRecords();
                    var rec = GetCurrentRecord();
                    if (rec != null)
                    {
                        rec.phase = "post-war";
                        rec.gameTime = GetGameTime();
                        SaveAllRecords();
                        Log("[POST] record updated to post-war");
                    }

                    Log("[SEQ] start direct prisoner sequence");
                    StartPrisonerSequence();
                }
            }

            if (batchCount > 2)
            {
                batchCount = 0;
                postWarHandled = false;
                attackHandled = false;
                prisonerQueue.Clear();
                prisonerIndex = 0;
                currentPrisonerId = null;
                sequenceRunning = false;
            }
        }
    }
}
