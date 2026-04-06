# Changelog

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
- 純劇情編輯器實現
