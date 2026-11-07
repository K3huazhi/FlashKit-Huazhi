# FlashKit 安卓刷机工具箱

> **项目状态：已暂停 / 停止开发（2026-11）**  
> 这是停止维护时的源码与设计记录。功能可用，但文件系统的开机级元数据恢复仍未闭环，详见下方“已知限制”。

一个基于 **WinUI 3 + Windows App SDK + C# / .NET 8** 的原生 Windows 11 桌面刷机工具箱。目标是把 ADB、Fastboot、分区备份/还原、镜像处理、文件传输和文件系统解包放在一个统一、可审计的桌面应用里，而不是散落的脚本。

> ⚠️ 刷机有风险。请确认镜像与机型、分区完全匹配，并提前备份。本项目不判断第三方镜像是否可信。

## 功能

| 模块 | 说明 |
| --- | --- |
| **任务队列与历史** | 刷入 / 备份 / 还原 / 解包 / 打包 / 镜像转换 / 文件传输串行执行；历史持久化到 `%LOCALAPPDATA%\FlashKit\tasks.json`，支持筛选、取消、打开日志/输出目录 |
| **设备信息** | ADB 读取型号、系统版本、电量、内存、存储、CPU 架构等，对厂商 ROM 差异做容错 |
| **分区备份** | 系统 KSU Root 或 TWRP 下 `adb exec-out dd` 只读回传；支持任意勾选、常规/super/高风险分组、全选/反选、super 覆盖提示 |
| **分区还原** | 从 manifest 校验镜像、大小、型号与序列号；系统/TWRP 用 `adb push + dd`，Fastboot 用 `fastboot flash` |
| **Fastboot 刷入** | 按目标分区名直接 `fastboot flash <partition> <file>`；支持手动单分区和文件夹扫描，目标名可逐条编辑 |
| **卡刷包工具** | A/B `payload.bin`（全量）和纯 img ZIP 解包；纯 img ZIP 打包 |
| **镜像工具** | sparse ↔ raw 流式互转、格式识别（raw/sparse/ext4/f2fs/erofs/payload）、MD5/SHA256、payload 解包 |
| **文件管理** | 设备目录浏览、上传/下载单文件或目录；拒绝写入 `/dev`、`/proc`、`/sys` 与系统分区 |
| **文件系统** | ext4/f2fs/erofs 内容级解包与回打包；ext4 解包内置 7-Zip，f2fs/erofs 使用设备端工具 |
| **终端 / 日志** | 独立 Window 单例，仅接受 `adb` / `fastboot` 前缀命令 |

## 设计思路

完整说明见 [docs/DESIGN.md](docs/DESIGN.md)，分层细节见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

```text
FlashKit.Core       模型 / 接口 / 解析器（无 UI、无进程）
FlashKit.Services   ProcessRunner、ADB/Fastboot、备份还原、镜像工具、文件管理、文件系统、任务队列
FlashKit.App        WinUI 3 页面 / ViewModel / 窗口 / 文案
```

关键决策：

1. **单一进程边界**：所有外部命令经过 `IProcessRunner`，UI 不拼接 shell；`RunToFileAsync` 只写原始字节，用于 `adb exec-out dd`。
2. **单一任务队列**：危险/重 IO 操作全局串行，排队任务可取消；刷入、解包、打包、备份都走同一队列。
3. **manifest 优先**：备份输出 `images/` + `manifest.json`；还原按 manifest 校验分区、大小、型号和序列号，不猜来源。
4. **dd 截断**：`dd bs=4M conv=noerror,sync` 会把末尾块补齐到 4MiB，部分 TWRP 还会把状态写到 stdout；服务按 `blockdev` 容量截断后再校验。
5. **不做假解包**：文件系统只做到内容级，SELinux/capabilities 元数据不完整时明确写警告，不用 raw 复制冒充完整解包。

## 构建

要求 Windows 11 22H2+、.NET 8 SDK、Windows App SDK（项目使用 2.6.4）。

```powershell
dotnet build FlashKit.sln -c Debug
dotnet test tests/FlashKit.Tests/FlashKit.Tests.csproj -c Debug
```

运行：

```powershell
dotnet run --project src/FlashKit.App
```

发布：

```powershell
dotnet publish src/FlashKit.App/FlashKit.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=false `
  -o artifacts/FlashKit-win-x64
```

发布目录需整体分发；`tools/platform-tools/`、`tools/7zip/`、`Assets/` 和 Windows App SDK 运行库不能只复制 `FlashKit.exe`。

## 真机验证

在 Redmi K70（`vermeer` / Android 16 / KernelSU）上实际验证过：

- 枚举 133 个分区
- 备份 `ALIGN_TO_128K_1`：106,496 字节，本地 SHA256 与设备端 `sha256sum` 一致
- 同镜像回写还原，回写后 hash 不变，设备保持 KSU root
- `/sdcard` 浏览、`/data/local/tmp` 上传/下载
- ext4/f2fs/erofs 内容级解包/回打包
- 自动化测试 **43/43 通过**

公开仓库中的验证记录已移除设备序列号和个人目录路径。详见 [docs/VALIDATION.md](docs/VALIDATION.md) 与 [docs/V2_IMPLEMENTATION.md](docs/V2_IMPLEMENTATION.md)。

## 已知限制

- **文件系统只达到内容级**：SELinux context、capabilities、完整 `fs_config`/`file_contexts` 回填未完成；ext4 的 7-Zip 解包只能记录 mode/UID/GID。
- 未测试 15GB `super` 全量备份/还原；未测试断线恢复。
- 未测试 AVB/dm-verity 失败后的救砖流程。
- 未做真实 Fastboot 刷入回归。
- 官方 Recovery 不支持；TWRP 需有 `ro.twrp.version`。
- Fastboot 模式不读取分区；KSU 不可用时不能直接备份。

## 仓库结构

```text
src/FlashKit.Core/        模型、接口、解析器
src/FlashKit.Services/    进程、设备、刷入、备份还原、镜像、文件、文件系统、任务队列
src/FlashKit.App/         WinUI 3 应用、页面、窗口、资源、内置 tools
tests/FlashKit.Tests/     xUnit 测试
docs/                     架构、设计、实现状态、验证记录
```

## 文档

- [整体设计](docs/DESIGN.md)
- [架构](docs/ARCHITECTURE.md)
- [新增模块与文件系统实现状态](docs/V2_IMPLEMENTATION.md)
- [真实验证记录](docs/VALIDATION.md)

## 许可证

仓库未添加开源许可证，默认保留所有权利。如需复用代码，请先联系作者。

