# Changelog

## [1.1.8] - 2026-05-02

### Added
- 對 `MapBuildSchool` 增加多名稱資料欄位探測：`buildData/data/schoolData/mapBuildData/build`。
- 若取得宗門 ID，直接掃 `allUnit.unitData.schoolID` 救回同宗門女性 NPC。
- 救援失敗時深挖 `MapBuildSchool` 本身欄位，找出實際資料名稱。

### Changed
- MOD log 版號更新為 `v33`。

## [1.1.7] - 2026-05-02

### Added
- 從 `schoolWarData.attackSchool` / `defendSchool` 的 `buildData` 直接恢復宗門成員名單。
- 依 `playerCamp` 優先選敵方宗門，攻方玩家時優先抓 `defendSchool`。

### Changed
- MOD log 版號更新為 `v32`。

## [1.1.6] - 2026-05-02

### Added
- 深挖 `schoolWarData` 內部欄位/屬性，列出 `[PROBE-DEEP]` 與 `[PROBE-LIST]` 候選 NPC 清單。
- 若 `schoolWarData` 內有可疑戰爭/宗門成員 list，嘗試自動對回 `allUnit` 救回女性戰俘。

### Changed
- MOD log 版號更新為 `v31`。

## [1.1.5] - 2026-05-02

### Added
- 沒有 `warhistory` 時，戰後嘗試從 `SchoolWar` instance 反射救回敵方宗門/成員名單。
- 增加 `[PROBE-SW]` 與 `[RECOVER]` log，用於定位已宣戰舊存檔是否仍保留戰爭狀態。

### Changed
- MOD log 版號更新為 `v30`。

## [1.1.4] - 2026-05-02

### Changed
- `warhistory_{玩家ID}.json` 改為直接寫入遊戲根目錄，不再依賴本地 MOD 或 Steam Workshop 路徑。
- 進世界取得玩家 ID 後，若 `warhistory_{玩家ID}.json` 不存在，先建立空 records 檔以驗證路徑與寫入權限。
- MOD log 版號更新為 `v29`。

## [1.1.3] - 2026-05-02

### Fixed
- 補上 Steam Workshop 安裝結構搜尋：`steamapps/workshop/content/1468810/<workshop-id>/ModCode/MOD_nV039M.dll`。
- 同時支援 DLL 位於 `ModCode/MOD_nV039M.dll` 與本地匯出的 `ModCode/dll/MOD_nV039M.dll`。

### Changed
- MOD log 版號更新為 `v28`。

## [1.1.2] - 2026-05-02

### Fixed
- 修正 IL2CPP/MelonLoader 載入時 `Assembly.Location` 為空，導致 v26 將 `warhistory` 寫到遊戲根目錄的問題。
- 新增從 `ModExportData/*/ModCode/dll/MOD_nV039M.dll` 反查真實 MOD DLL 目錄的 fallback。

### Changed
- MOD log 版號更新為 `v27`。

## [1.1.1] - 2026-05-02

### Fixed
- 移除 `ModMain.cs` 內固定 `D:\SteamLibrary...` 開發機路徑，改由目前載入的 DLL 動態解析 MOD 目錄。
- 宗門戰結束時先從 `warhistory_{玩家ID}.json` 恢復宣戰名單，再進行 fallback 掃描，避免重裝或換位置後記憶體名單清空就直接 `found=0`。

### Changed
- MOD log 版號更新為 `v26`。
- 增加 `[PATH]`、`[RESTORE]`、`[REBUILD]` 細節 log，方便確認實際讀寫路徑與人物掃描條件。

## [1.1.0] - 2026-04-07

### Added
- DLL 代碼注入（HarmonyLib prefix patch）
- 宗門戰結束後自動觸發戰俘處置序列
- 逐個處理戰俘，每人獨立開啟劇情 `820728301`
- 智慧氣運管理：`WorldUnitBase.CreateLuck()` / `DestroyLuck()`
- JSON 歷史記錄系統（`warhistory_{playerID}.json`）
- 三階段狀態管理：`pre-war` → `post-war` → `done`
- `EGameType.IntoWorld` 事件監聽，讀檔自動恢復處置進度
- 時間回溯偵測：讀取舊存檔時清除未來記錄
- 自動復活戰死 NPC 後再處置
- `SchoolWar.AttackSchool` hook：宣戰時記錄敵方女性成員
- `SchoolWar.SchoolWarClear` hook：偵測宗門戰結束（batch #2）
- `DramaFunction.ArrestNpcCondition` hook（備用覆寫機制）

### Removed
- 挑釁物道具及相關奇遇（改由 DLL 自動觸發宗門戰）
- 滅宗判定奇遇（不再需要手動觸發）

### Known Issues
- 門主（宗主）戰死後被遊戲從 `allUnit` 移除，無法復活或處置

### Technical Notes
- Il2Cpp 環境下 Harmony postfix 不可靠，全部使用 prefix
- 劇情內部跳轉不經過 `DramaTool.OpenDrama`，需用 code 控制序列
- 遊戲會在宗門戰期間 reload 資料，戰前加的氣運會被清除，因此改為戰後逐個加
- 抓取NPC表的「是否儲存」設定需改為「不儲存」才能正確切換立繪

## [1.0.1] - 2026-04-02

### Fixed
- 修正挑釁物奇遇觸發條件

## [1.0.0] - 2026-03

### Added
- 初始版本
- 基於原版「鬼畜灭宗（功能性模组—公开版）」
