<div align="center">

<img src="docs/icon.png" width="128" alt="Ivalice Trainer">

# Ivalice Trainer

**《最終幻想戰略版 伊瓦利斯編年史》（FFT: The Ivalice Chronicles）PC 修改器，免費開源，無廣告，不需要帳號。**

<p align="center">
  <a href="README.md">English</a> |
  <a href="README.zh_CN.md">简体中文</a> |
  <strong>繁體中文</strong>
</p>

<p align="center">
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT">
  </a>
  <a href="https://github.com/cqagc/ivalice-trainer/releases/latest">
    <img src="https://img.shields.io/github/v/release/cqagc/ivalice-trainer?include_prereleases" alt="GitHub release">
  </a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/runtime-.NET%2010-blue" alt=".NET 10">
</p>

<p align="center">
  <a href="#description">簡介</a> •
  <a href="#screenshots">截圖</a> •
  <a href="#capabilities">功能</a> •
  <a href="#quick-start">快速上手</a> •
  <a href="#development">開發</a> •
  <a href="#documentation">文件與社群</a> •
  <a href="#upstream">上游專案</a> •
  <a href="#license">許可證</a> •
  <a href="#star-history">Star History</a>
</p>

</div>

<p align="center">
  <a href="docs/screenshots/battle.zh_TW.png">
    <img src="docs/screenshots/battle.zh_TW.png" width="960" alt="Ivalice Trainer · 戰場">
  </a>
</p>

---

<a id="description"></a>

## 📝 簡介

Ivalice Trainer 在遊戲執行時修改 FINAL FANTASY TACTICS - The Ivalice Chronicles（Steam 版）的編隊、道具和戰鬥。它直接讀寫遊戲記憶體，不需要 Cheat Engine。

支援 Windows 10 / 11 x64 上的 **enhanced 版（`FFT_enhanced.exe`）**。介面支援簡體中文、繁體中文、English、Français、Deutsch、日本語、한국어，與遊戲支援的語言一致，各語言都使用遊戲自己的用語。

> [!IMPORTANT]
> 本專案是愛好者製作的非官方工具，與 SQUARE ENIX 無關。修改記憶體有損壞存檔的風險。修改器寫入前會備份存檔目錄，但請自己也做好備份。

> [!NOTE]
> 記憶體地址按 Steam build 24304444 核對。遊戲更新後，在修改器跟進之前可能讀不到資料或拒絕寫入。當前版本 **0.2.0**。

---

<a id="screenshots"></a>

## 截圖

點選圖片檢視原尺寸。

| 編隊 | 物品 |
|---|---|
| [<img src="docs/screenshots/roster.zh_TW.png" width="460" alt="編隊">](docs/screenshots/roster.zh_TW.png) | [<img src="docs/screenshots/items.zh_TW.png" width="460" alt="物品">](docs/screenshots/items.zh_TW.png) |

---

<a id="capabilities"></a>

## 功能

