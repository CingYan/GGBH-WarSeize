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
        private const string VERSION = "v40";

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
        private const string SAVE_FILE_PREFIX = "warhistory_"; // 歷史記錄檔名前綴，集中管理避免到處硬寫字串。
        private const string MOD_DLL_NAME = "MOD_nV039M.dll"; // MOD 匯出後的 DLL 檔名，用於 Assembly.Location 為空時反查真實目錄。
        private static string modDir = null; // 目前 MOD DLL 所在目錄；啟動時動態解析，不再綁死 Steam 安裝路徑。
        private static SchoolWar cachedSchoolWar = null; // 最近一次 Harmony patch 傳入的 SchoolWar instance；SchoolWar 不是 UnityEngine.Object，不能用 FindObjectOfType 找。

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

            modDir = ResolveModDirectory(); // 從目前載入的 DLL 反推目錄，避免換硬碟、換 Steam Library 或換 MOD ID 後失效。
            Log("[PATH] modDir=" + modDir); // 啟動時印出實際讀寫位置，方便 debug 找不到 warhistory 的問題。
            Log("[PATH] saveProbe=" + GetSaveFilePath()); // 同步印出完整存檔路徑，確認 MOD 實際會讀哪個檔案。

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

        private static string GetPlayerUnitId()
        {
            try { return g.world.playerUnit.data.unitData.unitID; } catch { return null; }
        }

        private static string GetPlayerSchoolId()
        {
            try { return g.world.playerUnit.data.unitData.schoolID; } catch { return null; }
        }

        private static string GetTargetSchoolId(MapBuildSchool target)
        {
            if (target == null) return null;
            try
            {
                object buildData = GetMemberValue(target, "buildData");
                string id = GetStringMember(buildData, "id");
                if (!string.IsNullOrEmpty(id)) return id;
            }
            catch { }
            try { return GetStringMember(target, "id"); } catch { return null; }
        }

        private static bool IsPlayerSchoolMain()
        {
            string playerId = GetPlayerUnitId();
            string schoolId = GetPlayerSchoolId();
            if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(schoolId) || schoolId == "0" || schoolId == "-1")
            {
                Log("[ATTACK] skip: player has no valid school");
                return false;
            }

            try
            {
                object playerData = g.world.playerUnit.data;
                foreach (string schoolMember in new string[] { "school", "_school" })
                {
                    object schoolWrap = GetMemberValue(playerData, schoolMember);
                    object buildData = GetMemberValue(schoolWrap, "buildData");
                    string leader = GetStringMember(buildData, "npcSchoolMain");
                    string id = GetStringMember(buildData, "id");
                    if (id == schoolId && leader == playerId) return true;

                    leader = GetStringMember(schoolWrap, "npcSchoolMain");
                    id = GetStringMember(schoolWrap, "id");
                    if (id == schoolId && leader == playerId) return true;
                }
            }
            catch (Exception ex)
            {
                Log("[ATTACK] leader check failed: " + ex.Message);
            }

            Log("[ATTACK] skip: player is not verified school main");
            return false;
        }

        // =============================================================
        // AttackSchool prefix — 只攔截玩家身為宗主時的主動宣戰
        // =============================================================
        public static void Patch_AttackSchool(SchoolWar __instance, MapBuildSchool target)
        {
            cachedSchoolWar = __instance; // 宣戰 hook 取得 SchoolWar instance 時先快取，供讀檔 hint 或後續 debug 使用。
            string playerSchoolId = GetPlayerSchoolId();
            string targetId = GetTargetSchoolId(target);
            if (!IsPlayerSchoolMain()) return; // 過月 NPC 宗門戰或玩家未當宗主時也會走 AttackSchool；這些不能彈挑釁劇情。
            if (!string.IsNullOrEmpty(playerSchoolId) && !string.IsNullOrEmpty(targetId) && targetId == playerSchoolId)
            {
                Log("[ATTACK] skip: target is player's own school " + targetId);
                return;
            }
            if (attackHandled) return;
            attackHandled = true;
            Log("[ATTACK] triggered");

            savedPrisonerIds.Clear();
            targetSchoolID = null;

            if (target == null) { Log("[ATTACK] skip: target is null"); return; }

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

            if (targetSchoolID == null) { Log("[ATTACK] skip: targetSchoolID unavailable"); return; }

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



        private static object GetMemberValue(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null; // 空物件或空名稱直接跳過。
            Type t = obj.GetType(); // 反射目前物件型別，用於讀取未知欄位。

            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); // 先找精確欄位。
                if (f != null) return f.GetValue(obj); // 找到欄位就回傳值。
            }
            catch { }

            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); // 再找精確屬性。
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(obj, null); // 只讀取非 indexer 屬性，避免觸發例外。
            }
            catch { }

            try
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f.GetValue(obj); // IL2CPP wrapper 有時大小寫不同，補 case-insensitive 欄位搜尋。
                }
            }
            catch { }

            try
            {
                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length == 0 && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.GetValue(obj, null); // 補 case-insensitive 屬性搜尋。
                }
            }
            catch { }

            return null; // 找不到就回傳 null。
        }

        private static string GetStringMember(object obj, string name)
        {
            try
            {
                object val = GetMemberValue(obj, name); // 讀取指定欄位/屬性。
                return val == null ? null : val.ToString(); // 轉成字串供 schoolID / buildData.id 使用。
            }
            catch { return null; }
        }

        private static void CollectListMember(object obj, string name, HashSet<string> target)
        {
            try
            {
                object val = GetMemberValue(obj, name); // 從 buildData 讀 npcIn/npcElders 等成員列表。
                if (val != null) CollectList((dynamic)val, target); // 交給既有 CollectList 支援 Il2Cpp list。
            }
            catch { }
        }

        private static int CollectFemalePrisonersFromMemberIds(HashSet<string> allMembers, string label)
        {
            int femaleCount = 0; // 實際可從 allUnit 找回的女性 NPC 數量。
            foreach (string uid in allMembers)
            {
                try
                {
                    WorldUnitBase unit = null; // 透過 runtime uid 找回世界角色。
                    try { unit = g.world.unit.allUnit[uid]; } catch { continue; } // 找不到代表角色已被移除或 uid 不是 NPC。
                    if (unit == null) continue; // 空角色跳過。

                    var pd = unit.data.unitData.propertyData; // 角色屬性資料。
                    if ((int)pd.sex != 2) continue; // 只保存女性 NPC。

                    savedPrisonerIds.Add(uid); // 加入戰俘候選名單。
                    femaleCount++; // 統計救回人數。
                    Log("[RECOVER] " + label + " saved " + pd.GetName() + " id=" + uid); // 列出救回的人。
                }
                catch { }
            }
            return femaleCount; // 回傳救回人數。
        }

        private static bool TryRecoverPrisonersFromBuildData(object buildData, string label)
        {
            if (buildData == null) return false; // 沒有 buildData 就不能從宗門成員恢復。

            HashSet<string> allMembers = new HashSet<string>(); // 收集宗門全部可能成員。
            CollectListMember(buildData, "npcIn", allMembers); // 內門/一般成員。
            CollectListMember(buildData, "npcElders", allMembers); // 長老。
            CollectListMember(buildData, "npcBigElders", allMembers); // 大長老。
            CollectListMember(buildData, "npcInherit", allMembers); // 傳承/真傳成員。

            try
            {
                string leader = GetStringMember(buildData, "npcSchoolMain"); // 宗主/門主 ID。
                if (!string.IsNullOrEmpty(leader)) allMembers.Add(leader); // 門主也加入候選。
            }
            catch { }

            Log("[RECOVER] " + label + " memberCandidates=" + allMembers.Count); // 先看有沒有抓到宗門成員。
            if (allMembers.Count == 0) return false; // 沒成員就不是可用 buildData。

            string schoolID = GetStringMember(buildData, "id"); // 嘗試取得宗門 runtime ID。
            if (!string.IsNullOrEmpty(schoolID)) targetSchoolID = schoolID; // 記錄宗門 ID 供歷史檔使用。

            int femaleCount = CollectFemalePrisonersFromMemberIds(allMembers, label); // 從候選成員裡救回女性 NPC。
            Log("[RECOVER] " + label + " females=" + femaleCount + " saved=" + savedPrisonerIds.Count + " school=" + (string.IsNullOrEmpty(schoolID) ? "unknown" : schoolID)); // 回報救援結果。
            return femaleCount > 0; // 有救到人才算成功。
        }


        private static int TryRecoverPrisonersBySchoolID(string schoolID, string label)
        {
            if (string.IsNullOrEmpty(schoolID)) return 0; // 沒宗門 ID 就不能用 schoolID 掃描。
            int scanned = 0; // 掃描到的同宗門角色數。
            int female = 0; // 同宗門女性角色數。

            try
            {
                var enumerator = g.world.unit.allUnit.GetEnumerator(); // 掃目前世界角色。
                while (enumerator.MoveNext())
                {
                    try
                    {
                        var pair = enumerator.Current; // pair.Key 是 NPC runtime ID。
                        WorldUnitBase unit = pair.Value; // 角色物件。
                        if (unit == null) continue; // 空角色跳過。

                        var ud = unit.data.unitData; // 角色資料。
                        if (ud.schoolID != schoolID) continue; // 只抓指定宗門。
                        scanned++; // 同宗門計數。

                        var pd = ud.propertyData; // 角色屬性。
                        if ((int)pd.sex != 2) continue; // 只保存女性 NPC。
                        female++; // 女性計數。
                        savedPrisonerIds.Add(pair.Key); // 加入戰俘候選。
                        Log("[RECOVER] " + label + " bySchoolID saved " + pd.GetName() + " id=" + pair.Key); // 列出救回的人。
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("[RECOVER] " + label + " bySchoolID scan error: " + ex.Message); // 掃描失敗保留原因。
            }

            Log("[RECOVER] " + label + " bySchoolID=" + schoolID + " scanned=" + scanned + " female=" + female); // 回報 schoolID 掃描結果。
            return female; // 回傳救回女性數量。
        }

        private static bool TryRecoverFromNamedChild(object candidate, string memberName, string label)
        {
            object child = GetMemberValue(candidate, memberName); // 嘗試讀取 MapBuildSchool 內可能保存 buildData 的欄位/屬性。
            if (child == null) return false; // 沒有這個 child 就跳過。

            if (TryRecoverPrisonersFromBuildData(child, label + "." + memberName)) return true; // child 本身若是 buildData，直接從宗門成員清單恢復。

            string childID = GetStringMember(child, "id"); // 若 child 有宗門 id，改用 schoolID 掃 allUnit。
            if (TryRecoverPrisonersBySchoolID(childID, label + "." + memberName) > 0) return true; // schoolID 掃描成功。

            return false; // 這個 child 不可用。
        }

        private static bool TryRecoverPrisonersFromCandidate(object candidate, string label)
        {
            if (candidate == null) return false; // 空候選跳過。
            if (TryRecoverPrisonersFromBuildData(candidate, label + ".self")) return true; // 有些欄位本身就是 buildData。

            string[] childNames = new string[] { "buildData", "BuildData", "data", "Data", "schoolData", "SchoolData", "mapBuildData", "MapBuildData", "build", "Build" }; // MapBuildSchool 在不同 wrapper 版本可能用不同名稱保存資料。
            foreach (string childName in childNames)
            {
                if (TryRecoverFromNamedChild(candidate, childName, label)) return true; // 逐一嘗試可能的宗門資料欄位。
            }

            string selfID = GetStringMember(candidate, "id"); // 如果 MapBuildSchool 本身有 id，直接用 schoolID 掃 allUnit。
            if (TryRecoverPrisonersBySchoolID(selfID, label + ".self") > 0) return true; // schoolID 掃描成功。

            string schoolID = GetStringMember(candidate, "schoolID"); // 也嘗試 schoolID 欄位。
            if (TryRecoverPrisonersBySchoolID(schoolID, label + ".schoolID") > 0) return true; // schoolID 掃描成功。

            DeepProbeObject(candidate, label + ".probe", 1); // 還是救不到時，深挖 MapBuildSchool 本身，找實際欄位名。
            return false; // 不是可恢復候選。
        }

        private static void ProbeSchoolWarMembers(object obj)
        {
            if (obj == null) return; // 沒有 SchoolWar instance 就無法 probe。
            Type t = obj.GetType(); // 取得 SchoolWar runtime 型別。
            int logged = 0; // 限制 log 數量，避免洗版。

            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (logged >= 80) break; // 最多印 80 個可疑欄位。
                string n = f.Name.ToLower(); // 欄位名轉小寫方便比對。
                if (!(n.Contains("school") || n.Contains("target") || n.Contains("build") || n.Contains("war") || n.Contains("atk") || n.Contains("def") || n.Contains("enemy"))) continue; // 只印可能相關欄位。
                try
                {
                    object val = f.GetValue(obj); // 讀欄位值。
                    Log("[PROBE-SW] field " + f.Name + " type=" + f.FieldType.Name + " value=" + (val == null ? "NULL" : val.ToString())); // 印出欄位名稱/型別/值。
                    logged++; // 計數。
                }
                catch { }
            }

            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (logged >= 80) break; // 最多印 80 個可疑屬性。
                if (p.GetIndexParameters().Length > 0) continue; // 跳過 indexer。
                string n = p.Name.ToLower(); // 屬性名轉小寫方便比對。
                if (!(n.Contains("school") || n.Contains("target") || n.Contains("build") || n.Contains("war") || n.Contains("atk") || n.Contains("def") || n.Contains("enemy"))) continue; // 只印可能相關屬性。
                try
                {
                    object val = p.GetValue(obj, null); // 讀屬性值。
                    Log("[PROBE-SW] prop " + p.Name + " type=" + p.PropertyType.Name + " value=" + (val == null ? "NULL" : val.ToString())); // 印出屬性名稱/型別/值。
                    logged++; // 計數。
                }
                catch { }
            }
        }



        private static int CountValidFemaleUnits(HashSet<string> ids, string label)
        {
            int valid = 0; // 可在 allUnit 找到的 NPC 數量。
            int female = 0; // 可在 allUnit 找到且性別為女性的 NPC 數量。
            int sample = 0; // sample log 限制，避免洗版。

            foreach (string uid in ids)
            {
                try
                {
                    WorldUnitBase unit = null; // 嘗試用候選字串當 NPC runtime id。
                    try { unit = g.world.unit.allUnit[uid]; } catch { continue; } // 不是 NPC id 就跳過。
                    if (unit == null) continue; // 空角色跳過。
                    valid++; // 找得到角色。

                    var pd = unit.data.unitData.propertyData; // 取得角色屬性。
                    if ((int)pd.sex == 2)
                    {
                        female++; // 女性角色計數。
                        if (sample < 5)
                        {
                            Log("[PROBE-LIST] " + label + " femaleSample=" + pd.GetName() + " id=" + uid); // 印前 5 個女性樣本，判斷是不是敵宗成員。
                            sample++; // 增加樣本計數。
                        }
                    }
                }
                catch { }
            }

            Log("[PROBE-LIST] " + label + " ids=" + ids.Count + " validUnits=" + valid + " femaleUnits=" + female); // 回報候選清單品質。
            return female; // 回傳女性數量，供自動救援判斷。
        }

        private static bool TryCollectStringIds(object listObj, HashSet<string> target)
        {
            if (listObj == null) return false; // 空清單直接失敗。
            int before = target.Count; // 記錄收集前數量。

            try
            {
                dynamic d = listObj; // Il2Cpp list 通常支援 Count 與 indexer。
                int count = d.Count; // 嘗試讀 Count。
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object item = d[i]; // 讀取第 i 個元素。
                        if (item != null)
                        {
                            string uid = item.ToString(); // 轉成字串候選 ID。
                            if (!string.IsNullOrEmpty(uid)) target.Add(uid); // 加入候選集合。
                        }
                    }
                    catch { break; }
                }
            }
            catch { }

            return target.Count > before; // 有新增字串才算成功。
        }




        private static string TryExtractUnitId(object obj, int depth)
        {
            if (obj == null || depth <= 0) return null; // 空物件或深度用完就停止。

            string[] idNames = new string[] { "unitID", "UnitID", "unitId", "id", "ID", "uid", "UID" }; // 常見角色 ID 欄位名稱。
            foreach (string idName in idNames)
            {
                try
                {
                    object val = GetMemberValue(obj, idName); // 嘗試讀取角色 ID。
                    if (val != null)
                    {
                        string uid = val.ToString(); // 轉字串。
                        if (!string.IsNullOrEmpty(uid) && HasWorldUnit(uid)) return uid; // 必須能對回 allUnit 才接受。
                    }
                }
                catch { }
            }

            string[] childNames = new string[] { "unit", "Unit", "worldUnit", "WorldUnit", "unitData", "UnitData", "data", "Data", "baseData", "BaseData" }; // 常見巢狀角色資料欄位。
            foreach (string childName in childNames)
            {
                try
                {
                    object child = GetMemberValue(obj, childName); // 讀巢狀物件。
                    string uid = TryExtractUnitId(child, depth - 1); // 往下一層找角色 ID。
                    if (!string.IsNullOrEmpty(uid)) return uid; // 找到就回傳。
                }
                catch { }
            }

            return null; // 找不到有效角色 ID。
        }

        private static bool HasWorldUnit(string uid)
        {
            try
            {
                WorldUnitBase unit = g.world.unit.allUnit[uid]; // 嘗試用 ID 對回世界角色。
                return unit != null; // 找得到才是真 NPC ID。
            }
            catch { return false; }
        }

        private static WorldUnitBase TryExtractWorldUnitBase(object obj, int depth)
        {
            if (obj == null || depth <= 0) return null; // 空物件或深度用完就停止。
            try
            {
                WorldUnitBase direct = obj as WorldUnitBase; // v37：SchoolWar UnitData.unit 已確認就是 WorldUnitBase。
                if (direct != null) return direct; // 直接拿到角色物件就回傳。
            }
            catch { }

            string[] childNames = new string[] { "unit", "Unit", "worldUnit", "WorldUnit", "unitData", "UnitData", "data", "Data", "baseData", "BaseData" }; // 常見巢狀角色物件欄位。
            foreach (string childName in childNames)
            {
                try
                {
                    object child = GetMemberValue(obj, childName); // 讀巢狀物件。
                    WorldUnitBase unit = TryExtractWorldUnitBase(child, depth - 1); // 往下一層找 WorldUnitBase。
                    if (unit != null) return unit; // 找到就回傳。
                }
                catch { }
            }

            return null; // 找不到 WorldUnitBase。
        }

        private static string TryResolveWorldUnitId(WorldUnitBase unit)
        {
            if (unit == null) return null; // 空角色不能解析 ID。
            try
            {
                string uid = unit.data.unitData.unitID; // 先用角色自身 unitID。
                if (!string.IsNullOrEmpty(uid) && HasWorldUnit(uid)) return uid; // 必須能對回 allUnit 才接受。
            }
            catch { }

            try
            {
                var enumerator = g.world.unit.allUnit.GetEnumerator(); // unitID 不可靠時，用 allUnit 反查同一個物件。
                while (enumerator.MoveNext())
                {
                    try
                    {
                        var pair = enumerator.Current; // pair.Key 是 runtime id，pair.Value 是 WorldUnitBase。
                        if (pair.Value == unit) return pair.Key; // 找到相同角色物件。
                    }
                    catch { }
                }
            }
            catch { }

            return null; // 無法解析可持久化 ID。
        }

        private static bool TryRecoverFromSchoolWarUnitList(object listObj, string label)
        {
            if (listObj == null) return false; // 沒清單就不能恢復。
            HashSet<string> directUnitIds = new HashSet<string>(); // v37：從 UnitData.unit 直接抽出的 WorldUnitBase ID。
            HashSet<string> fallbackIds = new HashSet<string>(); // 舊版反射 ID 備援。

            try
            {
                dynamic d = listObj; // SchoolWar 的 attackUnits/defendUnits 是 Il2Cpp list。
                int count = d.Count; // 讀取數量。
                Log("[PROBE-UNITLIST] " + label + " count=" + count); // 回報清單大小。
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object item = d[i]; // 取出 UnitData。
                        if (item == null) continue; // 空項跳過。
                        Log("[PROBE-UNITLIST] " + label + "[" + i + "] type=" + item.GetType().Name + " value=" + item.ToString()); // 印出項目型別。

                        WorldUnitBase directUnit = TryExtractWorldUnitBase(item, 3); // v37：優先從 defendUnits[].unit / attackUnits[].unit 抽 WorldUnitBase。
                        string directUid = TryResolveWorldUnitId(directUnit); // 將 WorldUnitBase 轉成可寫入 warhistory 的 runtime id。
                        if (!string.IsNullOrEmpty(directUid))
                        {
                            directUnitIds.Add(directUid); // 直接命中就加入主候選。
                            continue; // 不再依賴 DeepProbe / buildData。
                        }

                        DeepProbeObject(item, label + "[" + i + "]", 1); // 直接抽不到才深挖 UnitData 欄位，保留下一輪 debug 資料。
                        string uid = TryExtractUnitId(item, 3); // 舊版備援：嘗試自動抽出角色 ID。
                        if (!string.IsNullOrEmpty(uid)) fallbackIds.Add(uid); // 加入備援候選。
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("[PROBE-UNITLIST] " + label + " error=" + ex.Message); // 清單讀取失敗保留原因。
            }

            if (directUnitIds.Count > 0)
            {
                int saved = CollectFemalePrisonersFromMemberIds(directUnitIds, label + ".unit"); // v37 主路徑：直接從 WorldUnitBase 篩女性。
                Log("[RECOVER] " + label + " directWorldUnits=" + directUnitIds.Count + " saved=" + saved); // 回報直接抽取結果。
                if (saved > 0) return true; // 有保存到人才算成功。
            }

            if (fallbackIds.Count == 0)
            {
                Log("[RECOVER] " + label + " no world-unit ids extracted"); // 沒抽到有效角色 ID。
                return false; // 無法恢復。
            }

            int fallbackSaved = CollectFemalePrisonersFromMemberIds(fallbackIds, label + ".fallback"); // 反射 ID 備援。
            Log("[RECOVER] " + label + " fallbackIds=" + fallbackIds.Count + " saved=" + fallbackSaved); // 回報結果。
            return fallbackSaved > 0; // 有保存到人才算成功。
        }

        private static int GetIntMember(object obj, string name, int fallback)
        {
            try
            {
                object val = GetMemberValue(obj, name); // 讀取指定欄位/屬性。
                if (val == null) return fallback; // 沒值就用 fallback。
                return Convert.ToInt32(val); // 轉成 int，例如 schoolWarData.playerCamp。
            }
            catch { return fallback; }
        }

        private static bool TryRecoverFromSchoolWarDataSchools(object schoolWarData)
        {
            if (schoolWarData == null) return false; // 沒有 schoolWarData 就不能用宗門物件恢復。

            int playerCamp = GetIntMember(schoolWarData, "playerCamp", 0); // 讀取玩家陣營，1 通常代表攻方。
            object attackSchool = GetMemberValue(schoolWarData, "attackSchool"); // 攻方宗門 MapBuildSchool。
            object defendSchool = GetMemberValue(schoolWarData, "defendSchool"); // 守方宗門 MapBuildSchool。
            object attackWarData = GetMemberValue(schoolWarData, "attackWarData"); // 攻方戰爭資料。
            object defendWarData = GetMemberValue(schoolWarData, "defendWarData"); // 守方戰爭資料。
            object attackUnits = GetMemberValue(schoolWarData, "attackUnits"); // 攻方參戰單位清單。
            object defendUnits = GetMemberValue(schoolWarData, "defendUnits"); // 守方參戰單位清單。
            bool defendDead = false; // 守方是否滅宗。
            try { defendDead = Convert.ToBoolean(GetMemberValue(schoolWarData, "defendSchoolDie")); } catch { }

            Log("[RECOVER] schoolWarData playerCamp=" + playerCamp + " defendSchoolDie=" + defendDead); // 印出判斷敵方依據。

            if (playerCamp == 1)
            {
                if (TryRecoverFromSchoolWarUnitList(defendUnits, "schoolWarData.defendUnits.enemy")) return true; // v37：玩家為攻方時，敵方優先從守方參戰單位 UnitData.unit 抽 WorldUnitBase。
                DeepProbeObject(defendWarData, "schoolWarData.defendWarData.enemy", 1); // 直接名單救不到時才深挖守方戰爭資料。
                if (TryRecoverPrisonersFromCandidate(defendSchool, "schoolWarData.defendSchool.enemy.fallback")) return true; // 最後才用宗門 buildData 備援，避免依賴已被改寫的 defendSchool。
                Log("[RECOVER] enemy defendUnits unavailable; refusing to fallback to attackSchool to avoid capturing own sect"); // 明確拒絕抓自己宗門。
            }
            else if (playerCamp == 2)
            {
                if (TryRecoverFromSchoolWarUnitList(attackUnits, "schoolWarData.attackUnits.enemy")) return true; // v37：玩家為守方時，敵方優先從攻方參戰單位 UnitData.unit 抽 WorldUnitBase。
                DeepProbeObject(attackWarData, "schoolWarData.attackWarData.enemy", 1); // 直接名單救不到時才深挖攻方戰爭資料。
                if (TryRecoverPrisonersFromCandidate(attackSchool, "schoolWarData.attackSchool.enemy.fallback")) return true; // 最後才用攻方宗門 buildData 備援。
                Log("[RECOVER] enemy attackUnits unavailable; refusing to fallback to defendSchool to avoid capturing own sect"); // 明確拒絕抓自己宗門。
            }
            else
            {
                if (defendDead && TryRecoverPrisonersFromCandidate(defendSchool, "schoolWarData.defendSchool.dead")) return true; // 不知道陣營時，優先抓已滅的守方。
                if (TryRecoverFromSchoolWarUnitList(defendUnits, "schoolWarData.defendUnits.unknown")) return true; // 再試守方參戰單位。
                Log("[RECOVER] unknown playerCamp; not trying attackSchool fallback automatically"); // 不明陣營時也避免亂抓攻方。
            }

            return false; // 敵方資料救不到。
        }

        private static bool TryRecoverFromAnyStringLists(object obj, string label)
        {
            if (obj == null) return false; // 空物件不能掃。
            Type t = obj.GetType(); // 取得型別。
            bool recovered = false; // 是否已恢復名單。

            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    object val = f.GetValue(obj); // 讀欄位值。
                    HashSet<string> ids = new HashSet<string>(); // 存候選字串 ID。
                    if (!TryCollectStringIds(val, ids)) continue; // 不是 list 或沒有字串就跳過。
                    int females = CountValidFemaleUnits(ids, label + ".field." + f.Name); // 對回 allUnit 看是否像 NPC 清單。
                    if (females > 0 && IsLikelyWarMemberList(f.Name))
                    {
                        int saved = CollectFemalePrisonersFromMemberIds(ids, label + ".field." + f.Name); // 欄位名像戰爭/宗門名單才自動救援。
                        recovered = saved > 0; // 有存到人才算成功。
                        if (recovered) break; // 成功後停止，避免混入其他清單。
                    }
                }
                catch { }
            }

            if (!recovered)
            {
                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        if (p.GetIndexParameters().Length > 0) continue; // 跳過 indexer。
                        object val = p.GetValue(obj, null); // 讀屬性值。
                        HashSet<string> ids = new HashSet<string>(); // 存候選字串 ID。
                        if (!TryCollectStringIds(val, ids)) continue; // 不是 list 或沒有字串就跳過。
                        int females = CountValidFemaleUnits(ids, label + ".prop." + p.Name); // 對回 allUnit 看是否像 NPC 清單。
                        if (females > 0 && IsLikelyWarMemberList(p.Name))
                        {
                            int saved = CollectFemalePrisonersFromMemberIds(ids, label + ".prop." + p.Name); // 屬性名像戰爭/宗門名單才自動救援。
                            recovered = saved > 0; // 有存到人才算成功。
                            if (recovered) break; // 成功後停止。
                        }
                    }
                    catch { }
                }
            }

            return recovered; // 回傳是否救援成功。
        }

        private static bool IsLikelyWarMemberList(string name)
        {
            if (string.IsNullOrEmpty(name)) return false; // 沒名稱不自動救援，避免誤抓。
            string n = name.ToLower(); // 小寫比對。
            return n.Contains("npc") || n.Contains("unit") || n.Contains("member") || n.Contains("school") || n.Contains("def") || n.Contains("enemy") || n.Contains("target") || n.Contains("war"); // 僅自動接受看起來像戰爭/宗門成員的清單。
        }

        private static void DeepProbeObject(object obj, string label, int depth)
        {
            if (obj == null || depth <= 0) return; // 空物件或深度用完就停止。
            Type t = obj.GetType(); // 取得型別。
            int logged = 0; // 限制每層 log 數量。

            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (logged >= 80) break; // 避免洗版。
                try
                {
                    object val = f.GetValue(obj); // 讀欄位值。
                    Log("[PROBE-DEEP] " + label + ".field." + f.Name + " type=" + f.FieldType.Name + " value=" + (val == null ? "NULL" : val.ToString())); // 印欄位基本資訊。
                    logged++; // 計數。

                    HashSet<string> ids = new HashSet<string>(); // 嘗試當清單解析。
                    if (TryCollectStringIds(val, ids)) CountValidFemaleUnits(ids, label + ".field." + f.Name); // 若是字串清單，回報對應 NPC 數。
                }
                catch { }
            }

            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (logged >= 80) break; // 避免洗版。
                if (p.GetIndexParameters().Length > 0) continue; // 跳過 indexer。
                try
                {
                    object val = p.GetValue(obj, null); // 讀屬性值。
                    Log("[PROBE-DEEP] " + label + ".prop." + p.Name + " type=" + p.PropertyType.Name + " value=" + (val == null ? "NULL" : val.ToString())); // 印屬性基本資訊。
                    logged++; // 計數。

                    HashSet<string> ids = new HashSet<string>(); // 嘗試當清單解析。
                    if (TryCollectStringIds(val, ids)) CountValidFemaleUnits(ids, label + ".prop." + p.Name); // 若是字串清單，回報對應 NPC 數。
                }
                catch { }
            }
        }



        private static SchoolWar FindActiveSchoolWar()
        {
            if (cachedSchoolWar != null) return cachedSchoolWar; // SchoolWar 不是 UnityEngine.Object，不能用 FindObjectOfType；只能使用 patch 傳入過的 instance。
            Log("[WORLD-HINT] no cached SchoolWar instance yet"); // 讀檔後若沒有任何 SchoolWar hook 先觸發，這裡會是正常拿不到。
            return null; // 沒有快取 instance 就交給 AttackSchool / SchoolWarClear hook 再補抓。
        }

        private static void TryCaptureWarHintAfterWorldEnter()
        {
            try
            {
                Log("[WORLD-HINT] delayed scan start"); // 讀檔後延遲掃描，讓遊戲世界與戰爭資料有時間初始化。
                LoadAllRecords(); // 讀取目前 warhistory，避免重複建立同一場紀錄。

                var existing = GetCurrentRecord(); // 檢查是否已有未完成紀錄。
                if (existing != null && existing.prisoners != null && existing.prisoners.Count > 0)
                {
                    Log("[WORLD-HINT] skip: pending record already has prisoners phase=" + existing.phase + " school=" + existing.schoolID + " count=" + existing.prisoners.Count); // 已有名單就不覆蓋。
                    return; // 避免讀檔掃描覆寫宣戰當下保存的正確名單。
                }

                SchoolWar sw = FindActiveSchoolWar(); // 嘗試找場景中的 SchoolWar。
                if (sw == null)
                {
                    Log("[WORLD-HINT] no active SchoolWar found"); // 讀檔當下沒有 SchoolWar，代表只能靠戰後 Clear 或更早 hook。
                    return; // 沒有 instance 就無法抓 hint。
                }

                object schoolWarData = GetMemberValue(sw, "schoolWarData"); // 取得戰爭狀態資料。
                if (schoolWarData == null)
                {
                    Log("[WORLD-HINT] SchoolWar found but schoolWarData is null"); // 有元件但資料未建立。
                    return; // 無資料就停止。
                }

                HashSet<string> backup = new HashSet<string>(savedPrisonerIds); // 備份目前記憶體名單，避免掃描失敗清掉狀態。
                string backupSchool = targetSchoolID; // 備份目前宗門 ID。
                savedPrisonerIds.Clear(); // 讀檔 hint 只保存這次掃到的敵方名單。
                targetSchoolID = null; // 讓本次掃描重新決定敵方宗門 ID。

                DeepProbeObject(schoolWarData, "worldHint.schoolWarData", 1); // 讀檔時間點也列出戰爭狀態，方便和戰後 Clear 對照。
                bool ok = TryRecoverFromSchoolWarDataSchools(schoolWarData); // 用同一套敵方判斷抓取讀檔時的敵方名單。

                if (!ok || savedPrisonerIds.Count == 0)
                {
                    Log("[WORLD-HINT] no prisoners captured from load-time SchoolWar state"); // 讀檔時抓不到名單。
                    savedPrisonerIds = backup; // 還原原本名單。
                    targetSchoolID = backupSchool; // 還原原本宗門 ID。
                    return; // 等戰後 Clear 再比對。
                }

                LoadAllRecords(); // 準備寫入 hint record 前重新讀檔。
                var rec = GetCurrentRecord(); // 若有空 pending record，直接補上；否則新增。
                if (rec == null || rec.prisoners == null || rec.prisoners.Count > 0)
                {
                    rec = new WarRecord(); // 建立讀檔 hint 紀錄。
                    allRecords.Add(rec); // 加入歷史紀錄。
                }

                rec.schoolID = string.IsNullOrEmpty(targetSchoolID) ? "world-hint" : targetSchoolID; // 保存讀檔時判定的敵方宗門 ID。
                rec.phase = "pre-war"; // 讀檔 hint 等同宣戰後、戰前紀錄，讓 SchoolWarClear 可直接 restore。
                rec.gameTime = GetGameTime(); // 保存目前遊戲時間。
                rec.index = 0; // 尚未開始處理戰俘。
                rec.prisoners = new List<string>(savedPrisonerIds); // 保存讀檔時抓到的敵方女性名單。
                SaveAllRecords(); // 寫入遊戲根目錄 warhistory。
                Log("[WORLD-HINT] saved load-time hint school=" + rec.schoolID + " prisoners=" + rec.prisoners.Count); // 回報讀檔抓取成功。
            }
            catch (Exception ex)
            {
                Log("[WORLD-HINT] delayed scan error: " + ex.Message); // 延遲掃描不能影響遊戲主流程。
            }
        }

        private static void RecoverPrisonersFromSchoolWarState(SchoolWar instance)
        {
            if (instance == null) return; // 沒有 instance 就不能從 SchoolWar 狀態恢復。
            Log("[RECOVER] probing SchoolWar state"); // 標記開始從戰爭狀態救援。
            ProbeSchoolWarMembers(instance); // 先印出可疑欄位，讓下一輪 debug 有資料。

            object schoolWarData = GetMemberValue(instance, "schoolWarData"); // v30 log 顯示 SchoolWar 內有 schoolWarData，v31 進一步深挖它。
            if (schoolWarData != null)
            {
                DeepProbeObject(schoolWarData, "schoolWarData", 1); // 列出 schoolWarData 內部欄位與可能的 NPC id 清單。
                if (TryRecoverFromSchoolWarDataSchools(schoolWarData)) Log("[RECOVER] recovered from schoolWarData school object"); // v32：直接從 attackSchool/defendSchool 的 buildData 救援。
                if (savedPrisonerIds.Count == 0 && TryRecoverFromAnyStringLists(schoolWarData, "schoolWarData")) Log("[RECOVER] recovered from schoolWarData string list"); // 宗門物件救不到時，再嘗試可疑字串清單。
            }

            Type t = instance.GetType(); // 取得 SchoolWar 型別。
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    object val = f.GetValue(instance); // 讀取每個欄位作為候選。
                    if (TryRecoverPrisonersFromCandidate(val, "field." + f.Name)) break; // 找到可用宗門資料就停止。
                }
                catch { }
            }

            if (savedPrisonerIds.Count == 0)
            {
                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        if (p.GetIndexParameters().Length > 0) continue; // 跳過 indexer。
                        object val = p.GetValue(instance, null); // 讀取每個屬性作為候選。
                        if (TryRecoverPrisonersFromCandidate(val, "prop." + p.Name)) break; // 找到可用宗門資料就停止。
                    }
                    catch { }
                }
            }

            if (savedPrisonerIds.Count > 0)
            {
                LoadAllRecords(); // 將救回名單寫入歷史檔，避免同一輪後續又遺失。
                var rec = new WarRecord(); // 建立補救紀錄。
                rec.schoolID = string.IsNullOrEmpty(targetSchoolID) ? "recovered" : targetSchoolID; // 有宗門 ID 就寫宗門 ID，否則標 recovered。
                rec.phase = "post-war"; // 已經在戰後 CLEAR，因此直接標 post-war。
                rec.gameTime = GetGameTime(); // 記錄目前遊戲時間。
                rec.index = 0; // 從第一位戰俘開始處理。
                rec.prisoners = new List<string>(savedPrisonerIds); // 保存救回名單。
                allRecords.Add(rec); // 加入紀錄。
                SaveAllRecords(); // 寫檔。
                Log("[RECOVER] record added from SchoolWar state saved=" + savedPrisonerIds.Count); // 回報補救成功。
            }
            else
            {
                Log("[RECOVER] SchoolWar state did not expose prisoner list"); // 回報這一戰無法從狀態救回。
            }
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

        private static string ResolveModDirectory()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory; // 統一使用遊戲根目錄保存 warhistory，避免本地 MOD / Workshop / MOD ID 路徑差異。
            if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
            {
                Log("[PATH] using game root for save files=" + baseDir); // 明確記錄現在固定寫到遊戲根目錄。
                return baseDir; // warhistory_{玩家ID}.json 直接放在 鬼谷八荒 根目錄。
            }

            string currentDir = Environment.CurrentDirectory; // 極端情況下 baseDir 不可用，才退回目前工作目錄。
            Log("[PATH] baseDir unavailable, fallback currentDir=" + currentDir); // fallback 也要有 log。
            return string.IsNullOrEmpty(currentDir) ? "." : currentDir; // 永遠回傳可組路徑的值，避免 GetSaveFilePath 產生 null。
        }

        private static void RestorePrisonersFromRecordIfNeeded()
        {
            if (savedPrisonerIds.Count > 0) return; // 記憶體已有宣戰名單時不覆蓋，避免重複讀檔打亂當前流程。

            LoadAllRecords(); // 從 warhistory 讀回宣戰時保存的 NPC ID，這是戰後找人的主要來源。
            var rec = GetCurrentRecord(); // 取最後一筆尚未完成的宗門戰紀錄。
            if (rec == null)
            {
                Log("[RESTORE] no pending record at " + GetSaveFilePath()); // 找不到紀錄時把路徑打出來，方便確認是不是裝到新位置。
                return; // 沒紀錄就交給後續 rebuild fallback。
            }

            foreach (string uid in rec.prisoners)
            {
                if (!string.IsNullOrEmpty(uid)) savedPrisonerIds.Add(uid); // 只恢復有效 ID，避免空字串進入處置序列。
            }
            targetSchoolID = rec.schoolID; // 同步恢復宗門 ID，後續 debug log 才知道是哪場戰。
            Log("[RESTORE] restored " + savedPrisonerIds.Count + " prisoners from record phase=" + rec.phase + " school=" + rec.schoolID); // 明確區分讀檔恢復與動態掃描。
        }

        // =============================================================
        // File persistence (JSON records)
        // =============================================================
        private static string GetSaveFilePath()
        {
            string playerID = "unknown"; // 以玩家 ID 分檔，避免不同角色的宗門戰紀錄互相污染。
            try { playerID = g.world.playerUnit.data.unitData.unitID; } catch { } // 尚未進世界時可能拿不到玩家，保留 unknown 供啟動探測使用。

            string saveDir = modDir; // 存檔目錄統一走 ResolveModDirectory，目前固定為遊戲根目錄。
            if (string.IsNullOrEmpty(saveDir)) saveDir = ResolveModDirectory(); // 若 Init 前被呼叫，仍即時解析一次。

            try { Directory.CreateDirectory(saveDir); } catch { } // 確保 fallback 目錄存在；失敗時讓後續 File I/O log 真正錯誤。
            return Path.Combine(saveDir, SAVE_FILE_PREFIX + playerID + ".json"); // 組出完整 warhistory 路徑。
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
                if (!File.Exists(path))
                {
                    Log("[FILE] no record file at " + path); // 讀不到檔時輸出完整路徑，方便定位是不是 MOD 安裝位置不同。
                    return; // 沒有歷史檔不是錯誤，可能是第一次使用。
                }
                string json = File.ReadAllText(path); // 讀取目前玩家的宗門戰歷史紀錄。
                allRecords = ParseRecordsFromJson(json);
                Log("[FILE] loaded " + allRecords.Count + " records from " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Log("[FILE] load error: " + ex.Message);
            }
        }


        private static void EnsureRecordFileExists()
        {
            try
            {
                string path = GetSaveFilePath(); // 進世界後玩家 ID 已可取得，這裡會是 warhistory_{玩家ID}.json。
                if (File.Exists(path)) return; // 已有紀錄檔就不覆蓋，避免清掉既有戰俘進度。

                string dir = Path.GetDirectoryName(path); // 取出要寫入的資料夾，通常是 Workshop 的 ModCode 或 ModCode/dll。
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); // 確保資料夾存在，若權限不足會進 catch。

                File.WriteAllText(path, RecordsToJson(allRecords)); // 第一次進世界先建立空 records 檔，讓玩家可直接確認路徑與寫入權限。
                Log("[FILE] created empty record file -> " + path); // 明確告知檔案已建立在哪裡。
            }
            catch (Exception ex)
            {
                Log("[FILE] create empty record file error: " + ex.Message); // 若 Workshop 目錄不可寫，這裡會直接看到原因。
            }
        }

        private static void SaveAllRecords()
        {
            try
            {
                string path = GetSaveFilePath();
                string json = RecordsToJson(allRecords);
                File.WriteAllText(path, json); // 寫入動態解析出的 MOD 目錄，不再寫入固定開發機路徑。
                Log("[FILE] saved " + allRecords.Count + " records -> " + path); // 保存完整路徑，讓測試者可直接確認檔案位置。
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
            EnsureRecordFileExists(); // 第一次進世界也建立空 warhistory 檔，避免誤以為路徑解析成功但無法寫入。
            try
            {
                g.timer.Frame(new Action(() => { TryCaptureWarHintAfterWorldEnter(); }), 120, false); // 讀檔後延遲 120 frame，同步抓一次戰前/戰中 SchoolWar 線索。
                Log("[WORLD-HINT] scheduled delayed scan 120f"); // 讓 log 可確認讀檔掃描有排程。
            }
            catch (Exception ex)
            {
                Log("[WORLD-HINT] schedule error: " + ex.Message); // 排程失敗不影響原本讀檔恢復流程。
            }
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
            savedPrisonerIds.Clear(); // 清空舊掃描結果，避免 fallback 結果和宣戰紀錄混在一起。
            int scanned = 0; // 掃過的 world unit 數量，用來判斷是不是 allUnit 尚未載入。
            int female = 0; // 女性 NPC 數量，用來判斷 sex 條件是否正常。
            int noSchool = 0; // 無宗門女性數量，用來判斷戰後脫離宗門狀態是否存在。
            int anger = 0; // 帶憤怒氣運的人數，用來判斷抓取表條件是否存在。

            try
            {
                var allUnit = g.world.unit.allUnit; // fallback 掃描目前世界裡仍存在的 NPC。
                var enumerator = allUnit.GetEnumerator(); // Il2Cpp collection 以 enumerator 逐一讀取。
                while (enumerator.MoveNext())
                {
                    try
                    {
                        var pair = enumerator.Current; // pair.Key 是 NPC runtime ID，pair.Value 是 WorldUnitBase。
                        WorldUnitBase unit = pair.Value; // 取出 NPC 單位。
                        if (unit == null) continue; // 空單位跳過。
                        scanned++; // 計算實際掃描量。

                        var ud = unit.data.unitData; // 角色基礎資料。
                        var pd = ud.propertyData; // 角色屬性資料。
                        if ((int)pd.sex != 2) continue; // 只處理女性 NPC。
                        female++; // 計算女性數量。

                        if (!string.IsNullOrEmpty(ud.schoolID)) continue; // 戰後戰俘通常應已無宗門；仍有宗門者跳過。
                        noSchool++; // 計算無宗門女性數量。

                        if (!HasLuckId(pd, ANGER_LUCK_ID)) continue; // fallback 只接受已有憤怒氣運者，避免誤抓路人。
                        anger++; // 計算符合抓取表氣運條件的人數。

                        savedPrisonerIds.Add(pair.Key); // 加入 fallback 戰俘名單。
                        Log("[REBUILD] " + pd.GetName() + " id=" + pair.Key); // 列出被 fallback 抓到的人。
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log("[REBUILD] scan error: " + ex.Message); // 掃描本身失敗時保留錯誤訊息。
            }
            Log("[REBUILD] scanned=" + scanned + " female=" + female + " noSchool=" + noSchool + " anger=" + anger + " found=" + savedPrisonerIds.Count); // 比單純 found=0 更容易定位卡在哪個條件。
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
            cachedSchoolWar = __instance; // 戰後 clear hook 一定會提供 SchoolWar instance，快取供同輪 recovery 使用。
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
                    Log("[CLEAR] restoring prisoner list from warhistory"); // 戰後優先讀宣戰時保存的名單，而不是直接用氣運硬掃。
                    RestorePrisonersFromRecordIfNeeded(); // 解決換 MOD 安裝位置或 reload 後記憶體名單清空的問題。
                }

                if (savedPrisonerIds.Count == 0)
                {
                    Log("[CLEAR] recovering prisoner list from SchoolWar state"); // 沒有 warhistory 時，嘗試從目前 SchoolWar instance 反射救回宣戰目標。
                    RecoverPrisonersFromSchoolWarState(__instance); // 補救已宣戰但 MOD 尚未記錄的舊存檔。
                }

                if (savedPrisonerIds.Count == 0)
                {
                    Log("[CLEAR] rebuilding prisoner list fallback"); // SchoolWar 狀態也救不到時，才進入最後 fallback 掃描。
                    RebuildPrisonerList(); // fallback 保守掃描，避免亂抓非戰俘 NPC。
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
