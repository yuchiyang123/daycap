# 日額 DayCap

個人預算 app。不記帳，而是**先把這一期每一天、每一餐能花多少排好**，每天只要回報「跟預算不一樣」的地方；沒回報就當作照預算花掉。

- 前端：Vue 3 + TypeScript + Vite，PWA（iPhone 用 Safari「加入主畫面」就是全螢幕 app）
- 後端：ASP.NET Core 9 + EF Core + SQLite
- 登入：Mini-SSO（`auth.matthewyu.uk`），nginx 同網域轉發，後端用共用的 JWT 金鑰驗證 `token` cookie
- 部署：Docker Compose + yang 主機的 self-hosted runner + Cloudflare Tunnel（照 `_playbook/self-hosted-deploy-playbook.md`）
- 網址：`daycap.matthewyu.uk` → `localhost:12120`

## 為什麼不是原生 iOS app

側載 .ipa 需要 macOS/Xcode 簽章，免費 Apple ID 簽的每 7 天就失效；付費開發者帳號一年 US$99。這個 app 的資料本來就要存在後端，PWA 加到主畫面後體驗跟原生 app 幾乎一樣（全螢幕、有圖示、離開再回來會自動換日），更新也只要 push。

## 核心規則（`backend/DayCap.Api/Services/BudgetEngine.cs`）

| 情況 | 結果 |
|---|---|
| 沒回報的時段 | 視同照預算花掉 |
| 回報比預算少 | 差額進**待定區** |
| 回報比預算多 | 勾「先從待定區扣」→ 先扣待定區（扣到 0）；剩下的**依比例攤到同分類之後每一個還沒回報的時段**，後面每天的額度跟著遞減 |
| 後面沒有額度可攤 | 從待定區扣到負數 |
| 月額度類（衣、樂…）超支 | 超出月額度的部分從待定區扣 |
| 固定支出（鎖定） | 不能回報，不參與每日計算 |
| 回報時勾「這是訂閱」 | 這期當一般花費；下期起自動變成固定支出 |

- 回報輸入可以是「實際價格」或「超支多少」（勾選切換）。
- 待定區期初 = 收入 − 各分類額度 + 每日類「額度 − 實際排進日程」的差。
- 攤提、待定區都**不存結果**，每次讀取時把所有回報依時間重播算出來：刪除或修改一筆回報，影響自動修正。
- 「之後」以 max(回報的日期, 按下回報那天) 為界，補登過去的超支只會影響今天以後，結果也不會因為時間經過而改變。
- 假日：行政院人事行政總處辦公日曆表（含補假、補班，資料來源 [ruyut/TaiwanCalendar](https://github.com/ruyut/TaiwanCalendar)），每年抓一次快取；設定頁可以手動把某天改成假日 / 上班日。
- 改設定後可以「套用到本期」：只重排今天起的每日額度，過去的日子和回報都不動。

## 本機開發

```bash
# 後端（Development 預設開 DevAuth，不用跑 Mini-SSO 也能用；資料在 backend/DayCap.Api/App_Data/）
cd backend/DayCap.Api
dotnet run --launch-profile http        # http://localhost:5230

# 前端
cd frontend
npm install
npm run dev                             # http://localhost:5190（/api 轉到 5230）

# 測試
cd backend && dotnet test DayCap.sln
```

## 部署（第一次）

照 `_playbook/self-hosted-deploy-playbook.md`，專案特有的值：

| 項目 | 值 |
|---|---|
| repo 目錄 | `~/projects/daycap` |
| runner 目錄 / label | `~/actions-runner-daycap` / `daycap` |
| 對外 port | `127.0.0.1:12120` |
| Public Hostname | `daycap.matthewyu.uk` → `HTTP localhost:12120` |

1. 主機上 `cp .env.example .env`，`JWT_*` 三個值從 `~/projects/Mini-SSO/.env` 複製。
2. 因為前端是同網域轉發到 Mini-SSO，**不需要**把網域加進 Mini-SSO 的 CORS 白名單。
3. Mini-SSO 的 cookie 網域是 `.matthewyu.uk`，在其他子網域登入過的話，這裡直接就是登入狀態。
   用 Google/GitHub 登入完會被導回 Mini-SSO 的 `FRONTEND_REDIRECT_URL`（目前是 todo），再開回 `daycap.matthewyu.uk` 即可。
4. 在這個站不能註冊新帳號（nginx 擋掉 `/api/auth/create`）。

CI（`ci.yml`）每次 push 都跑 build + test + docker build；`main` 上 CI 通過後，`deploy.yml` 才在 self-hosted runner 上 `docker compose up -d --build`。

## iPhone 安裝

Safari 開 `https://daycap.matthewyu.uk` → 分享 → 加入主畫面。
