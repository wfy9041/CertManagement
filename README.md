# Certificate Manager

基于 .NET 8、ASP.NET Core Blazor Server、EF Core SQLite、Certes 和 NLog 的 Let's Encrypt 证书管理工具。

## 已实现功能

- 域名 CRUD、启用/禁用、SAN、HTTP-01/DNS-01 选择和通配符约束。
- Let's Encrypt Staging/Production 配置、ACME Account Key 本地保存、Certes 申请流程。
- HTTP-01 `/.well-known/acme-challenge/{token}` 并发临时存储。
- Cloudflare 与阿里云 DNS Provider 抽象、Data Protection 加密密钥、支持多个公共 DNS 查询端点回退的 TXT 传播轮询。
- 证书状态机、PEM 文件输出、受保护的 PEM/PFX 下载接口。
- BackgroundService 自动扫描 30 天内到期证书，重试并使用数据库状态条件更新防止并发续期。
- NLog 控制台和按日期滚动文件日志；日志不写入私钥、Provider 密钥或 ACME Account Key。
- Cookie 管理员认证；所有管理 API 与管理页面需要登录。

## 启动

1. 安装 .NET 8 SDK。
2. 配置 `appsettings.json` 中的 `Acme:Email`。默认 ACME 环境为 Staging，生产申请前请明确改为 `Production`。
3. 生成管理员密码哈希。可在 .NET 交互环境调用 `AdminCredentialService.Hash("你的密码")`，把结果写入 `Admin:PasswordHash`；不要把明文密码写入配置或日志。
4. 启动：

```powershell
dotnet restore
dotnet run --no-launch-profile
```

应用启动时会执行 `Database.MigrateAsync()`，SQLite 文件位于 `data/certmanager.db`。证书目录是 `data/certificates`，日志目录是 `data/logs`，均不在 WebRoot 下。

## 1Panel / Docker 部署

不要只把发布文件复制到容器内运行；容器重建后容器内的 `data` 会随容器丢失。仓库提供的 `Dockerfile` 和 `docker-compose.yml` 会将宿主机的 `./data` 挂载到容器 `/app/data`：

```bash
docker compose up -d --build
```

在 1Panel 的容器编排中也应保留以下挂载：

```yaml
volumes:
  - /宿主机/固定目录/cert-management-data:/app/data
```

其中 `/app/data` 必须同时保存 `certmanager.db`、`certificates`、`acme`、`logs` 和 `keys`。如果使用 1Panel 的.NET运行环境而不是 Docker 编排，请把应用目录下的 `data` 配置为持久化目录，并在应用重建/更新时不要清空该目录。

## Windows 服务部署

项目支持作为 Windows Service 运行。建议将发布目录放在 `C:\CertManagement`，并确保服务账户对该目录具有读写权限，因为数据库、证书、ACME 账户密钥和日志都位于发布目录下的 `data` 文件夹。

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\CertManagement
sc.exe create CertManagement binPath= '"C:\Program Files\dotnet\dotnet.exe" "C:\CertManagement\CertManagement.dll"' start= auto DisplayName= "Certificate Management"
sc.exe description CertManagement "Certificate Management ASP.NET Core service"
sc.exe start CertManagement
```

停止或卸载服务：

```powershell
sc.exe stop CertManagement
sc.exe delete CertManagement
```

如果服务器没有安装 .NET 8 Runtime，可发布为自包含程序，然后将 `binPath` 改为 `C:\CertManagement\CertManagement.exe`：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o C:\CertManagement
```

## 使用顺序

先在 DNS Providers 添加并测试 Provider，再在 Domains 添加域名。普通域名可选 HTTP-01；通配符或 DNS-01 必须选择 DNS Provider。随后使用 Certificates 页面申请或续期。

PFX 不落库密码：通过 `POST /api/certificates/{id}/download/pfx` 提供一次性密码即时生成。PEM/PFX 下载均受 Cookie 认证保护，证书目录不直接静态开放。

## 配置与安全注意事项

- 生产环境应持久化 ASP.NET Core Data Protection Key Ring，并限制 `data` 目录权限。
- ACME Account Key、DNS API Key/Token、DNS Secret 只保存为受保护文本；不要提交数据库、日志或源代码。
- `Admin:PasswordHash`、ACME 邮箱、Provider 凭据建议使用环境变量或外部 Secret Store 覆盖配置。
- DNS 传播使用 TXT 查询轮询和超时，不使用单次固定 Delay 判定生效。

## 验证

```powershell
dotnet build --no-restore
dotnet test tests/CertManagement.Tests/CertManagement.Tests.csproj --no-restore
```

当前包含域名 SAN 归一化和管理员 PBKDF2 密码校验的基础测试。真实 Let's Encrypt 签发仍需要可访问的公网 HTTP-01 或可用 DNS API 凭据，建议先使用 Staging。
