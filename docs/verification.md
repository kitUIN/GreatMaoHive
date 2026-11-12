# 本地验证记录

## Linux CI 无效重连关闭握手修复（2026-11-12）

- Actions `43184188776` 的 Linux 测试为 **181 通过、1 失败**，失败于 `UpgradeApiTests.RealBeeNotifiesBeforeShutdownAndApisShowUpgradeUntilReconnect` 等待无效重连错误消息；异常为 `Connection reset by peer`。上一次 `43182810031` 同一测试同样失败，Windows 测试及 Native AOT 发布均成功。蜂巢原先发送错误消息后立即 `Abort`，Linux 上可能在客户端读到错误帧前重置 TCP。
- 拒绝协议消息时先将会话标为不可用，再发送错误并执行 `PolicyViolation` 关闭握手，发送过程复用原发送锁；最多等待 2 秒，超时仍执行清理。升级测试继续严格验证错误消息，并增加关闭帧及回应；新增握手前/握手后协议拒绝、客户端不回应关闭握手时仍按期清理的回归测试。
- `dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r win-x64 -p:SelfContained=true --logger "trx;LogFileName=websocket-close-full.trx"`：**185 通过、0 失败、0 跳过**。本机未运行 Linux；Linux 测试及 Native AOT 发布以对应修复提交的 Actions 结果为准。未发布 Release 或部署。

## 蜂巢批量通知小蜜蜂更新（2026-11-12）

- `GreatMaoNetHive --update-bee` 不指定 ID 时通知批量开始时全部在线节点；指定 ID 保持单节点行为。固定本次会话快照，离线节点逐项列为跳过，中途上线或重连不会补发。按 ID 逐个等待确认，单节点拒绝、旧版不支持或超时不阻止后续节点。
- 本机管道按节点分条返回结果并汇总接受/失败/跳过数量，单条仍限制 4 KiB，读写超时按每次交互计算。命令退出码 `0` 要求至少一只接受且无失败；存在失败、没有在线节点或中断返回 `1`。显式空 ID、重复更新参数及未知参数返回 `2`，不会误触发全量操作；旧蜂巢返回明确的升级提示。HTTP 更新接口仍保持指定节点方式。
- `dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r win-x64 -p:SelfContained=true --logger "trx;LogFileName=hive-batch-full.trx"`：**182 通过、0 失败、0 跳过**。新增 6 项批量联调，并更新参数回归：独立命令进程通知多个 WebSocket 节点、101 个配置节点的分条报告、拒绝/超时/旧版后继续、超过旧 10 秒总时限、全离线、中途上线跳过及旧蜂巢兼容提示。既有单节点命令和 HTTP 更新测试继续通过。
- `dotnet build src/GreatMaoNetHive/GreatMaoNetHive.csproj -c Release -r linux-x64 -p:SelfContained=true`：成功，**0 警告、0 错误**。未执行 Linux systemd 实机批量升级、Native AOT 发布或线上更新；未发布 Release、未部署。

## 蜂巢本机命令触发小蜜蜂更新（2026-11-12）

- 新增 `GreatMaoNetHive --update-bee <节点ID>`（同时支持 `--update-bee=<节点ID>`）。一次性进程在创建 Host 前处理命令，使用同账号本机管道通知运行中的蜂巢，不启动第二个服务、不读取/修改监控数据库，也不依赖 HTTP 地址或管理员密钥。HTTP 与本机入口复用同一更新派发逻辑。
- Linux 默认使用安装目录下的 `.hive-control.sock`，兼容现有 systemd `ReadWritePaths`；Windows 使用安装目录散列区分管道。服务端和客户端均设置 `PipeOptions.CurrentUserOnly`，命令必须使用服务运行账号。管道请求有长度上限及超时，异常客户端不会中断监控；读取回复后确认，避免 Windows 断开管道丢弃未读结果。
- `dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r win-x64 -p:SelfContained=true --logger "trx;LogFileName=hive-cli-full.trx"`：**173 通过、0 失败、0 跳过**。本轮新增 20 项，包含独立 Hive 命令进程 → 运行中的 Hive → 真实 Bee 服务 → 更新队列、两种命令语法、无管理员密钥、不同当前目录、既有服务继续响应、离线/不存在节点、退出码、参数错误、服务未运行、异常长度以及客户端提前断开后的通道恢复。
- `dotnet build src/GreatMaoNetHive/GreatMaoNetHive.csproj -c Release -r linux-x64 -p:SelfContained=true`：成功，**0 警告、0 错误**。仅验证 Linux 目标编译；本机无可用 Linux/WSL 运行环境，未执行 Linux 管道权限隔离或 systemd 实机升级，也未进行 Native AOT 发布、线上更新、提交或部署。

## 蜂巢远程触发小蜜蜂更新（2026-11-12）

- 新增 `POST /api/v1/bees/{id}/update`，使用独立且可选的 `Hive:AdminApiKey`。通过协商的 `remote-update-v1` 能力发送指令并等待节点确认，`202` 仅表示接受。离线/旧版、策略拒绝、正在更新、超时及断线均返回明确结果；不向离线节点补发。
- 小蜜蜂将指令交给现有更新工作服务，自动与远程触发互斥。`Enabled=false` 时仍可远程触发；`AllowRemoteUpdates=false`、`AutoDownload=false` 或 `AutoApply=false` 拒绝远程安装。继续复用包校验、失败标记、文件锁、升级保护及更新器重启流程。
- `dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r win-x64 -p:SelfContained=true --logger "trx;LogFileName=remote-update-full.trx"`：**153 通过、0 失败、0 跳过**。新增 21 项覆盖管理员鉴权与密钥隔离、未知/离线/旧版节点、确认关联、并发去重、节点拒绝、超时/延迟响应、断线、真实 Bee 服务接收并入队、入队后升级通知仍能完成，以及更新队列策略和互斥。Swagger 回归验证管理员认证方案及响应结构。
- 本轮未向线上节点发送更新指令，未执行真实 Release 下载替换、Linux systemd 重启或 Native AOT 发布；未提交、发布或部署。完整功能要求两端升级，旧版 Bee 的第一次升级仍通过原自动更新或重新部署完成。