| 功能 | 說明 |
|---|---|
| 編隊 | Gil、等級與 EXP、Brave 與 Faith、各項能力值。升級時按當前職業的成長值增長。 |
| 職業 JP | 按遊戲的規則增加 JP：當前 JP 和累計 JP 同時增加，職業等級按遊戲的門檻更新。 |
| 技能槽 | 正常模式只列出已學會且符合條件的技能，補充九名已核驗角色的專屬指令。跨角色副指令 Hack 預設關閉，切換指令不等於學習招式。 |
| 學習副指令招式 | 獨立實驗功能：預覽目前已裝備副指令的真實招式名稱／ID、已學與新增位元及寫入區域，暫存後再二次確認寫入。請先閱讀下方安全說明。 |
| 物品 | 支援搜尋和分類篩選；虛擬化回收清單、批次更新並抑制無變化通知，減少庫存卡頓。名稱旁顯示待寫數量 `舊值 → 新值`，整列醒目提示。 |
| 裝備詳情 | 從本機遊戲讀取官方說明及已核驗 EXE 靜態屬性；未知項明確顯示未知，武器威力不等於最終傷害。 |
| 戰鬥：生存 | 我方鎖血、盟友鎖血、我方無限 MP、凍結陣亡倒數計時。 |
| 戰鬥：行動 | 鎖定我方 CT、我方全員 CT 滿、總能移動、總能行動、移動 · 跳躍最大、詠唱即發。 |
| 倍率 | EXP 與 JP 獲得 2× / 5× / 10×，只放大遊戲實際給我方的量。 |
| 保留戰鬥設定 | 開關和倍率在戰鬥之間、重啟修改器後都保留。 |
| 暫存寫入 | 編隊和物品的改動先暫存，點 **寫入遊戲**（Ctrl+S）一次寫入；寫入前逐項核對遊戲裡的當前值。 |
| 存檔備份 | 寫入前把存檔目錄複製到 `%LOCALAPPDATA%\FftTrainer\save-backups`（10 分鐘內最多一次，保留最近 20 份）。 |

單位、職業、物品、技能的名稱在執行時從你本機的遊戲檔案裡按所選語言讀取，修改器本身不包含任何遊戲文字或素材。

---

<a id="quick-start"></a>

## 快速上手

