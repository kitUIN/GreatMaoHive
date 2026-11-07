# GreatMaoNet 大猫猫网络监控

`GreatMaoNetBee`（大猫猫网络小蜜蜂）采集服务器网卡流量，主动通过 WebSocket 连接 `GreatMaoNetHive`（大猫猫网络蜂巢）。蜂巢保存历史数据并提供 HTTP JSON API，内置 Swagger UI 接口文档与调试页面。目标平台为 Windows/Linux x64，项目默认启用 .NET 10 Native AOT，无监控仪表盘、抓包、主动测速或进程级流量分类。

## 项目与构建

```text
src/GreatMaoNet.Contracts  版本化协议、API DTO、JSON 源生成、WebSocket 封装
src/GreatMaoNet.Updates    Release 查询、校验、下载、更新调度与文件回退
src/GreatMaoNetUpdater     停服后替换程序并重启的独立 Native AOT 更新器
src/GreatMaoNetBee        网卡/路由采集、本地检查点和补传队列、连接管理
src/GreatMaoNetHive       HTTP API、连接管理、单写入队列、SQLite 历史聚合
tests/GreatMaoNet.Tests   采集、存储、HTTP/WebSocket、并发入库测试
scripts/                 发布与真实进程联调脚本
deploy/systemd/           Linux 服务单元与环境配置示例
```

需要 .NET 10 SDK。`global.json` 允许使用更新的 .NET 10 feature band；运行时与主要 Microsoft 包锁定 10.0.14，SQLite 本机依赖使用 SQLitePCLRaw 3.0.5。普通 framework-dependent 程序需对应的 .NET/ASP.NET Core 运行时；正式 Native AOT 包不需安装 .NET。

```powershell
dotnet restore GreatMaoNet.slnx
dotnet build GreatMaoNet.slnx
# 自包含测试不依赖本机是否安装了项目所需的运行时补丁版本。
dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r win-x64 -p:SelfContained=true
./scripts/publish.ps1 -Runtime win-x64
```

Linux 上将 RID 换成 `linux-x64`，或直接逐个执行：

```sh
dotnet test tests/GreatMaoNet.Tests/GreatMaoNet.Tests.csproj -r linux-x64 -p:SelfContained=true
dotnet publish src/GreatMaoNetHive -c Release -r linux-x64 -o artifacts/publish/GreatMaoNetHive/linux-x64
dotnet publish src/GreatMaoNetBee -c Release -r linux-x64 -o artifacts/publish/GreatMaoNetBee/linux-x64
```

Windows AOT 需要 Visual Studio 的 **使用 C++ 的桌面开发**工作负载及 Windows SDK；Ubuntu 需要 `clang`、`zlib1g-dev`。Windows 和 Linux 包在各自系统构建。发布时应复制**整个发布目录**，包括 SQLite 本机库和配置文件，不能只复制主可执行文件。GitHub Actions 的 Windows/Linux 矩阵会运行测试、AOT 发布和原生进程联调。

### GitHub CI

推送、Pull Request 或手动触发 `Verify and publish Native AOT` 工作流后，Windows 与 Linux 分别运行自包含测试、Native AOT 发布和独立进程联调。单个平台失败不会取消另一个平台的验证，同一分支的新提交会取消旧任务。

通过后可在该次 Actions 运行页面下载 `native-packages-win-x64` / `native-packages-linux-x64`，包含两端的完整 ZIP 发布包、独立更新器和每包的 `.sha256` 校验文件。`verification-*` 保存测试结果、联调 JSON 和日志，构建产物默认保留 14 天。两平台还会验证更新器等待原进程退出、替换程序、重新启动和 HTTP 健康检查。

推送 `vMAJOR.MINOR.PATCH` 稳定版本标签后，同一工作流会将标签版本写入二进制；仅当 Windows/Linux 全部测试、AOT 发布和联调通过，才创建 GitHub Release。上传全部四个包和四个校验文件后才公开草稿，避免客户端发现不完整的 Release。已公开的版本不能覆盖；修复应使用新标签。普通分支推送和 PR 只验证，不创建 Release。

```sh
git tag v0.1.0
git push origin v0.1.0
```

