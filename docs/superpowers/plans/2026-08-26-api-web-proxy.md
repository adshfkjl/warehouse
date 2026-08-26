# API 与 Web 同源代理配置实施计划

> **For agentic workers:** When available, use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans`; if unavailable, follow the project's single-Task execution protocol. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 WMS Web 补齐到 API 的可配置同源反向代理，使浏览器始终使用相对路径，并在开发、测试和正式部署中明确区分 Web 自身健康状态与 API 上游状态。

**Architecture:** `Warehouse.Wms.Web` 继续负责静态页面和前端路由，使用固定版本的 YARP 仅转发白名单路径：`/api/{**catch-all}` 和 `/health/api/{**catch-all}`。Web 自身暴露 `/health/web/live`，API 上游仍由配置指定；代理不代理静态文件、不改变业务认证授权、不接受浏览器动态目标地址。请求失败由代理返回明确的 502/504，而不是交给 SPA fallback。

**Tech Stack:** ASP.NET Core 8、YARP Reverse Proxy `2.2.0`、WebApplicationFactory、Kestrel 动态端口、xUnit、Playwright 或等价浏览器自动化、Docker 开发环境。

---

## 共享前置：Task 9.11A 完成旧系统完整性基线

执行本专项计划前，Task 9.11A 必须已经创建并验收 [`docs/legacy-source-manifest.sha256`](../../legacy-source-manifest.sha256) 和 `scripts/verify-legacy-source.ps1`。本计划不重新生成或覆盖清单；每个代理 Task、阶段门禁和最终验收都执行校验脚本。哈希不一致时状态为 `BLOCKED`，不得用 `git diff -- warehouse` 代替清单校验。清单只能证明 2026-08-27 建立基线之后的变化，首次来源核对需另有可信备份或现场原始副本证据。

---

### Task 9.12A：代理路由、配置与安全边界

**Files:**
- Modify: `src/Warehouse.Wms.Web/Warehouse.Wms.Web.csproj`
- Modify: `src/Warehouse.Wms.Web/Program.cs`
- Create: `src/Warehouse.Wms.Web/appsettings.json`
- Create: `src/Warehouse.Wms.Web/appsettings.Development.json`
- Modify: `src/Warehouse.Wms.Web/wwwroot/app.js`
- Modify: `tests/Warehouse.Wms.IntegrationTests/Warehouse.Wms.IntegrationTests.csproj`
- Create: `tests/Warehouse.Wms.IntegrationTests/Web/FrontendProxyRoutingTests.cs`
- Modify: `docs/development.md`
- Modify: `README.md`
- Modify: `PROJECT_DESIGN.md`
- Modify: `docs/superpowers/plans/2026-08-24-independent-wms-implementation.md`

- [x] **Step 1: Write failing route and configuration tests.** 覆盖 YARP 依赖版本、`ApiProxy:UpstreamBaseUrl` 绝对 `http/https` 校验、生产环境拒绝缺失配置和开发默认值、非 HTTP/HTTPS 地址失败；生产 loopback 只有显式 `AllowLoopbackUpstream=true` 时允许；浏览器不能通过请求参数改变上游、`/api/*` 优先于静态文件和 SPA fallback。
- [x] **Step 2: Add the minimal YARP route table.** 注册固定版本 `Yarp.ReverseProxy` `2.2.0`；仅配置 `/api/{**catch-all}` 与 `/health/api/{**catch-all}`，把后者转换为 API 的 `/health/{**catch-all}`。Web 本地映射 `/health/web/live`，不得配置泛化的 `/health/{**catch-all}`。
- [x] **Step 3: Enforce forwarding and retry rules.** 保留 HTTP 方法、路径、查询参数、请求体、`Authorization`、`Content-Type`、`Accept`、W3C `traceparent` 和 `X-Correlation-ID`；将 `HttpContext.RequestAborted` 传递到上游；明确禁用 POST/PUT/PATCH/DELETE 自动重试，GET/HEAD 也不得隐式改变业务语义。
- [x] **Step 4: Add upstream failure handling.** 连接拒绝或上游不可用返回 `502`，超时返回 `504`，响应为 `application/problem+json`；失败响应不得继续进入静态文件或 SPA fallback，也不得泄漏上游凭据、连接字符串或内部堆栈。
- [x] **Step 5: Keep front-end calls same-origin.** `wwwroot/app.js` 只允许 `/api/...` 和 `/health/...` 相对调用；删除固定 `localhost:5054`、`localhost:5055` 或其他端口。浏览器请求不能携带代理目标、host 或 cluster 参数。
- [x] **Step 6: Run focused verification and commit.** 执行代理路由测试、`pwsh -NoProfile -File scripts/verify-legacy-source.ps1` 和 `git diff --check`；提交 `feat(web): add secure configurable api proxy routes`。此 Task 只验证路由与安全边界，不进行双进程端口验收。

**验收：** `AGENT_VERIFIED`；Web 自身 `/health/web/live` 与 API 代理 `/health/api/live` 可区分；白名单外路径由 Web 处理；缺失/非法配置启动失败；API 错误明确返回 502/504；旧系统清单校验通过。

**执行记录（2026-08-27）：** 固定 `Yarp.ReverseProxy` 2.2.0，实施受信任的 `ApiProxy:UpstreamBaseUrl` 校验、两条白名单路由和分层健康检查；路由/配置测试 19/19 通过，生产非法地址、loopback 开关、SPA 边界和问题详情响应均已验证。旧源码 SHA-256 校验 133/133 通过，`git diff --check` 通过；提交 `ca97dbd`、`3d0f9ed`、`e5648e9`、`f276294`。状态：`AGENT_VERIFIED`；生产域名/TLS/认证网关为 `HUMAN_PENDING`，真实现场网络为 `FIELD_PENDING`。

### Task 9.12B：双进程代理集成测试和开发启动验证

**Files:**
- Modify: `tests/Warehouse.Wms.IntegrationTests/Web/ManagementWebTests.cs`
- Create: `tests/Warehouse.Wms.IntegrationTests/Web/FrontendProxyIntegrationTests.cs`
- Modify: `docs/development.md`
- Modify: `README.md`
- Modify: `PROJECT_DESIGN.md`
- Modify: `docs/superpowers/plans/2026-08-24-independent-wms-implementation.md`

- [x] **Step 1: Add dynamic-port upstream fixtures.** 用 Kestrel `127.0.0.1:0` 启动临时 API，上游测试结束时调用 `StopAsync` 和 `DisposeAsync`；Web 测试也使用动态端口。`5054`/`5055` 只保留给开发启动冒烟验证，测试不得绑定固定端口。
- [x] **Step 2: Test request/response fidelity.** 覆盖 GET 路径与查询串、JSON POST 请求体与内容类型、`Authorization`/`Accept`/追踪 ID、客户端取消信号和响应状态/`Content-Type` 原样传递。
- [x] **Step 3: Test warehouse file flows.** 通过 Web 端口上传 `multipart/form-data` Excel，断言上游收到边界、文件名和内容；通过 Web 下载 CSV/XLSX 错误报告，断言下载状态、`Content-Disposition`、内容类型和字节内容不变。
- [x] **Step 4: Test failure and routing boundaries.** 上游停止时断言 Web 返回 502/504 的问题详情 JSON；访问 `/index.html` 仍返回 Web 静态文件；访问不存在的 `/api/...` 不得返回 `text/html` 首页；`/health/web/live` 不依赖 API，`/health/api/live` 反映 API 状态。
- [x] **Step 5: Run two-process development smoke test.** 仅在本地开发配置下启动 API `5054` 和 Web `5055`，执行 `/health/web/live`、`/health/api/live`、`/api/reports/health` 和一次真实相对路径请求；记录两个进程日志和退出码。
- [x] **Step 6: Run full quality gates and commit.** 执行 `dotnet restore`、`dotnet build`、全量 `dotnet test`、`scripts/verify.ps1` 和旧系统哈希校验；提交 `test(web): verify dynamic-port api proxy flows`，并记录 `AGENT_VERIFIED`、`HUMAN_PENDING` 与 `FIELD_PENDING`。

**验收：** `AGENT_VERIFIED`；动态端口双进程测试覆盖普通 API、multipart 上传、错误报告下载、取消、认证头、追踪 ID、502/504 和 SPA 边界；开发冒烟通过。生产域名、TLS、认证网关和网络策略继续由 `HUMAN_PENDING`/`FIELD_PENDING` 确认。

**执行记录（2026-08-27）：** 临时 API/Web 都使用 Kestrel `127.0.0.1:0`，Web 由当前测试构建配置的 `dotnet run --no-build -c <Debug|Release>` 启动，启动超时或提前退出均等待并释放进程句柄；普通 API、JSON、认证/追踪头、取消、结构化解析的 multipart Excel、错误报告下载、API 422、502/504、健康分层与静态/SPA 边界均由真实 HTTP 覆盖。GET/POST/PUT/PATCH/DELETE 在上游一次性 503 后均观察到单次到达，成功 POST/PUT/PATCH/DELETE 同样各断言单次发送。Debug 定向代理组 17/17、Release 定向代理组 17/17，Release 解决方案构建 0 警告/0 错误。开发 5054/5055 冒烟的四个 Web/API 端点均为 200。完整 restore/build/test、Docker SQL 迁移、`verify.ps1`、旧哈希和 `git diff --check` 均通过；状态：`AGENT_VERIFIED`，生产域名/TLS/认证网关 `HUMAN_PENDING`，真实 PLC/现场网络/账实 `FIELD_PENDING`。

## 配置与部署约束

- `ApiProxy:UpstreamBaseUrl` 只能来自受信任的应用配置或 `ApiProxy__UpstreamBaseUrl` 环境变量，必须是绝对 `http`/`https` URI；生产环境缺少配置或使用开发默认值时失败，生产 loopback 只有显式设置 `AllowLoopbackUpstream=true` 且地址经过部署审核时允许。
- 上游地址不能包含用户名、密码、令牌或查询参数；浏览器请求不参与目标解析，避免形成开放代理。
- 代理不新增认证层，但必须原样转发 API 的认证头并由 API 执行授权。
- Web 的静态文件和 SPA fallback 必须排在白名单代理之后的明确分支中；任何代理异常不得回退为首页 HTML。
- 不连接真实 PLC、ERP、生产数据库或生产 API；所有自动化测试使用临时本地上游。
