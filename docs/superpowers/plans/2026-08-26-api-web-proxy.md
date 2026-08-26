# API 与 Web 同源代理配置实施计划

> **For agentic workers:** When available, use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task; if unavailable, follow the project's single-Task execution protocol and stop for review after this Task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 WMS Web 补齐到 API 的可配置同源反向代理，使浏览器始终使用相对 `/api` 和 `/health` 路径，开发、测试和正式部署不再依赖前端硬编码 API 端口或额外 CORS 配置。

**Architecture:** `Warehouse.Wms.Web` 继续负责静态页面和前端路由，并使用 YARP 将 `/api/{**catch-all}` 与 `/health/{**catch-all}` 转发到配置的 API 上游地址。API 上游地址只存在于 Web 的配置/环境变量中，代理不代理静态文件、不修改请求路径、不改变 API 认证和业务授权；API 仍是唯一业务入口。

**Tech Stack:** ASP.NET Core 8、YARP Reverse Proxy、WebApplicationFactory、xUnit、Docker 开发环境。

---

### Task 9.12：补齐 API 与 Web 的同源代理配置

**Files:**
- Modify: `src/Warehouse.Wms.Web/Warehouse.Wms.Web.csproj`
- Modify: `src/Warehouse.Wms.Web/Program.cs`
- Create: `src/Warehouse.Wms.Web/appsettings.json`
- Create: `src/Warehouse.Wms.Web/appsettings.Development.json`
- Modify: `src/Warehouse.Wms.Web/wwwroot/app.js`（只确认并保持相对 `/api` 调用，不得写入固定端口）
- Modify: `tests/Warehouse.Wms.IntegrationTests/Warehouse.Wms.IntegrationTests.csproj`
- Modify: `tests/Warehouse.Wms.IntegrationTests/Web/ManagementWebTests.cs`
- Create: `tests/Warehouse.Wms.IntegrationTests/Web/FrontendProxyTests.cs`
- Modify: `docs/development.md`
- Modify: `README.md`
- Modify: `PROJECT_DESIGN.md`
- Modify: `docs/superpowers/plans/2026-08-24-independent-wms-implementation.md`

- [ ] **Step 1: Write the failing proxy tests**

  在 `FrontendProxyTests` 中加入以下测试：

  - `Web_proxy_forwards_get_path_and_query_to_configured_api`：启动一个本地临时 ASP.NET Core 上游，记录请求路径和查询串；通过 Web 的 `/api/probe?warehouse=DEV` 请求，断言上游收到完全相同的 `/api/probe?warehouse=DEV`，响应状态和 JSON 原样返回。
  - `Web_proxy_forwards_post_body_and_content_type`：通过 Web 的 POST 请求发送 JSON，断言上游收到相同请求体和 `Content-Type`。
  - `Web_proxy_does_not_proxy_static_files`：访问 `/index.html` 返回 Web 自己的静态页面，不经过 API 上游。
  - `Web_proxy_rejects_missing_or_invalid_upstream_configuration`：上游地址为空、相对路径或非 HTTP(S) URI 时，Web 启动失败并给出配置错误；不得静默回退到假 API。
  - `Frontend_uses_relative_api_paths`：读取 `wwwroot/app.js`，断言所有 API 请求以 `/api/` 开头，且不包含 `localhost:5054`、`localhost:5055` 或其他固定端口。

  临时上游使用 Kestrel 绑定 `127.0.0.1:0`，测试结束时调用 `StopAsync` 和 `DisposeAsync`；不得依赖外部网络或真实 API 进程。

- [ ] **Step 2: Run the focused tests to capture the baseline failure**

  Run:

  ```powershell
  dotnet test tests/Warehouse.Wms.IntegrationTests/Warehouse.Wms.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~FrontendProxyTests
  ```

  Expected: tests fail because the Web project has no reverse-proxy route and no configurable upstream yet.

- [ ] **Step 3: Add configurable YARP proxy registration**

  Add the pinned `Yarp.ReverseProxy` 2.x package to `Warehouse.Wms.Web.csproj` and register a route/cluster in `Program.cs`:

  - route `/api/{**catch-all}` to the configured API upstream;
  - route `/health/{**catch-all}` to the same upstream;
  - leave static files and the SPA fallback before/alongside the proxy without catching `/` or arbitrary static paths;
  - preserve HTTP method, path, query, request body and response status/content type;
  - validate the upstream as an absolute `http` or `https` URI at startup;
  - read `ApiProxy:UpstreamBaseUrl`, allowing environment override `ApiProxy__UpstreamBaseUrl`;
  - default Development to `http://127.0.0.1:5054/`; production must require an explicit configured upstream.

  The proxy must not add a second authentication layer, bypass API authorization, or expose a route that writes directly to the database or PLC.

- [ ] **Step 4: Keep front-end calls same-origin and document the topology**

  Keep `app.js` calls such as `fetch('/api/warehouse/points?...')` and `fetch('/api/reports/summary?...')`; remove any fixed API port if found. Add development configuration and commands to `docs/development.md` and `README.md`:

  ```powershell
  $env:ApiProxy__UpstreamBaseUrl = 'http://127.0.0.1:5054/'
  dotnet run --project src/Warehouse.Wms.Api --launch-profile http
  dotnet run --project src/Warehouse.Wms.Web --urls http://127.0.0.1:5055
  Invoke-WebRequest http://127.0.0.1:5055/health/live
  Invoke-WebRequest http://127.0.0.1:5055/api/reports/health
  ```

  Document that the browser only uses `5055` in development; the Web process proxies API requests to `5054`, while production should expose one same-origin host behind the deployment reverse proxy.

- [ ] **Step 5: Run focused, full and runtime verification**

  Run the focused proxy tests, then:

  ```powershell
  dotnet restore Warehouse.Wms.sln
  dotnet build Warehouse.Wms.sln --no-restore -m:1 -nodeReuse:false
  dotnet test Warehouse.Wms.sln --no-build --no-restore
  pwsh -NoProfile -File scripts/verify.ps1
  ```

  Start API on `5054` and Web on `5055`; expected results are HTTP 200 for Web `/`, Web `/health/live` and Web `/api/reports/health`. Also assert direct API health remains HTTP 200 and `warehouse/` has no tracked diff.

- [ ] **Step 6: Record acceptance and commit**

  Update the main implementation plan with exact test counts, proxy smoke-test responses, configuration validation result, build/test/quality-gate results, and `HUMAN_PENDING`/`FIELD_PENDING` notes. Commit the implementation as `feat(web): add configurable api reverse proxy` and push the development branch; a network push failure is recorded as `PUSH_PENDING` without changing local acceptance.

## Security and deployment constraints

- The proxy allowlist is limited to `/api/` and `/health/`; no catch-all forwarder is permitted.
- Upstream addresses must be environment-specific and must not contain credentials.
- The default development upstream is loopback only; no production PLC, ERP or database address belongs in source control.
- CORS is not the primary integration mechanism for the bundled Web; same-origin proxying is the default browser path.