本地带版本发布：`./scripts/publish.ps1 -Runtime win-x64 -Version 0.1.0`，随后 `./scripts/package.ps1 -Runtime win-x64`。目前只发布稳定版，不支持预发布标签。首次部署 Linux ZIP 解压后需执行 `chmod +x GreatMaoNetHive GreatMaoNetUpdater`（蜜蜂目录将 Hive 改为 Bee）；后续自动更新会设置执行权限。

### 自动更新

蜂巢和小蜜蜂默认在启动后检查一次，并每 6 小时查询 `kitUIN/GreatMaoHive` 的最新稳定 Release；只更新到更高版本。自动选择当前程序的 Windows/Linux x64 完整包，下载后验证大小、SHA-256、压缩路径和二进制内嵌版本，再正常停止程序。独立更新器替换程序及本机依赖，并重启 Windows Service、systemd 服务或原控制台程序。安装目录中的 `appsettings*.json`、数据库、证书及其他用户文件不会被替换；新版本增加的配置项使用代码默认值。

```json
{
  "Updates": {
    "Enabled": true,
    "Repository": "kitUIN/GreatMaoHive",
    "CheckIntervalMinutes": 360,
    "AutoDownload": true,
    "AutoApply": true,
    "DownloadDirectory": ".updates"
  }
}
```

配置可放在 `appsettings.Local.json`，或通过 `Updates__Enabled` 等环境变量覆盖。`DownloadDirectory` 相对可执行文件目录解析；每个应用必须使用独立更新目录。关闭更新使用 `Enabled=false`；只下载不安装使用 `AutoApply=false`；仅检查使用 `AutoDownload=false, AutoApply=false`。网络、限流、校验或权限检查失败时继续监控，下一周期重试。开发构建没有独立 AOT 更新器时不运行后台更新。GitHub 仓库须公开；Fork 使用者应修改 `Repository`。

以下命令不需要配置节点密钥：

```powershell
./GreatMaoNetBee.exe --version
./GreatMaoNetBee.exe --check-update
./GreatMaoNetBee.exe --download-update
```

蜂巢同样支持以上命令；Linux 去掉 `.exe`。`--download-update` 只下载已校验的完整 ZIP，不停止正在运行的服务。

更新前的文件保存在安装目录 `.update-backup/`；更新记录保存在更新目录 `updater.log`、`last-update.json`。文件替换失败会还原已改动文件；控制台新进程在启动后 3 秒内退出也会回退。Windows 服务启动请求失败时回退并尝试启动旧版；systemd 负责在更新步骤结束后重启。**不提供运行一段时间后的健康检查回滚或数据库迁移回滚**。失败版本记录为 `failed-<版本>.txt`，防止反复停服；排查后删除对应失败标记可重试。残留 `pending.json` 会阻止新更新，应先检查日志和备份再处理。备份和下载包保留供排查，确认升级正常后可清理旧版本，勿删除正在使用的更新目录。

服务账号必须拥有安装目录和更新目录的写权限；这些目录应仅允许管理员和服务账号写入。Windows Service 使用上述默认服务名，并要求服务账号有启动自身服务的权限（默认 LocalSystem 具备）；更新前会预检查权限。更新依赖完整发布包中的 `GreatMaoNetUpdater`，不能只部署主可执行文件。更新过程会短暂中断监控，蜜蜂使用现有检查点与补传机制恢复。

## 配置与首次运行

两端读取自己的 `appsettings.json`，可用 `appsettings.Local.json`、环境变量或命令行覆盖；环境变量以 `__` 表示配置层级。默认密钥为空，程序会拒绝启动，避免使用公共默认凭证。密钥至少 32 个字符；蜂巢的只读 API Key 和每只蜜蜂的 Token 必须各不相同。

可在 PowerShell 7 中生成密钥：

