# Phase 2 设计 — 翻译层 + 悬浮显示中文（端到端 MVP）

> 状态：设计定稿，待用户复审 → writing-plans。
> 日期：2026-07-02。分支基线：`main`（Phase 1 已合入）。

## 0. 目标与验收

**目标**：最小可用闭环——游戏房间列表里的韩文，经 OCR → 翻译 → 悬浮层原位显示**中文**。

**验收**：SC:R 窗口化开着"自定义游戏"列表，启动探测后，悬浮层在 Game Name 列韩文位置显示**对应中文**（而非现在的韩文）；切换翻译引擎（DashScope / ChatGPT 订阅）均可出中文；重复内容不重复请求 API；`logs/phase1-ocr.log` 记录原文+译文+引擎+耗时。

## 1. 关键决策（已与用户对齐）

1. **两个 provider 都进 Phase 2**：阿里云 DashScope（API Key）+ ChatGPT 订阅（Codex OAuth）。
2. **编排 = 单活跃 + 可切换**：同一时刻只用一个 provider（配置/托盘选定），另一个备用。**不做**两级翻译（快态→终态）——留 Phase 3。
3. **范围含**：变化检测 + 翻译缓存、DPAPI 加密存密钥/token、术语表精确命中直出、最小托盘配置入口。
4. **实现方式 = 裸 HttpClient，不用 SDK**：共享 OpenAI 兼容 Chat 客户端（DashScope）+ 专用 Codex Responses 流式(SSE)客户端（ChatGPT）。
5. **玩家 ID 不翻**：Phase 2 只翻 Game Name 列 ROI，ID 列不在 ROI 内，天然不翻；`TranslatePlayerId`/F7 留 Phase 4。

**参考实现**（ChatGPT 订阅机制的可信来源，仅参考机制、**不搬真实密钥**）：`D:\sanyan\projects\news-videos-workflow`，
关键文件 `backend/app/oauth/openai_oauth.py`（PKCE 登录 + 刷新）、`backend/app/providers/text/openai_text.py`（Responses 流式调用）、`model_providers.yaml`（provider 配置形态）。

## 2. 架构与分层

### Core（`ScrTranslator.Core/Translation/`，无 UI/WinRT，可单测）
- `ITranslationProvider` — 翻译提供方接口。
- `TranslationService` — 编排：术语表 → 缓存 → 活跃 provider；批量；写回缓存。
- `TranslationCache` — 有界 LRU，键=规整后韩文，值=中文。
- `Glossary` — 从 `docs/GLOSSARY.md` 解析术语，精确命中直出。**格式**：md 表格 `| 韩文 | 中文 | 备注 |`（第 3 列为上下文备注）；韩文/中文单元格可含 ` / ` 分隔的多写法（如 `인구수 / 인구`），韩文侧每个写法都注册为独立键、中文取该单元格首选项（第一个 ` / ` 段）。解析器只认表格行、跳过表头/分隔行/非表格文本。**注**：术语多为单词级黑话，整行精确命中对完整房间名基本不会命中——这符合设计（长句交给 provider），glossary 只截黑话短词，不期望覆盖长句。
- `ITokenSource` — 抽象"取一个有效的 ChatGPT access token + accountId"，App 侧实现（含刷新/持久化）。
- `TranslationSettings` — 活跃引擎、各引擎模型、超时秒数等（**非密钥**，可明文）。
- `Providers/OpenAiCompatibleChatClient` — POST `/chat/completions`，任意 OpenAI 兼容端点（DashScope 用）。
- `Providers/CodexResponsesClient` — POST `/responses` 流式(SSE)，ChatGPT codex 端点。
- `Providers/DashScopeProvider` / `Providers/ChatGptSubscriptionProvider` — 实现 `ITranslationProvider`，薄封装上面两个 client + system prompt 组装 + 行数对齐解析。

> 注：Core 现有类型分布不一致（`RoiConfig`/`RoiStore` 在扁平 `ScrTranslator.Core`，OCR 在 `.Ocr`）。本 Phase 新代码统一放 `ScrTranslator.Core.Translation` 子命名空间，不动既有类型。