## 升级期间免掉线统计（2026-11-12）

- 自动更新在停止进程前执行 `IUpgradePreparation`：Bee 向支持该能力的 Hive 请求持久化升级窗口并等待确认；Hive 为当前有效会话登记窗口。新表 `upgrade_windows` 跨 Hive 重启保存，数据库版本升至 3。重连握手成功后结束窗口；取消准备撤销窗口，超时恢复正常判断。此前通信压缩改动保留。
- 当前节点 API 增加 `upgrading` 和 `status`，在线历史增加 `upgrading` 区间及 `upgradingSeconds`，升级时间不计入 `offlineSeconds`；真实连接布尔值 `online` 保留原语义。控制台、周期日志及实时查询分别识别升级状态。
- `dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r win-x64 -p:SelfContained=true --no-restore --logger "trx;LogFileName=upgrade-full.trx"`：**132 通过、0 失败、0 跳过**。新增 9 项覆盖 Hive 升级停机/重连、Bee 升级期间 Hive 重启、超时、取消及再次升级、重叠窗口、保留期、过期/其他会话拒绝、重复通知不延长保护、升级前已离线节点、真实 Bee 服务通知/确认/停止/替换重连、无效重连不清除保护、所有相关 API 与控制台状态。既有数据库升级测试也验证缺表自动迁移至版本 3。
- 两端以 `dotnet publish src/<应用>/<应用>.csproj -c Release -r win-x64 -p:PublishAot=false --self-contained true -o artifacts/upgrade-smoke/<hive或bee>` 构建成功；`./scripts/smoke.ps1 -HivePath artifacts/upgrade-smoke/hive/GreatMaoNetHive.exe -BeePath artifacts/upgrade-smoke/bee/GreatMaoNetBee.exe -OutputDirectory artifacts/verification/upgrades -WarmupSeconds 2 -ObservationSeconds 5` 通过，包含真实采集/API/重启恢复。该脚本的强制退出仍按异常中断处理，不伪装成升级；升级保护由上述专门测试验证。
- 本轮未执行线上 Release 下载替换、Linux 实机或 Native AOT 验证；上轮已确认本机缺少完整 C++ 链接工具链。尚未提交或部署。首次从旧版本升级及手动替换/重启不能自动登记升级保护，完整行为需要两端具备升级通知能力。

## Bee/Hive 通信压缩（2026-11-12）

- 使用独立的 `WireJson` 源生成上下文省略通信中的 null 字段，HTTP API 和数据库序列化仍使用原上下文。两端协商 `permessage-deflate`，仅压缩 `batch` 和 `current-response`，连接内复用压缩字典。
- `dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r win-x64 -p:SelfContained=true --no-restore --logger "trx;LogFileName=compression-full.trx"`：**123 通过、0 失败、0 跳过**。新增 9 项覆盖零值与旧 JSON 兼容、压缩/普通帧混用、解压后 512 KiB 限制、旧服务端协商回退、旧客户端接入新 Hive、100 条积压数据补传与去重、心跳和实时查询。
- `ProtocolCompressionTests.RepresentativeHourUsesLessThanHalfTheOriginalWireBytes` 通过 .NET 实际 WebSocket 编码器测量代表性单网卡的一小时消息：720 组上报/确认、240 组 ping/pong，数据值随样本变化，含首次建立压缩字典的开销。结果如下（十进制 MB，不是生产抓包）：

  | 格式 | 一小时双向 WebSocket 帧 | 按相同负载折算每天 |
  |---|---:|---:|
  | 原格式 | 665,526 B | 15.973 MB |
  | 仅省略空字段 | 490,326 B | 11.768 MB |
  | 省略空字段并启用压缩 | 112,913 B | 2.710 MB |

  此样本帧字节减少 **83.0%**。数字不包含 TLS/TCP/IP、底层保活、握手、重传及额外实时查询。在稳定单网卡连接、不持续查询实时速率的条件下，计入这些常规协议开销可粗估为 **8～10 MB/天、240～300 MB/30 天**；链路和实际字段值会影响结果，两端都升级且协商成功后才有完整压缩效果。
- Hive/Bee 分别以 `dotnet publish src/<应用>/<应用>.csproj -c Release -r win-x64 -p:PublishAot=false --self-contained true -o artifacts/compression-smoke/<hive或bee>` 构建成功。`./scripts/smoke.ps1 -HivePath artifacts/compression-smoke/hive/GreatMaoNetHive.exe -BeePath artifacts/compression-smoke/bee/GreatMaoNetBee.exe -OutputDirectory artifacts/verification/compression -WarmupSeconds 2 -ObservationSeconds 5` 通过：真实采集、实时/历史 API、Hive 重启重连、Bee 重启恢复及元数据/额度/在线历史检查。
- `./scripts/publish.ps1 -Runtime win-x64` 在更新器构建阶段报 `Platform linker not found`；本机缺少完整 C++ 链接工具链，未完成本轮 Native AOT 验证。本轮未在 Linux 实机、生产 WSS 链路或反向代理后验证，未部署或发布。

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