```powershell
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

蜂巢 `appsettings.Local.json` 示例：

```json
{
  "Urls": "https://0.0.0.0:7443",
  "Kestrel": {
    "Certificates": { "Default": { "Path": "hive.pfx", "Password": "证书密码" } }
  },
  "Hive": {
    "ReadApiKey": "替换为独立随机查询密钥，至少32字符",
    "DatabasePath": "data/hive.db",
    "Bees": [
      { "Id": "bee-01", "Token": "替换为bee-01专属随机密钥，至少32字符" },
      { "Id": "bee-02", "Token": "替换为bee-02专属随机密钥，至少32字符" }
    ]
  }
}
```

小蜜蜂 `appsettings.Local.json` 示例：

```json
{
  "Bee": {
    "Id": "bee-01",
    "Name": "生产服务器01",
    "Region": "中国香港",
    "Ip": "192.0.2.10",
    "HiveUrl": "wss://hive.example.com:7443/agent",
    "Token": "填写蜂巢为bee-01配置的相同Token",
    "DatabasePath": "data/bee.db",
    "Interfaces": []
  }
}
```

`Id` 配置后保持稳定。数据库保存数据代次和序号；不要把同一只小蜜蜂的数据库复制到两台同时运行的服务器。蜂巢配置新节点后重启生效。启动两端即可注册，不需要客户端入站端口。

`Bee:Region` 和 `Bee:Ip` 为可选的节点展示信息，也可通过环境变量 `Bee__Region`、`Bee__Ip` 或命令行 `--Bee:Region`、`--Bee:Ip` 设置。地区为最多 256 字符的自定义文本；IP 为单个标准点分十进制 IPv4 或裸 IPv6 地址（例如 `2001:db8::10`），不含端口、方括号或作用域。两项都会去掉首尾空白，空值表示未设置；IPv6 会规范化格式。IP 由用户填写，不自动探测公网地址，也不据此定位地区或选择采集网卡。示例 IP 为文档专用地址，部署时请替换为实际值或留空。

修改后重启小蜜蜂，下一次成功连接会更新蜂巢保存的地区与 IP；留空可清除原值。节点列表、详情和周期额度查询均返回这两个字段，节点离线或蜂巢重启后仍可查询最后保存的值。旧版小蜜蜂及旧数据库缺少字段时返回 `null`，无需手动迁移数据库；旧版小蜜蜂重新连接也会将这两个字段清空。

生产直接使用受客户端信任的 HTTPS/WSS 证书，不关闭证书验证。服务仅在显式设置 `AllowInsecureTransport=true` 时允许明文连接，供本机开发使用；本机开发同时设置蜂巢 `Urls=http://127.0.0.1:7443` 和小蜜蜂 `HiveUrl=ws://127.0.0.1:7443/agent`。环境变量覆盖监听地址使用 `URLS`，以覆盖配置文件里的 `Urls`。

相对数据库路径按应用 ContentRoot 解析。作为系统服务部署时，建议始终使用绝对数据库路径并给服务账号授予该目录写权限。

### 网卡选择

```powershell
./GreatMaoNetBee.exe --list-interfaces
```

该命令无需连接配置，输出网卡 ID、名称及链路速率。默认 `Interfaces=[]` 自动选取主出口：先 IPv4 默认路由，再 IPv6，按 metric 选择；Windows 使用路由 metric 与接口 metric 之和。每 5 秒刷新路由和网卡元数据，每秒只读取已选网卡的计数。

指定白名单可覆盖自动选择：`"Interfaces": ["Ethernet"]` 或 `"Interfaces": ["eth0", "eth1"]`，支持 ID 或名称。所选网卡汇总为一个节点，调用方应避免把承载同一份流量的网桥、VPN、底层接口同时加入。Linux 自动检测基于 `/proc/net/route` 与 `/proc/net/ipv6_route`；策略路由或特殊网络命名空间应使用白名单，在对应网络命名空间启动程序。

## 统计口径与持久化