### App（组合根/桌面/密钥）
- `Secrets/SecretStore` — DPAPI（`ProtectedData`, `CurrentUser`）加解密，落盘 `%AppData%/ScrTranslator/secrets.dat`。
- `Auth/OpenAiOAuth` — PKCE 登录（浏览器 + 本地 1455 回调）+ `refresh_tokens`，实现 `ITokenSource`，token 经 `SecretStore` 持久化。
- 托盘菜单扩展：登录 ChatGPT / 设置阿里云 Key / 选活跃引擎。
- `Phase1Probe` 接入 `TranslationService`（OCR 结果 → 翻译 → 上屏中文）。
- DI 注册：`TranslationService`、两个 provider、`SecretStore`、`OpenAiOAuth`、`TranslationSettings`。

## 3. 接口与数据流

```csharp
namespace ScrTranslator.Core.Translation;

public interface ITranslationProvider
{
    string Name { get; }                       // "dashscope" / "chatgpt"
    // 一帧多行一次请求；返回与入参等长、逐行对应的中文；某行失败可回原文。
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> koreanLines, CancellationToken ct);
}
```

**System prompt（固定，中文说明）**：把韩文逐行翻成简体中文；这是《星际争霸：重制版》联机房间/聊天语境；**只输出译文**，输入几行就输出几行、**行数与顺序一一对应**；玩家 ID、纯英文/数字/符号原样保留；不加解释。
**批量协议**：用户消息把 N 行各自前缀 `[i]` 序号列出（`[1] …\n[2] …`），要求模型逐行返回**同序号同行数**的译文；解析时按 `[i]` 还原为 N 条。任一序号缺失/重复/行数不符 → 整批降级回原文并记警告。

**数据流（`Phase1Probe.OnFrame` 内，替换"直接上屏韩文"）**：
1. OCR 得 `lines`（韩文 + bounds）。
2. **变化检测**：与上帧该 ROI 的韩文集合比较；完全未变 → 跳过翻译与上屏刷新（沿用上帧 overlay）。
3. 对每行文本：先 `Glossary.TryGet` 命中→中文；否则 `TranslationCache.TryGet` 命中→中文。
4. 未命中行收集成批 → `activeProvider.TranslateAsync` → 逐行写 `TranslationCache`。
5. 组装 `OverlayItem(中文, x, y)`（坐标沿用 Phase 1 的 `ScreenMapper` 映射）→ 上屏。
6. 日志：每行记 `原文 | 译文 | 引擎 | 来源(glossary/cache/api) | 耗时`。

## 4. 两个 provider 调用细节

### DashScope（`OpenAiCompatibleChatClient`）
- `POST {baseUrl}/chat/completions`，`Authorization: Bearer {apiKey}`。
- baseUrl `https://dashscope.aliyuncs.com/compatible-mode/v1`，默认 model **待核实**（参考项目配置写 `qwen3.6-flash`，但 DashScope 兼容模式官方常见为 `qwen-flash`/`qwen-plus`/`qwen-turbo`；实现前按官方模型表确认，错 id 会首调 4xx）。
- body：`{ "model", "messages":[{system},{user}], "stream": false }`。
- 解析 `choices[0].message.content`。

### ChatGPT 订阅（`CodexResponsesClient`）
- `POST https://chatgpt.com/backend-api/codex/responses`。
- 头：`Authorization: Bearer {accessToken}`、`ChatGPT-Account-ID: {accountId}`、`originator: codex_cli_rs`。
- body：`{ "model", "instructions": {system}, "input":[{"role":"user","content":[{"type":"input_text","text":{user}}]}], "stream": true, "store": false }`。默认 model **待确认**（`gpt-5` / `gpt-5-codex`；参考项目默认 `gpt-4o`。codex responses 端点接受的 id 在实现期显式确认一次，避免反复试）。
- **必须流式**：读 SSE，累积 `event: response.output_text.delta` 的 `delta`，遇 `response.completed` 或 `[DONE]` 结束。非流式会被拒。
- token 由 `ITokenSource.GetAsync(ct)`（返回 accessToken+accountId），App 侧临期刷新。

## 5. 密钥、登录与配置（App）

### SecretStore
- 明文结构（内存）：`{ dashScopeApiKey, openAi: { accessToken, refreshToken, idToken, accountId, expiresAt, planType, email } }`。
- 序列化 JSON → `ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)` → 写 `secrets.dat`。读时反向。文件缺失/损坏 → 视为空配置。
- 依赖包：`System.Security.Cryptography.ProtectedData`（App 工程）。

