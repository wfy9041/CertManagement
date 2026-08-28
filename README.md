# 证书管理

基于 .NET 8、ASP.NET Core Blazor Server、Entity Framework Core SQLite 和 Certes 的 Let's Encrypt SSL/TLS 证书管理系统。系统提供域名与 DNS Provider 管理、ACME 证书申请/续期、证书文件下载、申请日志，以及阿里云 WAF 证书部署。

## 功能概览

- 管理域名、SAN（Subject Alternative Name）、启用状态和自动续期状态。
- 支持 HTTP-01 和 DNS-01 挑战；通配符域名必须使用 DNS-01。
- 支持 Cloudflare、阿里云 DNS Provider，可测试连接并自动创建/清理 TXT 记录。
- 支持 Let's Encrypt Staging 和 Production 环境；ACME Account Key 按环境和邮箱保存。
- HTTP-01 挑战地址为 `/.well-known/acme-challenge/{token}`，挑战内容使用并发安全的临时存储。
- DNS-01 会通过多个公共 DNS 查询端点轮询 TXT 记录，直到传播成功或超时。
- 证书申请状态可追踪为创建订单、创建挑战、等待 DNS、验证、签发、续期或失败等状态。
- 证书以 PEM 文件保存，支持下载 `cert.pem`、`chain.pem`、`fullchain.pem`、`privkey.pem` 和 ZIP 文件；也支持按请求密码即时生成 PFX。
- 后台服务按配置周期扫描即将到期的证书，默认提前 30 天续期，并支持失败重试和并发续期保护。
- 支持配置阿里云 WAF 2.0 部署目标，测试连接、手动部署已签发证书，并查看部署记录。
- 提供申请日志页面：按证书查看记录、手动刷新、选择证书后每 3 秒自动刷新，并支持滚动加载历史记录。
- 使用 Cookie 管理员认证保护管理页面和管理 API；系统设置页可查看签发配置并修改管理员密码。
- 使用 NLog 输出控制台日志和按日期滚动的文件日志，敏感凭据不会写入日志。

## 管理页面

| 页面 | 地址 | 用途 |
| --- | --- | --- |
| 概览 | `/` | 查看系统中的域名和证书概况 |
| 域名管理 | `/domains` | 新增/修改域名、配置 SAN、挑战类型、DNS Provider、启用和自动续期 |
| 证书管理 | `/certificates` | 申请、续期、查看状态和下载证书文件 |
| 部署目标 | `/waf-deployments` | 管理阿里云 WAF 目标、测试连接、手动部署和查看部署记录 |
| DNS 服务商 | `/dns-providers` | 管理 Cloudflare/阿里云凭据并测试连接 |
| 申请日志 | `/logs` | 查看证书申请和续期过程中的状态变化 |
| 系统设置 | `/settings` | 查看 ACME/续期配置及修改管理员密码 |

## 快速开始

### 环境要求

- .NET 8 SDK 或 .NET 8 Runtime
- 可访问外网的运行环境
- 使用 HTTP-01 时，公网能够访问当前站点的 `80` 端口和挑战路径；使用 DNS-01 时，需要对应 DNS API 权限

### 本地启动

1. 配置 `appsettings.json` 中的 `Acme:Email`。
2. 确认 `Admin:PasswordHash` 已配置为管理员密码的 PBKDF2 哈希。不要将明文密码写入配置、日志或代码。
3. 启动应用：

```powershell
dotnet restore
dotnet run --no-launch-profile
```

默认监听地址为 `http://localhost:8080`（Kestrel 配置为 `http://0.0.0.0:8080`）。首次启动会自动执行 EF Core 数据库迁移。

### 首次使用顺序

1. 登录管理台，默认用户名由 `Admin:Username` 配置，当前默认值为 `admin`，默认密码为`admin123`。
2. 在“DNS 服务商”中新增并测试 Cloudflare 或阿里云凭据。
3. 在“域名管理”中新增域名，填写 SAN，并选择 HTTP-01 或 DNS-01。DNS-01 需要绑定已启用的 DNS Provider。
4. 在“证书管理”中提交申请。生产签发前建议先使用 Staging 验证配置和流程。
5. 证书签发成功后，可在证书详情中下载 PEM、ZIP 或输入一次性密码生成 PFX。
6. 如需部署到阿里云 WAF，在“部署目标”中配置 WAF 实例，测试连接后手动部署已签发证书。

## 配置说明

配置文件为 `appsettings.json`，生产环境建议使用环境变量或外部 Secret Store 覆盖敏感配置。

| 配置项 | 说明 | 默认值 |
| --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | SQLite 数据库连接字符串 | `Data Source=data/certmanager.db` |
| `Admin:Username` | 管理员用户名 | `admin` |
| `Admin:PasswordHash` | PBKDF2 密码哈希 | 必填 |
| `Acme:Environment` | ACME 环境，`Staging` 或 `Production` | `Staging` |
| `Acme:Email` | ACME 账户邮箱 | 必填 |
| `Acme:AccountKeyPath` | ACME Account Key 路径 | `data/acme/account-key.pem` |
| `Acme:CertificateDirectory` | 证书文件目录 | `data/certificates` |
| `Acme:DnsPropagationTimeoutSeconds` | DNS TXT 传播最大等待秒数 | `180` |
| `Acme:DnsPollingIntervalSeconds` | DNS TXT 轮询间隔秒数 | `5` |
| `Acme:DnsQueryEndpoints` | 公共 DNS 查询端点列表 | 阿里云 DNS、`doh.pub` |
| `Renewal:ScanIntervalMinutes` | 自动续期扫描间隔 | `15` |
| `Renewal:RenewBeforeDays` | 距离过期多少天开始续期 | `30` |
| `Renewal:MaxAttempts` | 单次自动续期最大尝试次数 | `3` |
| `Kestrel:Endpoints:Http:Url` | HTTP 监听地址 | `http://0.0.0.0:8080` |