- 接收即下载，发送即上传。累计以 64 位字节表示，速率以 bit/s 表示；`totalBytes=receivedBytes+sentBytes`。JavaScript 查询方需注意超过 `Number.MAX_SAFE_INTEGER` 时的整数字段精度。
- 首次启动建立基线，不计入启动前已有的网卡计数。监控内网流量、协议开销和监控自身通信，不等同运营商账单。
- 每秒读取计数，以单调时钟计算实际时间差；每约 5 秒按 UTC 时间桶保存并上传，首次/退出时可能有短桶。峰值取采样得到的每秒速率，无法测量比采样周期更短的瞬时峰值。
- 网卡链路速率 `linkSpeedBitsPerSecond` 与实际传输速率分开，无法读取时为 `null`；不推断购买的公网带宽。
- SQLite WAL + FULL 同步事务原子保存累计、计数器检查点和待上传记录。进程重启后，同一系统启动及同一网卡计数连续时，恢复停机期间的差值到累计；该段不会伪造为瞬时峰值或均摊进图表。
- 系统重启、网卡计数归零、网卡切换、采样延迟或系统时间跳变会标记缺口。未监控期间的其他网卡历史计数不会补入。突然断电且操作系统计数丢失时，末尾尚未落盘的字节无法恢复。
- 时钟跳变时不把尚未落盘的旧时间桶挪到新时间伪造历史，累计仍保留。蜂巢遇到超前 5 分钟以上的时间戳，会接收累计、标记缺口并跳过该条历史，避免修正时钟后补传队列永久阻塞；实时接口遇到超过 5 分钟的时钟偏差返回 `503`。部署时仍应启用系统时钟同步。
- 累计 `hasGaps/lastGapAt` 表示有过不连续的观测；历史每点另有 `hasGap/coverageSeconds`。恢复到累计但无法分配时间的字节不计入历史桶，因此存在缺口时历史求和可能小于累计增量。
- 小蜜蜂最多保留 7 天未确认明细，过期删除后仍保留累计和丢弃序号。蜂巢对 `(节点数据代次, 序号)` 采用持久化高水位去重，整批提交成功才确认；丢失确认后的重传不会重复计费或重复聚合。
- 蜂巢在同一事务内维护 5 秒、分钟、小时桶，分别保存 24 小时、90 天、2 年，每分钟清理过期桶。平均值由字节数/有效观测时长计算，峰值取最大值。累计长期保存；缺失桶的字节与速率为 `null`，正常无流量才是 `0`。
- 单实例 SQLite 写入队列容量 256，单批最多 100 条，WebSocket 消息最多 512 KiB，支持背压。客户端一批等待确认后才发送下一批，重连采用带抖动的指数退避，最高约 61 秒。
- 15 秒应用心跳，45 秒无有效消息离线。短暂离线不删除节点及历史。实时查询并发时合并等待下一次采样，避免每个 HTTP 请求单独采集。

**备份和恢复：**备份 SQLite 应使用其在线备份机制，或停止对应服务后复制数据库。运行中不能只拷贝 `.db` 而遗漏 WAL。蜂巢丢失已确认历史或小蜜蜂数据库重建时，握手会明确拒绝不一致的检查点；恢复匹配数据库，或为新监控生命周期配置新节点 ID。不能通过回退序号来继续旧累计。

## HTTP API

### Swagger 文档与调试

蜂巢启动后访问 `https://<蜂巢地址>:7443/swagger`（自动跳转到 `/swagger/index.html`）。OpenAPI 文档地址为 `/swagger/v1/swagger.json`，包含健康检查及全部流量查询接口、参数、响应结构和错误码。页面在生产环境同样可用；HTML、JavaScript 和 CSS 全部内嵌到蜂巢二进制，无需外部 CDN，随 AOT 发布和自动更新一起交付。

点击 **Authorize**，输入 `Hive.ReadApiKey` 的值（不加 `Bearer ` 前缀），即可使用 **Try it out → Execute** 调试查询。节点 Token 无法用于查询 API。页面不会持久保存输入的密钥；文档和静态资源可匿名访问，实际 `/api` 请求仍需有效 API Key。默认仍要求 HTTPS，本地 HTTP 调试需显式启用 `Hive:AllowInsecureTransport=true`。`/agent` 是小蜜蜂 WebSocket 协议入口，不作为 Swagger HTTP 调试接口。

除 `/healthz` 外，查询接口均携带 `Authorization: Bearer <Hive.ReadApiKey>`。节点 Token 不能用于查询 API。所有时间戳为带偏移量的 ISO 8601；响应统一 UTC。