### OpenAiOAuth（实现 ITokenSource，复刻参考项目）
- 常量同参考：`CLIENT_ID=app_EMoamEEZ73f0CkXaXp7hrann`、`AUTH_BASE=https://auth.openai.com`、`REDIRECT_URI=http://localhost:1455/auth/callback`、`SCOPE=openid profile email offline_access`、`ORIGINATOR=codex_cli_rs`。
- 登录：`gen_pkce`(S256) → 浏览器打开 authorize URL（空格编码 `%20`）→ **本地 1455 监听**用 `TcpListener`（127.0.0.1 + 尽量兼顾 ::1）手解单个 GET 取 `code`/`state`，避开 `HttpListener` 的 URL ACL；→ `exchange_code`(POST `/oauth/token`, grant=authorization_code) → 存 tokens。
- `GetValidAccessToken`：解 access_token JWT 的 `exp`/`chatgpt_account_id`/`plan_type`/`email`（不验签，仅本地读）；距过期 <5min 用 `refresh_token`（POST `/oauth/token`, `grant_type=refresh_token` + `client_id` + `refresh_token` + **`scope=SCOPE`**，scope 必带否则可能刷新失败）刷新并回写 SecretStore；无 refresh_token → 抛"未登录"。
- 线程安全：刷新加锁，锁内重读避免重复刷新。

### 托盘菜单（最小配置入口，非完整配置窗口）
- `登录 ChatGPT（订阅）` → 跑 OAuth；完成后菜单显示登录邮箱/plan。
- `设置阿里云 Key` → 小 WPF 输入框，存 SecretStore。
- `翻译引擎 ▸ DashScope | ChatGPT（订阅）` → 单选，写 `TranslationSettings`；切到未就绪的引擎（未登录/未填 key）时提示。

## 6. 错误处理与降级

- provider 抛错/超时/未登录/未配 key → 记日志，**该行降级显示原文韩文**（不崩溃、不阻塞取帧）；可在悬浮/托盘给轻提示。
- 超时短（默认 10s，可配 8–12s）；实时场景失败即回退，不重试堆积。
- 命中 Glossary/Cache 不走网络。
- 批量行数不符 → 整批回原文并记警告。
- OAuth 未登录且活跃引擎为 ChatGPT → 上屏回原文 + 提示登录。

## 7. 测试策略

**Core 单测（无网络，TDD）**：
- `Glossary`：命中/未命中、大小写与首尾空白规整、缺文件不崩。
- `TranslationCache`：命中、LRU 淘汰、容量上界。
- `TranslationService` 编排：术语优先于缓存优先于 provider；批量只请求未命中行；provider 调用次数；行数对应；provider 抛错时降级回原文。
- `OpenAiCompatibleChatClient` / `CodexResponsesClient`：用假 `HttpMessageHandler` 打桩——验证请求 URL/头/body 形态；DashScope 解析 `choices[0].message.content`；Codex 累积多个 `output_text.delta`；非 2xx/异常 → 抛可降级异常。
- OAuth 纯函数：PKCE 生成（S256 长度/字符集）、JWT claims 解析、临期刷新判定（给定 exp 计算是否刷新）。

**手动验收（无法自动化）**：真实 1455 回调 + 浏览器登录、真实 DashScope/ChatGPT 网络往返、实机上屏中文、引擎切换。

## 8. YAGNI / 明确不做（留后续 Phase）

两级翻译（快态→终态原地替换）、术语表热更新/编辑 UI、完整多标签配置窗口、多 ROI 类型（聊天/地图信息）、玩家 ID 翻译开关 + F7、磁盘持久化缓存、流式增量上屏、多账号、用量/成本统计。

## 9. 交付物清单

Core：`ITranslationProvider`、`TranslationService`、`TranslationCache`、`Glossary`、`ITokenSource`、`TranslationSettings`、`OpenAiCompatibleChatClient`、`CodexResponsesClient`、`DashScopeProvider`、`ChatGptSubscriptionProvider` + 对应单测。
App：`SecretStore`、`OpenAiOAuth`、阿里云 Key 输入框、托盘菜单扩展、`Phase1Probe` 接翻译、DI 注册。
文档回写（收尾）：`ROADMAP.md` Phase 2/3、`CLAUDE.md`（LLM 提供方改 OpenAI 订阅 + 阿里云）、`TECH_DESIGN.md`。
