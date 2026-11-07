# 本地验证记录

验证日期：2026-11-04（Asia/Hong_Kong）。环境：Windows 10.0.26200 x64，.NET SDK 10.0.103。项目运行时与主要 Microsoft 包为 10.0.14。

## 已通过

| 验证 | 命令/结果 |
|---|---|
| 首次完整构建 | `dotnet build GreatMaoNet.slnx`：零警告、零错误 |
| 自动化测试 | `dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r win-x64 -p:SelfContained=true --logger "trx;LogFileName=verification.trx"`：24 通过、0 失败、0 跳过 |
| Linux 目标编译 | 分别对 Hive/Bee 执行 `dotnet build <项目> -c Release -r linux-x64 -p:SelfContained=true`：零警告、零错误；只验证编译，没有在 Linux 执行 |
| 依赖审计 | `dotnet list GreatMaoNet.slnx package --vulnerable --include-transitive`：当前 NuGet 源未报告已知漏洞 |
| Windows 自包含发布 | 两端以 `-c Release -r win-x64 -p:PublishAot=false --self-contained true` 发布成功，目录 `artifacts/smoke/hive` 和 `artifacts/smoke/bee` |
| 真实进程联调 | `scripts/smoke.ps1`：实际 Windows 主出口网卡采集、实时接口、历史接口、蜂巢异常退出重连、小蜜蜂异常退出恢复均通过 |

自动化测试启用了 `JsonSerializerIsReflectionEnabledByDefault=false`，避免只在允许反射的测试进程中掩盖 JSON 序列化问题。

覆盖范围：首次采样基线、单调时钟速率、同次启动补计、计数归零、系统重启、网卡变化/消失、时钟跳变、峰值合并、Linux 路由解析、本地事务与过期队列、重复补传、整批回滚、历史缺口、分级保留、数据代次不一致、查询鉴权、404/503/504、实时请求响应、100 节点并发写入，以及 100 条真实 WebSocket 连接每隔 5 秒上报并重复发送，共 4 轮。

## 已知长度下载与短时资源测量

最终联调命令：

```powershell
./scripts/smoke.ps1 `
  -HivePath artifacts/smoke/hive/GreatMaoNetHive.exe `
  -BeePath artifacts/smoke/bee/GreatMaoNetBee.exe `
  -TransferUrl 'https://api.nuget.org/v3-flatcontainer/microsoft.data.sqlite.core/10.0.14/microsoft.data.sqlite.core.10.0.14.nupkg'