| 方法和路径 | 说明 |
|---|---|
| `GET /healthz` | 存活检查，不依赖节点在线 |
| `GET /api/v1/bees` | 已注册节点及地区 `region`、IP `ip`、在线状态、累计摘要 |
| `GET /api/v1/bees/traffic/quotas` | 所有已配置节点的周期额度、本周期已观测流量、剩余额度和超额标记 |
| `GET /api/v1/bees/{id}` | 名称、地区 `node.region`、IP `node.ip`、系统、当前统计网卡、链路速率、最后采集/接收时间 |
| `GET /api/v1/bees/{id}/online/history?from=...&to=...` | 指定窗口的 online/offline/unknown 区间和时长 |
| `GET /api/v1/bees/{id}/traffic/total` | 自监控启用以来累计；离线仍可查询 |
| `GET /api/v1/bees/{id}/traffic/current` | 请求后下一次有效采样；最多等待 3 秒 |
| `GET /api/v1/bees/{id}/traffic/history?from=...&to=...&step=auto` | 折线图时间序列 |

`total` 在节点尚未提交首批记录时为 `null`。`lastSampledAt` 是小蜜蜂上一次提交的实际采样时间，`lastReceivedAt` 是蜂巢最后收到消息的时间，两者含义不同。

历史 `step` 支持 `auto|5s|1m|1h`。返回覆盖请求范围的完整 UTC 桶，`from` 向下对齐，`to` 向上对齐，响应的 `from/to` 给出实际边界，时间桶为左闭右开。`auto` 选择满足保留期和最多 10,000 个点的最细粒度；明确指定已过期粒度、无效时间范围或超出点数上限返回 `400`。区间当前仍未完成的桶可能不完整，由覆盖状态表示。

示例：

```sh
curl -H "Authorization: Bearer $HIVE_READ_KEY" https://hive.example.com:7443/api/v1/bees
curl -H "Authorization: Bearer $HIVE_READ_KEY" https://hive.example.com:7443/api/v1/bees/bee-01/traffic/total
curl -H "Authorization: Bearer $HIVE_READ_KEY" https://hive.example.com:7443/api/v1/bees/bee-01/traffic/current
curl -G -H "Authorization: Bearer $HIVE_READ_KEY" \
  --data-urlencode 'from=2026-11-04T00:00:00Z' \
  --data-urlencode 'to=2026-11-04T01:00:00Z' \
  --data-urlencode 'step=auto' \
  https://hive.example.com:7443/api/v1/bees/bee-01/traffic/history
```

历史点包含 `receivedBytes/sentBytes`、`downloadBitsPerSecond/uploadBitsPerSecond`、`peakDownloadBitsPerSecond/peakUploadBitsPerSecond`、`coverageSeconds/hasGap` 及网卡元数据。多条采样合并成同一桶时，网卡元数据为最后入账记录的值。

错误采用 `application/problem+json`：`400` 参数/HTTPS 要求；`401` 凭证不正确；`404` 节点未知；`503` 节点离线、已断开或无可用网卡采样；`504` 实时采样超时。离线或超时时不返回伪装成实时值的缓存。

## 周期额度与在线历史

### 周期流量额度（仅查询）

在蜂巢的 `appsettings.Local.json` 中为每项 `Hive:Bees` 配置 `Quota`，重启蜂巢生效。例如每月按香港时间重置 1 TiB 的上传与下载合计额度：

```json
{
  "Hive": {
    "Bees": [
      {
        "Id": "bee-01",
        "Token": "填写该节点现有的独立随机密钥，至少32字符",
        "Quota": {
          "LimitBytes": 1099511627776,
          "Period": "monthly",
          "ResetDay": 1,
          "UtcOffsetHours": 8
        }
      }
    ]
  }
}
```

| 配置 | 含义 |
|---|---|
| `LimitBytes` | 非负整数，单位字节；`null` 表示未设置上限，`0` 表示零额度 |
| `Period` | `daily`、`weekly`、`monthly`；默认 `monthly` |
| `ResetDay` | 每月第 1～31 日；短月没有对应日期时取月末，下月恢复原日期。每周为 1（周一）～7（周日）；每日必须为 1 |
| `UtcOffsetHours` | 固定整小时时区偏移，-12～14，默认 0（UTC）；香港为 8。不自动跟随夏令时 |

每个周期在当地时间 00:00 开始，区间左闭右开。无需定时清空数据库，查询按当前周期选择数据；重启不会清零，修改额度或周期也能按已有数据重新统计。此设置**不会限速、断网或拒绝小蜜蜂上传**，也不会修改终身累计流量。

