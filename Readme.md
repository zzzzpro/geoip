# GeoIP API 服务技术概览

## 项目定位

本项目旨在提供一个高性能、可配置且易于部署的 IP 地址归属地信息查询（GeoIP）API 服务。它利用 .NET 9 的最新特性，包括 AOT (Ahead-of-Time) 编译，以实现优化的启动速度和资源占用。

## 核心技术特性

* **IP 信息查询**: 支持 IPv4/IPv6 地址的地理位置（城市、国家、大洲、经纬度、时区）、ISP、组织及自治系统 (AS) 信息的精确检索。

* **动态 IP 获取**: 若 API 请求未显式指定 IP 地址，服务将自动解析并查询请求来源客户端的 IP 地址。

* **自动化数据库管理**: 集成后台服务 (`BackgroundService`)，通过 CRON 表达式定义的调度策略，定期从 MaxMind 自动下载并更新 GeoLite2 数据库，确保数据时效性。

* **AOT 编译优化**: 项目结构和依赖项选择均考虑了 AOT 兼容性，可编译为高效的本机代码，显著减少部署体积和冷启动时间。

* **灵活的端口配置**: 支持通过 `appsettings.json` (Kestrel Endpoints)、环境变量 (`ASPNETCORE_URLS`) 或命令行参数动态配置服务监听端口。

* **规范化响应格式**: API 响应采用 JSON 格式。对于 `string` 类型的 `null` 值，将统一格式化为空字符串 (`""`)，以提升客户端处理一致性。错误响应遵循标准的 `ProblemDetails` 或 `ValidationProblemDetails` 结构。

## 技术架构与组件

* **框架**: .NET 9, ASP.NET Core

* **API 类型**: Minimal APIs，提供轻量级路由和处理。

* **GeoIP 库**: `MaxMind.GeoIP2` 官方 .NET 库。

* **JSON 序列化**: `System.Text.Json`，并配置 `JsonSerializerContext` 以支持 AOT 环境下的高效、无反射序列化。

* **后台任务**: `BackgroundService` 实现数据库的周期性更新。

* **HTTP 客户端**: `IHttpClientFactory` 管理用于数据库下载的 `HttpClient` 实例。

* **日志与配置**: 标准的 ASP.NET Core 日志和配置框架。

## API 端点定义

* **`GET /geoip/{ipAddress?}`**

  * `ipAddress` (路径参数, 可选): 目标 IP 地址 (IPv4 或 IPv6)。若此参数缺失，则服务将查询发起请求的客户端 IP。

## 关键配置项 (`appsettings.json`)

* `Kestrel.Endpoints.Http.Url`: 服务监听的 URL 及端口 (例如: `"http://*:5100"`)。

* `GeoIp.DatabaseDownloadUrl`: MaxMind GeoLite2 数据库的下载 URL (必须包含有效的许可证密钥)。

  * 当前实现支持直接 `.mmdb` 文件及 `.mmdb.gz` 压缩文件的处理。对于 `.tar.gz` 等其他格式，需在 `GeoIpService` 中扩展解压逻辑。

* `GeoIp.DatabasePath`: GeoIP 数据库在本地的存储路径 (例如: `Data/GeoLite2-City.mmdb`)。应用程序进程需对此路径拥有读写权限。

* `GeoIp.UpdateScheduleCron`: CRON 表达式，用于定义数据库自动更新的调度周期。

## 部署与运维注意事项

1. **初始数据库**: 首次部署前，需在 `GeoIp.DatabasePath` 指定的位置手动放置从 MaxMind 下载的 `GeoLite2-City.mmdb` 文件。

2. **AOT 发布**: 使用 `dotnet publish -c Release -r <TargetRuntimeIdentifier>` 命令进行 AOT 编译和发布。

3. **反向代理集成**: 若部署于 Nginx、YARP 等反向代理之后，需确保代理正确传递 `X-Forwarded-For` 和 `X-Forwarded-Proto` 头部。服务已内置 `ForwardedHeadersMiddleware` 以处理这些头部，确保客户端 IP 地址的准确获取。

4. **权限管理**: 确保运行服务的用户或进程对 `GeoIp.DatabasePath` 指定的目录拥有读取和写入权限，以便进行数据库的初始化和后续更新。

此服务为需要 IP 地理位置信息的应用程序提供了一个稳定、高效且易于集成的解决方案。