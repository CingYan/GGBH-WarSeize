# 🏯 鬼畜滅宗 — 戰後戰俘處置 MOD

**鬼谷八荒（Tale of Immortal）** 宗門戰後戰俘自動處置 MOD — 滅宗後一鍵處置敵方女性成員，加入宗門、放走、隨你決定。

> 基於原版「[鬼畜灭宗（功能性模组—公开版）](https://steamcommunity.com/sharedfiles/filedetails/?id=3438149783)」二次開發，加入 **DLL 代碼注入** 實現全自動化流程。

---

## ✨ 功能特色

| 功能 | 說明 |
|------|------|
| **全自動處置** | 宗門戰結束後自動逐一觸發戰俘處置劇情，不需手動前往宗門 |
| **智慧氣運管理** | 逐個加/刪憤怒氣運，確保劇情抓取表每次只找到當前目標 |
| **檔案持久化** | 宣戰時立即存檔，遊戲中斷、讀檔後自動恢復進度 |
| **完整歷史記錄** | 所有宗門戰記錄永久保存，支援多次宣戰不同宗門 |
| **時間回溯偵測** | 讀取舊存檔時自動清除「未來」的記錄，避免狀態錯亂 |
| **斷點續傳** | 處置到一半關閉遊戲，重新載入後從上次進度繼續 |
| **自動復活** | 戰死的 NPC 自動復活後再處置 |

---

## 📋 使用方式

### 前置需求
- [MelonLoader](https://melonwiki.xyz/) 已安裝
- 鬼畜八荒（必備前置 MOD）

### 操作流程
1. 在 [Steam 工作坊訂閱本 MOD](https://steamcommunity.com/sharedfiles/filedetails/?id=3683459718)（或手動放入 `ModExportData/` 資料夾）
2. 啟動遊戲，確認 MelonLoader console 顯示 `=== Init done (v25) ===`
3. 以宗主身份發起滅宗戰
4. 打完宗門戰後，MOD 自動觸發處置序列
5. 依照劇情選項決定每位戰俘的命運
6. 全部處理完畢後自動播放結束劇情

### 遊戲中斷？
- 宣戰後、開戰前中斷 → 讀檔後 MOD 記住戰俘名單，等宗門戰結束後自動處置
- 處置到一半中斷 → 讀檔後從上次的戰俘繼續
- 讀取更早的存檔 → 自動清除尚未發生的記錄

---

## ⚠️ 已知限制

### 宗主（門主）無法被處置（包含代宗主)
宗門戰結束後，遊戲會將**戰死的 NPC 從 `allUnit` 列表中完全移除**，而非僅標記 `isDie=true`。由於門主在滅宗戰中幾乎必定陣亡，且被移除後無法透過任何 API 復活或存取，因此**門主不會出現在戰俘處置序列中**。

這是遊戲底層的限制，並非 MOD 的 bug。其他存活的成員（長老、傳功、弟子等）則會正常被處置。

---

## 🗂️ 檔案結構

```
Mod_nV039M/
├── ModAssets/          # MOD 資源（劇情配置）
├── ModCode/
│   └── dll/
│       └── MOD_nV039M.dll    # 編譯後的 DLL
├── ModExportData.cache        # MOD 匯出快取
├── ModMain.cs                 # 原始碼
└── README.md
```

### 歷史記錄檔
MOD 運行時會在 DLL 同目錄下生成 `warhistory_{玩家ID}.json`：
```json
{
  "records": [
    {
      "schoolID": "s8klQ4",
      "phase": "done",
      "gameTime": 120,
      "index": 6,
      "prisoners": ["pluhUf", "ZPBwst", "H0Uudv", ...]
    }
  ]
}
```

| 欄位 | 說明 |
|------|------|
| `schoolID` | 敵方宗門的 runtime ID |
| `phase` | `pre-war`（宣戰後）→ `post-war`（處置中）→ `done`（完成） |
| `gameTime` | 遊戲內月份時間戳 |
| `index` | 處置進度（已處理幾人） |
| `prisoners` | 戰俘 NPC ID 列表 |

---

## ⚙️ 技術細節

- **框架：** MelonLoader MOD System
- **語言：** C# (.NET Framework 4.7.2)
- **環境：** IL2CPP
- **注入方式：** HarmonyLib prefix patch
- **Hook 方法：**
  - `SchoolWar.SchoolWarClear` — 偵測宗門戰結束（batch #2）
  - `SchoolWar.AttackSchool` — 攔截宣戰，記錄敵方成員
  - `DramaFunction.ArrestNpcCondition` — 強制覆寫劇情 NPC（備用）
- **事件監聽：** `EGameType.IntoWorld` — 讀檔恢復中斷的處置流程
- **氣運操作：** `WorldUnitBase.CreateLuck()` / `DestroyLuck()` — 正規 API
- **劇情觸發：** `DramaTool.OpenDrama()` + `onDramaEndCall` 回調鏈

### 為什麼用 DLL 而不是純劇情編輯器？
原版 MOD 使用純劇情編輯器實現，但有幾個限制：
- 宗門戰結束後沒有自動觸發機制，需要玩家手動前往宗門
- 一次只能處理一個戰俘，需要反覆進出宗門
- 無法持久化進度，中斷後需要重來

DLL 注入解決了這些問題，實現了完全自動化。

---

## 📝 更新日誌

### v1.1.0 (2026-04-07)
- 🆕 DLL 代碼注入，實現全自動處置流程
- 🆕 宗門戰結束後自動觸發戰俘處置序列
- 🆕 逐個處理戰俘，每人獨立開啟劇情
- 🆕 智慧氣運管理（逐個加/刪，配合抓取表）
- 🆕 JSON 歷史記錄系統，永久保存所有宗門戰記錄
- 🆕 三階段狀態管理（pre-war → post-war → done）
- 🆕 讀檔自動恢復中斷的處置進度
- 🆕 時間回溯偵測，讀取舊存檔時自動清除未來記錄
- 🆕 自動復活戰死 NPC
- 🆕 `IntoWorld` 事件監聽，支援存檔讀取恢復
- 🗑️ 移除挑釁物道具及相關奇遇
- 🗑️ 移除滅宗判定奇遇（改由 DLL 自動觸發）

### v1.0.1 (2026-04-02)
- 🐛 修正奇遇觸發條件

### v1.0.0 (2026-03)
- 🎉 初始版本（純劇情編輯器實現）
- 基於原版「鬼畜灭宗（功能性模组—公开版）」

---

## 👥 貢獻者

- [**CingYan**](https://github.com/CingYan) — 專案發起人、劇情設計、測試
- **Eagle (拍拍)** — AI 開發助手，負責 DLL 代碼注入、Harmony patch、持久化系統設計

---

## 📄 授權

MIT License

---

## 🙏 致謝

- 原版「[鬼畜灭宗（功能性模组—公开版）](https://steamcommunity.com/sharedfiles/filedetails/?id=3438149783)」作者
- [鬼谷八荒](https://store.steampowered.com/app/1468810/) 開發團隊
- [MelonLoader](https://melonwiki.xyz/) 社群
- 鬼畜八荒 MOD 團隊
- [OpenClaw](https://github.com/openclaw/openclaw) — AI 基礎設施