`GET /api/v1/bees/traffic/quotas` 需携带查询 API Key，返回 `asOf` 和 `bees` 数组，包含所有当前配置的节点，包括离线、从未连接和未设上限的节点。每项包含：

- `id/name/region/ip/online`、`quota` 配置、UTC `periodStart/periodEnd`（未连接或未设置地区/IP 时对应字段为 `null`）；
- 本周期 `receivedBytes/sentBytes/totalBytes`，其中总量为上传与下载之和；
- `remainingBytes`（最小为 0）、`isExceeded`（总量严格大于额度时为 true）；
- `hasGaps`、`lastSampledAt/lastReceivedAt`，用于判断采集完整性及新鲜度。

本周期没有可用采样时，用量、剩余额度和超额标记为 `null`，不会伪装成零用量；确有采样但没有流量时为 `0`。未设上限时，剩余额度和超额标记为 `null`，仍统计用量。

统计从已有的持久化小时汇总读取，每条采样按其起始时间归属周期，重复补传不重复计数；额度周期边界与小时汇总对齐。`hasGaps` 表示周期开始至最后采样时间存在缺口或仅覆盖部分周期，**不表示节点当前在线或数据实时**，应同时检查 `online` 和采样时间。停机补计、丢弃的补传历史及时间错误中无法分配到具体时段的字节只保留在终身累计，不凭空分配到本周期；因此有缺口时该用量是已知采样总和，实际用量可能更高，剩余额度不能作为准确账单。正常周期切换和历史清理不影响当前日/周/月周期。

```sh
curl -H "Authorization: Bearer $HIVE_READ_KEY" https://hive.example.com:7443/api/v1/bees/traffic/quotas
```

### 在线历史与时间窗口查询

`GET /api/v1/bees/{id}/online/history?from=...&to=...` 返回指定节点在时间窗口内的连续区间 `intervals`（`from/to/status`），以及 `onlineSeconds/offlineSeconds/unknownSeconds`。需要查询 API Key；两个时间参数须为带时区的 ISO 8601 格式，窗口最多 31 天，`to` 不得晚于当前时间。返回 UTC、毫秒精度、左闭右开的区间，区间无重叠且覆盖整个请求窗口；相邻同状态区间会合并。

| 状态 | 含义 |
|---|---|
| `online` | 蜂巢记录到已鉴权的 WebSocket 会话，静默不足 45 秒；这表示与蜂巢的连接状态，不代表整台服务器或全部网络可用 |
| `offline` | 蜂巢在运行且配置中包含该节点，但没有有效会话；从未连接的已配置节点也可查询 |
| `unknown` | 蜂巢自身停机、尚未启用本功能、当时未配置该节点、已过保留期，或最近尚未持久化观测的时间 |

蜂巢每 5 秒持久化一次观测进度，连接和有效消息也会推进进度。正常断开会关闭对应会话；异常退出后，下次启动只恢复至最后持久化的观测点，**不会把蜂巢停机误报成小蜜蜂离线**，也不会向前推测为持续在线。窗口末端可能有约 5 秒 `unknown`，应以返回的区间为准。每个新连接使用独立会话标识，旧连接清理不会结束新连接。

在线记录默认保留 90 天，可配置 `Hive:OnlineHistoryRetentionDays`（1～730）并重启生效；过期窗口仍可查询，但过期部分为 `unknown`。窗口中会话/观测记录过多（任一超过 10,000 条）返回 400，需缩小窗口。旧数据库启动时自动新增记录表，保留原有流量数据；升级前没有记录的在线历史无法补造。离线或已从配置移除、但数据库仍有记录的节点可以查询其历史。

```sh
curl -G -H "Authorization: Bearer $HIVE_READ_KEY" \
  --data-urlencode 'from=2026-11-07T00:00:00+08:00' \
  --data-urlencode 'to=2026-11-07T12:00:00+08:00' \
  https://hive.example.com:7443/api/v1/bees/bee-01/online/history
```

以上两个接口均已加入内置 Swagger，可直接授权调试。

## Windows Service 部署

将整个发布目录放入固定位置，先配置密钥、证书和数据目录，再以管理员身份注册：