1. 安裝 [.NET 10 桌面執行時](https://dotnet.microsoft.com/download/dotnet/10.0)（x64）。
2. 從 [GitHub Releases](https://github.com/cqagc/ivalice-trainer/releases/latest) 下載壓縮包並解壓。
3. 執行 `FftTrainer.App.exe`，再啟動遊戲，修改器會自動連線。

寫入編隊改動前，請先回到世界地圖；在商店或隊員介面裡寫入的改動，離開時可能被遊戲覆蓋。標題欄的語言選單可以切換介面語言。升級時保留自己的 `assets` 資料夾；本機頭像索引修正不代表發行包新增或散布遊戲版權圖片。

### 0.2.0 實驗技能操作與風險

- 正常技能規則補充 Orlandeau、Reis、Mustadio、Marach、Agrias、Beowulf、Rapha、Meliadoul、Cloud 九名角色已核驗的專屬指令，未知身分仍唯讀。獨立的**跨角色副指令 Hack 預設關閉**，只開放已核驗人類指令；它只換副指令，不學習招式，也不繞過武器和招式使用條件。
- 先寫入副指令改動並重新整理，再開啟**目前已裝備副指令**的學習預覽，核對真實招式名稱／ID、已學與新增數量及記憶體區域，確認暫存後再確認「寫入遊戲」。取消／重開相應選擇器、切換角色、斷線或復原會撤銷授權，必要時重新選擇或預覽。
- 解析 16 個行動槽的真實 ID（包括第 9 位構成的 ID 256），僅依位置 OR 非空有效招式的學習位元，保留已有位元；不覆蓋被動技能、JP 或職業。普通指令使用各自的學習索引；特殊指令共用 **k0**，其他特殊指令可能把相同位置也解釋為已學，不能隔離跨指令影響。
- 寫前核對身分、連線代次、目前副指令、技能表、原值及三份名冊，寫後讀回精確目標。寫入**不是原子交易**，失敗可能部分生效；學習功能沒有自動重試，請重新讀取檢查，不要連續盲點寫入。
- 已回報的遊戲實測：全劍技由 4 項變為全部 11 項可用，換成龍後 8 項全亮。需要**退出整個編隊／隊員選單回到世界地圖，再重新進入**才更新，不只是關閉技能彈窗。這不代表已驗證全部戰鬥、工作副本或存檔重讀路徑；遊戲仍可能覆蓋改動。
- 實驗前獨立備份存檔。「復原」只取消未寫入的暫存改動，不能回滾已完成的遊戲寫入。本版未修復既有 JP 部分寫入後的重試問題。

裝備數值只讀取已核驗 build 24304444 的 EXE。其他版本或未核驗欄位顯示未知，不推測盾／飾品迴避、元素或狀態效果；官方說明的讀取與數值是否可用相互獨立。

---

<a id="development"></a>

## 開發

需要 Windows 和 .NET 10 SDK：

```sh
git clone https://github.com/cqagc/ivalice-trainer.git
cd ivalice-trainer
dotnet build src/FftTrainer.App/FftTrainer.App.csproj -c Release
dotnet test tests/FftTrainer.Core.Tests/FftTrainer.Core.Tests.csproj -c Release
```

Windows 專用 `tests/FftTrainer.Inventory.Harness/` 使用真實 XAML 與合成名冊／工作階段檢查庫存回收和編輯，以及 `--hack-ui`、`--learn-ui`，不連線或寫入遊戲。庫存情境需互動桌面，會移動指標並聚焦測試視窗；技能介面情境需傳 `--game-root <遊戲安裝目錄>` 唯讀本機文字，在螢幕外執行且不啟用。可用 `--output <結果目錄>` 儲存本機截圖與結果，未經隱私和素材審查不要公開這些輸出或文字快取。

| 目錄 | 內容 |
|---|---|
| `src/FftTrainer.App/` | WPF 介面 |
| `src/FftTrainer.Core/` | 記憶體讀寫、遊戲規則、檢視模型和介面文字（`Locales/`） |
| `src/FftTrainer.GameFiles/` | 從遊戲封包讀取文字表 |
| `tests/FftTrainer.Core.Tests/` | 單元測試 |
| `tools/FftProbe/` | 命令列記憶體探針：數值掃描、AOB 掃描、讀寫 |
| `docs/` | 記憶體佈局筆記 |

---

<a id="documentation"></a>

## 文件與社群

| 資源 | 說明 |
|---|---|
| [記憶體佈局](docs/memory-map.md) | build 24304444 已確認的地址和結構 |
| `scripts/check_locales.py` | 檢查各語言的 key 和佔位符是否一致 |
| [GitHub Issues](https://github.com/cqagc/ivalice-trainer/issues) | 問題反饋和功能建議 |

反饋問題時請寫明修改器版本、遊戲語言，以及當時在遊戲裡做什麼。

---

<a id="upstream"></a>

## 🔗 上游專案

感謝以下專案的維護者和貢獻者。詳見 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

| 專案 | 在本專案中的用途 | 許可證 |
|---|---|---|
| [FF16Tools](https://github.com/Nenkai/FF16Tools) | 讀取封包和 Nex 表（原始碼子集） | MIT |
| [fftivc-nex-layouts](https://github.com/Nenkai/fftivc-nex-layouts) | 遊戲文字表的表結構 | MIT |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) | DirectStorage 託管繫結，用於解壓 | MIT |
| [Syroot.BinaryData](https://gitlab.com/Syroot/BinaryData) | 二進位制讀取 | MIT |
| [CommunityToolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) | 記憶體工具 | MIT |

DirectStorage 本身從遊戲安裝目錄載入，不隨修改器分發。

---

<a id="license"></a>

## 📜 許可證

Ivalice Trainer 以 [MIT](LICENSE) 許可證釋出。第三方元件保留各自的許可證。FINAL FANTASY TACTICS 是 SQUARE ENIX 的商標。

---

<a id="star-history"></a>

## 🌟 Star History

<div align="center">

[![Star History Chart](https://api.star-history.com/svg?repos=cqagc/ivalice-trainer&type=Date)](https://star-history.com/#cqagc/ivalice-trainer&Date)

</div>

---

<div align="center">

### 感謝

如果覺得好用，歡迎給專案點個 Star。

**[下載最新版](https://github.com/cqagc/ivalice-trainer/releases/latest)** • **[反饋問題](https://github.com/cqagc/ivalice-trainer/issues)**

由 [CQAGC](https://www.cqagc.com) 維護

</div>