管理员密码哈希可通过应用中的 `AdminCredentialService.Hash("你的密码")` 生成。修改密码也可以直接在“系统设置”页面完成；修改成功后当前登录会话会退出。

## 数据目录与持久化

应用会在内容根目录下创建 `data` 目录：

```text
data/
├─ certmanager.db       # SQLite 数据库
├─ certificates/        # cert.pem、chain.pem、fullchain.pem、privkey.pem
├─ acme/                # ACME Account Key
├─ keys/                # ASP.NET Core Data Protection Key Ring
└─ logs/                # NLog 文件日志
```

`data` 是运行所需的持久化目录。容器、面板或服务重建时必须将宿主机目录挂载或配置为持久化目录，否则数据库、证书、ACME 账户密钥和 Data Protection 密钥可能丢失。仓库当前未提供 Dockerfile 或 `docker-compose.yml`，使用容器时请在外部编排配置中挂载整个 `data` 目录到应用目录下的 `/app/data`（或对应内容根目录）。

## 部署

### Windows Service

项目已接入 Windows Service Host，发布后可注册为 Windows 服务。以下示例使用框架依赖部署：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\CertManagement
sc.exe create CertManagement binPath= '\"C:\Program Files\dotnet\dotnet.exe\" \"C:\CertManagement\CertManagement.dll\"' start= auto DisplayName= \"Certificate Management\"
sc.exe description CertManagement \"Certificate Management ASP.NET Core service\"
sc.exe start CertManagement
```

服务账户需要对发布目录及其 `data` 子目录具有读写权限。停止或删除服务：

```powershell
sc.exe stop CertManagement
sc.exe delete CertManagement
```

如果服务器没有安装 .NET 8 Runtime，可改用自包含发布：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o C:\CertManagement
```

此时服务的 `binPath` 可直接指向 `C:\CertManagement\CertManagement.exe`。

### 反向代理与公网挑战

部署到 Nginx、IIS 或其他反向代理后，应将外部请求转发到应用监听端口，并确保：

- HTTP-01 的 `/.well-known/acme-challenge/` 路径不会被缓存、拦截或重写；
- DNS-01 环境能够访问 DNS Provider API 和配置的公共 DNS 查询端点；
- 生产环境持久化 `data/keys`，避免重启后 Cookie 和受保护凭据无法解密；
- 证书目录、数据库、日志和密钥目录仅授予应用服务账户必要权限。

## 受保护 API

所有以下管理 API 都要求 Cookie 登录，登录接口除外：

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `POST` | `/api/auth/login` | JSON 登录 |
| `POST` | `/api/domains` | 保存域名 |
| `PATCH` | `/api/domains/{id}/enabled` | 修改域名启用状态 |
| `POST` | `/api/certificates/{id}/issue` | 申请证书 |
| `POST` | `/api/certificates/{id}/renew` | 手动续期 |
| `GET` | `/api/certificates/{id}/download/{kind}` | 下载 PEM 文件 |
| `GET` | `/api/certificates/{id}/download/bundle` | 下载 PEM ZIP 包 |
| `POST` | `/api/certificates/{id}/download/pfx` | 生成并下载 PFX |
| `GET/POST` | `/api/dns-providers` | 管理和测试 DNS Provider |
| `GET/POST` | `/api/waf-deployments/targets` | 管理 WAF 部署目标 |
| `POST` | `/api/waf-deployments/certificates/{id}/deploy` | 部署证书到 WAF |

HTTP-01 挑战端点 `/.well-known/acme-challenge/{token}` 为 ACME 验证所需的匿名访问端点。证书文件目录不作为静态文件目录直接开放，下载由受保护接口提供。

## 安全注意事项

- 生产环境必须使用 `Production` 前确认域名、DNS API 权限和续期流程已在 Staging 验证。
- 不要提交 `appsettings` 中的真实凭据、SQLite 数据库、ACME Account Key、DNS 密钥、WAF Secret 或 `data` 目录。
- DNS Provider 凭据和 WAF AccessKeySecret 使用 ASP.NET Core Data Protection 保护后保存；请持久化 `data/keys` 并限制其访问权限。
- PFX 密码只用于当前请求，不落库；私钥下载接口同样受管理员 Cookie 认证保护。
- 定期备份 `data/certmanager.db`、`data/acme`、`data/certificates` 和 `data/keys`，并安全保存备份。

## 验证

仓库当前未包含独立测试项目。修改后可执行以下静态检查和构建命令：

```powershell
git diff --check
dotnet build --no-restore
```

真实证书签发、DNS 传播、WAF 部署和公网 HTTP-01 验证需要实际运行环境及对应凭据，不能仅由本地编译结果证明。