```powershell
sc.exe create GreatMaoNetHive binPath= '"C:\GreatMao\Hive\GreatMaoNetHive.exe"' start= auto
sc.exe create GreatMaoNetBee binPath= '"C:\GreatMao\Bee\GreatMaoNetBee.exe"' start= auto
sc.exe failure GreatMaoNetHive reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe failure GreatMaoNetBee reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start GreatMaoNetHive
sc.exe start GreatMaoNetBee
```

两端不必安装在同一机器。使用专用账号时，给数据库、安装目录和更新目录写权限、配置/证书读权限，并授予启动自身服务的权限。自动更新默认启用；如需人工升级，设置 `Updates:Enabled=false`，停止服务后替换程序文件，保留配置与数据库，再启动。示例配置请保存为发布目录内的 `appsettings.Local.json`，数据路径使用绝对路径。

## Linux systemd 部署

### 一键安装（推荐）

Linux x86_64 / glibc、已运行 systemd 的系统可直接下载安装脚本，无需安装 .NET：

**私有仓库：**当前仓库为私有，下载脚本和 Release 都需要对本仓库拥有 `Contents: read` 权限的 GitHub Token。以下交互输入不会回显 Token，也不会把 Token 写入配置文件：

```sh
read -rsp 'GitHub Token: ' GH_TOKEN; echo
export GH_TOKEN
curl -fL --proto '=https' -H "Authorization: Bearer $GH_TOKEN" \
  -H 'Accept: application/vnd.github.raw+json' \
  https://api.github.com/repos/kitUIN/GreatMaoHive/contents/scripts/install-linux.sh \
  -o install-linux.sh
sudo --preserve-env=GH_TOKEN bash install-linux.sh hive  # 蜂巢服务器
# 或在需要监控的服务器上执行：
sudo --preserve-env=GH_TOKEN bash install-linux.sh bee
unset GH_TOKEN
```

**公开仓库：**如果仓库已设为公开，可以直接匿名下载：

```sh
curl -fL --proto '=https' https://raw.githubusercontent.com/kitUIN/GreatMaoHive/main/scripts/install-linux.sh -o install-linux.sh
# 安装蜂巢；在蜂巢服务器上执行。
sudo bash install-linux.sh hive
# 安装小蜜蜂；在需要监控的服务器上执行。
sudo bash install-linux.sh bee
```

不填写 `hive` / `bee` 时会询问安装角色。脚本从 GitHub 查询最新稳定 Release，按同一个版本下载对应 Linux 包和 SHA-256 文件，校验并验证程序能在当前系统运行后，创建 `greatmao` 服务账号、部署完整文件、注册 systemd 服务并启用开机自启。缺少 Python 3 / OpenSSL 3 等依赖时自动通过 apt-get 或 dnf 安装；其他发行版需先安装依赖。当前发布包在 Ubuntu 24.04 上构建，较旧的 glibc 系统可能无法运行，脚本会在停止原服务前报错。

首次安装 Hive 会询问首个 Bee ID，并生成独立的 ReadApiKey 和 Bee Token。默认 HTTPS 模式需要已有、受客户端信任的 PFX 证书；脚本会把证书复制到 `/etc/greatmao/certificates/hive.pfx` 并设置服务读取权限。也可选择 `local-proxy` 模式，仅监听 `http://127.0.0.1:7443`；此时需自行配置支持 WebSocket 的 HTTPS 反向代理。脚本不签发证书、不修改防火墙。

Hive 的密钥保存在 `/opt/greatmao/hive/appsettings.Local.json`，用 `sudo cat /opt/greatmao/hive/appsettings.Local.json` 查看。安装 Bee 时输入相同的 Bee ID、Token 和 Hive 的 `wss://域名:端口/agent` 地址；可填写名称、地区和 IP。需要多个 Bee 时，在 Hive 配置中的 `Hive.Bees` 数组添加各自不同的 ID/Token，再执行 `sudo systemctl restart greatmaonethive`。

无人值守首次安装可提供完整配置文件（与上文 `appsettings.Local.json` 格式相同，Hive 须包含 `Urls`）：