```

2026-11-04 17:25 HKT 完成，结果保存于 `artifacts/verification/smoke/result.json`。

- 下载响应体：208,438 字节。
- 对应下载操作前后，操作系统选定网卡接收计数增量：367,076 字节。
- 覆盖该下载的持久化监控累计增量：476,379 字节。
- 二者均包含了已知长度的下载；协议开销、其他应用流量及采样窗口不同，使增量大于响应体，不能把差值视为统计误差。
- 预热 15 秒后观测 30 秒，小蜜蜂平均 CPU 约为**一个逻辑核的 1.822%**，工作集约 **60.52 MiB**。这是普通自包含 **JIT** 程序的本机短时测量；同时存在其他开发工作负载，不代表 Native AOT、所有网卡驱动或生产长期性能。

## 尚未完成的环境验证

- Windows Native AOT 发布已分别尝试两端，均在本机工具链检测阶段失败：`Platform linker not found`。本机 Visual Studio 没有所需的 Visual C++ 桌面编译工具链；没有生成或声称验证 Native AOT 可执行文件，也没有关闭 AOT/裁剪警告绕过检查。
- 当前主机没有可用的 Linux/WSL 运行环境，因此未完成 Linux 实机采集、Linux AOT 发布与运行。
- `.github/workflows/verify.yml` 已配置 Windows/Linux 的自包含测试、Native AOT 发布及原生进程联调，但尚未在远端执行。
- Windows Service/systemd 安装文件及说明已经提供，未在本机注册生产服务、开放生产端口或部署到外部服务器。

普通自包含包仅用于已完成的功能联调。正式部署 Native AOT 时，应先补齐对应系统工具链，执行 `scripts/publish.ps1` 或 README 中的 Linux 发布命令，再对生成的原生包运行同一个联调脚本。

## Release 与自动更新验证（2026-11-04，后续验证）

本轮环境为 Windows 10.0.22631 x64、.NET SDK 10.0.103，已可执行 Native AOT 发布；上文关于 Windows 缺少链接器的记录属于此前环境。

- 自动化测试：`dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r win-x64 -p:SelfContained=true --logger "trx;LogFileName=release-update.trx"`，**49 通过、0 失败、0 跳过**。
- 新增更新测试覆盖数字版本比较、稳定版限制、平台资源选择、无 Release、限流、缺失资源、下载取消/截断/摘要错误、缓存重新校验、路径穿越、符号链接、重复压缩条目、配置/数据库保留和部分替换失败后的文件回退。
- `scripts/publish.ps1 -Runtime win-x64 -Version 0.1.0` 成功发布 Hive、Bee 和独立更新器的 Windows Native AOT 二进制，构建没有警告或错误；两端 `--version` 输出 `0.1.0`。
- `scripts/update-smoke.ps1 -Runtime win-x64`：使用原生更新器等待旧 Hive 退出、替换安装目录、启动 Hive 并检查 HTTP 健康；配置与数据库保留，更新完成记录已生成。结果位于 `artifacts/verification/update-*/result.json`。
- 原生两端联调：`scripts/smoke.ps1 -HivePath artifacts/publish/GreatMaoNetHive/win-x64/GreatMaoNetHive.exe -BeePath artifacts/publish/GreatMaoNetBee/win-x64/GreatMaoNetBee.exe -OutputDirectory artifacts/verification/release-native -WarmupSeconds 2 -ObservationSeconds 5` 通过，包括实际采集、查询、蜂巢断线重连、小蜜蜂异常退出恢复。这是功能检查，短时资源数据不作性能结论。
- 原生 Bee 的 `--check-update` 已访问真实 GitHub API，并在仓库没有 Release 时正常返回无新稳定版。
- GitHub 工作流经 actionlint v1.7.7 检查通过；PowerShell 脚本语法检查通过。

Windows Service 控制管理器重启和 Linux systemd 的 `ExecStopPost` 集成尚未在本机实际安装服务验证。Linux 原生发布与运行、GitHub Release 创建需由远端 CI 执行。当前更新回退只覆盖文件替换失败和明确的启动失败，不包含数据库迁移或长期运行健康回滚。未在本机注册生产服务或创建线上 Release。

## 蜂巢内置 Swagger 验证（2026-11-04）

- 完整自包含测试 **57 通过、0 失败、0 跳过**；其中 8 项 Swagger 测试覆盖生产环境页面及内嵌资源、OpenAPI 响应模型、历史查询参数、Bearer 安全方案、无效文档版本和原有 HTTPS/查询鉴权要求。调整页面分组后，8 项 Swagger 测试再次通过。
- 蜂巢 Windows Native AOT 发布成功，没有 AOT/裁剪警告。使用实际原生程序运行 `scripts/smoke.ps1`，页面、OpenAPI JSON、CSS/JavaScript、真实节点采集和 API 查询检查通过，结果在 `artifacts/verification/swagger-native/result.json`。本次沿用已有恢复测试结果，短时联调使用 `-SkipRestart`。
- 使用独立无头 Edge 浏览器访问原生蜂巢 `/swagger`：6 个接口正常显示；通过授权弹窗输入临时查询密钥，执行 `/api/v1/bees` 获得 HTTP 200，实际请求正确携带 Bearer 头；外部网络请求 **0**，浏览器脚本错误 **0**。检查结果和截图在 `artifacts/verification/swagger-browser/`，临时进程已停止。
- UI 使用官方 `swagger-ui-dist` 5.34.1 的内嵌资源，下载时验证 npm SHA-512，保留上游许可证。Linux AOT 与页面资源检查已包含在现有 CI 原生联调脚本中，尚未在本机执行 Linux 运行验证。

## 周期额度与在线历史验证（2026-11-07）

- 自包含完整测试 **81 通过、0 失败、0 跳过**，结果 `tests/GreatMaoNet.Tests/TestResults/quotas-online.trx`。新增覆盖固定时区的日/周/月边界、月末与闰年、非法配置、重放去重、跨月归属、清理与重启后统计、配置变更、未知/零用量，以及缺失数据不冒充完整用量。
- 在线历史测试覆盖窗口裁剪、相邻区间合并、旧会话清理不干扰新会话、45 秒静默、正常停止、异常退出恢复、蜂巢停机标记未知、保留期清理和延长保留期后不误判历史。模拟旧版数据库升级，原节点数据保留。
- HTTP/WebSocket 集成测试验证两个新接口的查询密钥鉴权、未连接节点、非法窗口/未知节点、真实连接断开持久化，以及超额后继续成功上报。Swagger 验证覆盖新增接口和查询参数，当前共 8 个 HTTP 接口。
- 蜂巢 Windows Native AOT 发布成功，无警告/错误。`scripts/smoke.ps1` 新增两个接口的原生检查：环境变量绑定月度额度及 UTC+8 时区、超额后继续采集上报、持久化在线历史，并在杀死蜂巢后重启验证停机期间为 `unknown`、小蜜蜂断开重连有独立区间。输出 `artifacts/verification/quotas-online-native/result.json`。
- 本轮未在 Linux 实机运行；同一联调脚本会由 Windows/Linux CI 调用。尚未推送本轮变更或发布新 Release。在线历史在本版本启用后才开始记录，不补造升级前的在线情况；周期用量只包含可归属时间的已持久化采样，不保证补全停机期间缺失的字节。

## 小蜜蜂地区与 IP 验证（2026-11-07）

- 自包含完整测试 **97 通过、0 失败、0 跳过**，结果 `tests/GreatMaoNet.Tests/TestResults/node-metadata.trx`。新增覆盖可选配置、首尾空白处理、IPv4/IPv6 规范化、非法 IP 与超长地区拒绝、上报后重启持久化、修改及清空字段且保留流量累计、缺少字段的旧版握手与旧数据库读取。
- HTTP/WebSocket 集成验证在线与离线时节点列表、详情、额度查询均返回地区和 IP；其他未设置的节点返回 null。Swagger 验证三个相关响应模型包含 `region` 与 `ip`。
- `scripts/publish.ps1 -Runtime win-x64` 完成蜂巢、小蜜蜂及更新器的 Windows Native AOT 发布，无警告或错误。原生两端联调通过，结果 `artifacts/verification/node-metadata-native/result.json`：环境变量设置地区及 IPv6、真实网卡采集、蜂巢重启保留信息、小蜜蜂改为另一地区及 IPv4 后重启更新，三个查询接口均返回新值，流量累计与在线历史检查继续通过。
- 原生联调预热 2 秒、观测 5 秒，仅作功能检查。本轮未在 Linux 实机运行；新增检查已加入 Windows/Linux CI 共用的联调脚本。
- 提交前整合远端 Linux 更新器进程标识修复后，再次运行完整自包含测试：**105 通过、0 失败、0 跳过**，结果 `tests/GreatMaoNet.Tests/TestResults/node-metadata-rebased.trx`。上述原生发布与联调在整合该独立修复前完成。