```sh
sudo bash install-linux.sh hive --config /root/hive.json
sudo bash install-linux.sh bee --config /root/bee.json
```

未指定数据库路径时，默认使用 `/var/lib/greatmao/hive/hive.db` 或 `/var/lib/greatmao/bee/bee.db`。自定义路径须符合服务的目录权限与 `ProtectHome` / `ReadWritePaths` 限制。脚本生成的配置文件权限为 `0640`，不会在安装输出中打印密钥。

重复执行 `sudo bash install-linux.sh hive` / `bee` 可重新部署最新稳定版本，保留已有 `appsettings*.json`、`/etc/greatmao/*.env` 和数据库；已有配置时不能使用 `--config` 覆盖。自定义环境文件仍优先于 JSON 生效。服务启动失败时恢复本次替换的文件，并尝试恢复原先运行的服务。已有待处理自动更新时会停止安装，请先检查更新日志。

公开仓库下，安装后的服务默认继续每 6 小时自动更新；已有配置中的更新开关仍然生效。**私有仓库的 Token 仅用于本次安装，当前应用后台更新不支持 GitHub 凭据，因此需携带 `GH_TOKEN` 重跑脚本来升级。**服务启动成功不代表 Bee 已连接到 Hive，需查看连接日志：

```sh
sudo systemctl status greatmaonethive
sudo systemctl status greatmaonetbee
sudo journalctl -u greatmaonethive -f
sudo journalctl -u greatmaonetbee -f
```

### 手动安装

1. 创建 `greatmao` 专用账号，将完整发布目录分别放到 `/opt/greatmao/hive`、`/opt/greatmao/bee`，给该账号安装目录的写权限，并为主程序及 `GreatMaoNetUpdater` 设置执行权限。
2. 创建并授权 `/var/lib/greatmao/hive`、`/var/lib/greatmao/bee`；将对应 `.env.example` 复制到 `/etc/greatmao/hive.env` 或 `bee.env`，替换密钥和证书路径。密钥文件只允许管理员/服务账号读取。
3. 将所需 `.service` 文件复制到 `/etc/systemd/system/`，执行 `systemctl daemon-reload`，然后 `systemctl enable --now greatmaonethive` 或 `greatmaonetbee`。
4. 使用 `journalctl -u greatmaonetbee -f` 查看重连及异常日志。无需每秒记录正常采样日志。

服务单元通过 `ExecStopPost` 在主程序退出后安装待更新文件，`Restart=always` 自动启动新版；手动 `systemctl stop` 仍会保持停止。必须同时更新本仓库的服务单元并执行 `systemctl daemon-reload`，旧单元不支持自动安装。更新目录固定为 `/var/lib/greatmao/<hive或bee>/updates`；如需更换，应同时修改 `Updates__DownloadDirectory` 和 `ExecStopPost` 的路径。`ReadWritePaths` 已允许安装目录和数据目录写入，但仍需正确设置实际文件所有权。

## 验证与性能

```powershell
./scripts/smoke.ps1 `
  -HivePath artifacts/publish/GreatMaoNetHive/win-x64/GreatMaoNetHive.exe `
  -BeePath artifacts/publish/GreatMaoNetBee/win-x64/GreatMaoNetBee.exe
```

脚本仅监听本机随机端口，生成临时凭证和独立数据库，检查真实网卡采集、实时/历史查询、蜂巢断开重连以及小蜜蜂异常退出后的恢复，结束后清理它启动的进程。结果与日志保存在 `artifacts/verification/smoke`，默认预热 15 秒、测量 30 秒 CPU/工作集，仅用于联调，不是长期性能承诺。可传 `-TransferUrl <公开下载地址>` 检查已知长度下载是否同时反映在选定网卡计数与持久化累计中；该检查会实际下载数据，且要求下载经过被监控网卡。

采集不解析业务数据包，采样热路径只读取操作系统计数；CPU 开销主要取决于网卡数量与采样频率。数据库提交、JSON 序列化及上报集中到约 5 秒一次。Native AOT 与普通自包含 JIT 包的内存、启动和 CPU 表现应分开测量。长期保留历史需要预留磁盘容量，SQLite 删除过期记录后文件空间会复用，不会立即缩小文件。

本地实际验证记录见 `docs/verification.md`。
