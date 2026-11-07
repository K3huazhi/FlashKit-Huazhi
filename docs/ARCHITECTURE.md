# FlashKit 架构设计

## 1. 目标与边界

首版目标是一个可编译、可运行、可扩展的 WinUI 3 桌面刷机工具箱，而不是一次性把所有安卓刷机协议都实现完。明确不做：

- payload.bin 回打包
- .dat.br / .new.dat 打包
- 以本机 Windows 系统信息代替设备信息
- 内置厂商专用驱动
- 多语言（首版中文，但 UI 文案集中管理）

## 2. 技术选型

| 项 | 选择 | 说明 |
| --- | --- | --- |
| UI | WinUI 3 / Windows App SDK 2.6.4 | 构建时 NuGet 最新稳定版；原生 Mica、TitleBar、NavigationView |
| 语言 | C# 12 / .NET 8 | `net8.0`（Core）与 `net8.0-windows10.0.26100.0`（App） |
| 最低系统 | Windows 11 22H2（22621） | TFM 使用 26100 SDK；低于 22H2 不承诺运行 |
| 部署 | 非 MSIX，unpackaged | `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` |
| ADB / Fastboot | 内置 Google platform-tools | 路径来自 `AppContext.BaseDirectory`，工作目录显式指定 |
| 压缩 | SharpCompress | BZip2 / XZ 流式解压；ZipArchive 负责 ZIP |

> 目标 TFM 是 `net8.0-windows10.0.26100.0`，`SupportedOSPlatformVersion` 为 22621。Windows 11 26H2 可运行；使用 26100 SDK 是因为 Windows App SDK 2.6.4 的 BuildTools 基线为 26100。

## 3. 项目与依赖方向

```
FlashKit.App (WinUI 3)
   │  依赖
   ├──────────────► FlashKit.Services
   │                     │
   │                     └──────────────► FlashKit.Core
   └────────────────────────────────────► FlashKit.Core
```

- **FlashKit.Core**：不引 UI、不引 WinUI、不启动进程。放模型、接口、纯文本解析器、镜像格式识别。
- **FlashKit.Services**：外部世界适配层。所有进程、ADB/Fastboot、设备枚举、分区、解包/打包、日志都在这里。`net8.0-windows` 因为需要 WMI 与 Windows 文件语义。
- **FlashKit.App**：页面、ViewModel、主题资源、独立窗口。页面只操作 ViewModel / 服务接口，不拼 shell 命令。
- **FlashKit.Tests**：对 Core 解析器、Services 的 payload/ZIP/校验/ProcessRunner 做自动化验证。

### 目录树

```
FlashKit.sln
Directory.Build.props
README.md
docs/
  ARCHITECTURE.md
src/
  FlashKit.Core/
    Abstractions/Interfaces.cs
    Models/
      AndroidDevice.cs / DeviceInfo.cs / Enums.cs
      FlashModels.cs / PackageModels.cs / PartitionInfo.cs
      BackupModels.cs / ProcessModels.cs / DiagnosticsModels.cs / SparseImageInfo.cs
    Parsing/
      DeviceOutputParser.cs / PartitionMatcher.cs / CommandLineTokenizer.cs
  FlashKit.Services/
    Process/ProcessRunner.cs
    Logging/LoggingService.cs
    Tools/ToolchainService.cs
    Adb/AdbClient.cs
    Fastboot/FastbootClient.cs
    Devices/DeviceManager.cs
    Devices/DeviceInfoProvider.cs
    Partitions/PartitionCatalogService.cs
    Flash/FlashValidationService.cs
    Flash/FlashService.cs
    Backup/BackupService.cs
    Packages/PackageExtractor.cs
    Packages/ZipImagesExtractor.cs
    Packages/PackagePacker.cs  (门面 + ZipImagesPacker 实现)
    Packages/DatBrExtractor.cs
    Payload/PayloadModels.cs
    Payload/PayloadManifestParser.cs
    Payload/PayloadExtractor.cs
    Payload/SubStream.cs
    Diagnostics/DriverDiagnosticsService.cs
    Terminal/TerminalCommandService.cs
    Templates/update-binary.template
    Templates/updater-script.template
  FlashKit.App/
    App.xaml / App.xaml.cs
    MainWindow.xaml / MainWindow.xaml.cs
    Views/
      DevicePage / BackupPage / FlashPage / PackagePage / SettingsPage / DriverGuidePage
      TerminalWindow / TerminalPage / LogWindow / LogPage
    ViewModels/
      MainViewModel / DeviceViewModel / BackupViewModel / FlashViewModel / PackageViewModel
      SettingsViewModel / TerminalViewModel / LogViewModel
      BackupPartitionViewModel / FlashTaskViewModel / ObservableObject.cs
    Services/
      WindowManager.cs / AppSessionState.cs
    Localization/Strings.cs      # UI 文案与 LocExtension
    Converters/Converters.cs
    Assets/AppIcon.*
    tools/platform-tools/        # adb.exe / fastboot.exe / DLL / NOTICE
tests/
  FlashKit.Tests/
    ParserTests.cs
    PackageTests.cs
    FlashValidationTests.cs
    FlashServiceTests.cs
    BackupTests.cs
    ProcessRunnerTests.cs
```

## 4. 关键类职责

### 4.1 ProcessRunner（唯一进程入口）

`IProcessRunner` 的公开操作：

- `RunAsync(ProcessRequest, onOutput, ct)`
- `RunToFileAsync(ProcessRequest, outputPath, onBytes, onOutput, ct)`：stdout 原始字节写文件（用于 `adb exec-out cat` 分区回传），stderr 仍作为文本
- `OpenFolderAsync`
- `OpenUrlAsync`

`ProcessRequest` 用 `ArgumentList` 表达参数，不经过 `cmd.exe` / PowerShell。Runner 负责：

- 校验可执行文件和工作目录存在
- UTF-8 stdout/stderr 并行流式读取
- 超时（linked CTS）和用户取消
- 超时/取消时 `Kill(entireProcessTree: true)`
- 捕获 `ExitCode`、stdout、stderr、耗时、StartError；`RunToFileAsync` 的 stdout 不进入文本缓冲，直接逐字节写入文件
- 写入全局命令日志（可关闭轮询类日志）

这避免了 UI 层散落 `Process.Start`，也让日志格式统一。`OpenFolderAsync/OpenUrlAsync` 也集中在这里，属于“外部进程”边界。

### 4.2 LoggingService

线程安全的环形列表（最多 10,000 条），提供 `Snapshot()` 与 `EntryAdded/Cleared` 事件。UI 在 DispatcherQueue 中维护自己的 `ObservableCollection`，服务本身不依赖 WinUI。

每条命令日志包含：时间、类别、完整命令行、退出码、stdout、stderr、耗时、超时/取消标志。轮询 `adb devices` / `fastboot devices` 不写入历史，避免日志刷屏。

### 4.3 ToolchainService

- `ToolsDirectory = AppContext.BaseDirectory/tools/platform-tools`
- `AdbPath` / `FastbootPath` 由目录拼接
- 启动时执行 `adb version`、`fastboot --version`，展示在设置页
- 缺失文件时返回报告而不是崩溃

### 4.4 AdbClient / FastbootClient

对参数做 `-s <serial>` 注入，统一超时和日志；提供 devices、shell、getprop、push/pull、reboot、flash、getvar、is-userspace 等操作。`ExecuteRawAsync` 只接受构造好的参数数组，不接收整串 shell。

### 4.5 DeviceManager

- 每 3 秒轮询 `adb devices -l` 与 `fastboot devices`
- 根据 `adb devices` 状态区分 `system / recovery / sideload / unauthorized / offline`
- Fastboot 存在时覆盖同序列号状态
- 单设备时自动选中；**多设备不默认选第一台**
- 切换模式后保留序列号，设备暂时消失时标题栏显示“已断开”
- 提供重启到 Fastboot / Fastbootd / 系统，以及等待设备重新出现

事件在后台线程触发，ViewModel 统一通过 DispatcherQueue 回到 UI 线程。

### 4.6 DeviceInfoProvider

系统模式下并行执行：

- `dumpsys battery`
- `cat /proc/meminfo`
- `df -k`
- `getprop`

每个命令失败只产生 warning，不影响其他字段。所有解析器均容忍空输出、缺字段、超范围数值。Fastboot / Recovery / Sideload 不执行命令，直接返回 `IsAvailable=false` 和可读原因。

`df -k` 表头为 `1K-blocks` 时数字按 KiB 换算；`df -h` 带 K/M/G 后缀时直接换算。挂载点优先 `/data`，其次 `/storage/emulated`、`/sdcard`、`/`。

### 4.7 PartitionCatalogService

**禁止硬编码分区。** 两个真实来源：

- 系统：`adb shell ls /dev/block/by-name`，失败时回退 `ls /dev/block/platform/*/by-name`、`ls /dev/block/mapper`
- Fastboot：`fastboot getvar all` 中的 `partition-size:*`

解析时：
- 保留实际分区名（含 `_a/_b`）
- 计算风险等级（persist/modem/efs/xbl 等）
- 标记动态分区（system/vendor/product 等）
- 缓存 30 秒；同一序列号两种模式都读取过时做交叉校验，差异写入 warning

Fastboot 模式下同时读取 `is-userspace`、`product`、`current-slot`。

### 4.8 FlashValidationService / FlashService

V1.1 起改为**按目标分区名直接刷入**，`FlashService` 对每个条目执行：

```
fastboot flash <item.PartitionName> <item.FilePath>
```

目标分区名由文件名推导（`boot.img → boot`），用户可在手动来源或扫描结果、以及待刷清单中逐条修改，修改后的值原样传给 fastboot。分区目录/getvar 仅用于可选提示。

真正阻断的只有：

1. 文件不存在、不可读、为空、sparse 头损坏
2. 目标分区名为空或含非法字符（字母/数字/点/下划线/短横线）
3. 清单内目标分区重复（按去 slot 后缀比较）
4. 设备不在 Fastboot / Fastbootd

以下全部降级为 warning，不再拦截：

- 分区不在当前目录
- 分区容量未知或镜像超过目录容量
- 动态分区但当前是 bootloader
- 设备代号不一致或无法读取

`FlashService` 按清单顺序逐项调用 `fastboot flash`；失败默认中止并标记剩余项，`ContinueOnFailure` 时继续。每一项返回状态、退出码、耗时，写入日志。UI 在 `AppSessionState.IsFlashing` 中锁定导航，并保持任务清单只读。

### 4.9 BackupEnvironmentService / BackupService / RestoreService

分区备份使用 **系统 KSU Root** 或 **TWRP**，不使用 fastboot fetch：

1. `BackupEnvironmentService` 先看 `DeviceMode`：system 检查 `adb shell id`、`adb root`、`su -c id`；recovery 必须 `getprop ro.twrp.version` 非空；sideload/官方 Recovery/fastboot 明确拒绝。
2. 用一次 shell 脚本枚举 `/dev/block/by-name`、`/dev/block/bootdevice/by-name`、`/dev/block/platform/*/by-name`，（TWRP）`/dev/block/mapper`，并用 `blockdev --getsize64` 取大小。
3. `BackupService` 只对实际勾选集合执行：
   `adb exec-out dd if=<block> bs=4M conv=noerror,sync`，通过 `ProcessRunner.RunToFileAsync` 将 stdout 原样写入本地文件。
4. toybox `conv=sync` 会把最后一个块补齐到 4M；完成后按 `blockdev` 容量截断，再校验/写 manifest。
5. 读取长度不足或 dd 退出码非 0 时判定失败并删除半成品。
6. `RestoreService` 从 manifest 解析镜像，校验来源型号/序列号与文件大小；系统/TWRP 用 `adb push + dd if=... of=... bs=4M`，Fastboot 用 `fastboot flash`；默认失败即停。

`BackupService` 的其余行为：

- 默认不勾选，UI 的“开始备份”由 `CanStartBackup` 控制（必须可用 Root + 支持的环境）
- 空间校验：对已勾选且已知大小的分区求和，与目标盘可用空间比较；未知大小给 warning
- 输出 `<型号>_<序列号>_<时间>/images/<partition>.img` 与 `manifest.json`；未勾选分区不创建文件
- `manifest.json` 记录 `mode`（boot-root/twrp）、`isSelective=true`、`images`、`notBackedUpPartitions`、`coveredBySuperPartitions`、`sourceBlockPath`、`androidRelease`、`twrpVersion`、`ddUsedNoErrorSync`、失败项和 warning
- 勾选 `super` 时，同组逻辑分区标记 `已包含于 super 备份`，不重复读取；同批勾选的普通/高风险分区仍会备份；只有 super 成功才把逻辑分区从“未备份”中移除
- 备份/还原执行入口均接入统一任务队列；任务历史持久化到 `%LOCALAPPDATA%\FlashKit\tasks.json`
- 完成摘要按 `已备份 N 个分区，未包含 M 个分区` 计算

`BackupPage` 按常规 / super 整体与内逻辑 / 高风险三组平铺；高风险组使用单个默认折叠的 `Expander`（这是唯一保留的分组折叠，需求明确要求默认折叠）。选择、模式、目录、开始操作都在一级页面完成，开始前只弹一次汇总确认。Fastboot 模式下页面只提示“重启到 Recovery”，不提供读取分区按钮的可用路径。

### 4.10 Payload 解包

`PayloadExtractor`：

1. 读取 24 字节 payload 头部：`CrAU` + version + manifest_size + metadata_signature_size
2. 读取并手工解析 protobuf manifest（只覆盖需要的字段）
3. 检测真正依赖源包的操作（SOURCE_* / BSDIFF / PUFFDIFF / ZUCCHINI / MOVE 等）→ 抛 `IncrementalOtaException`；仅 minor/partial 标记但全部为 REPLACE 时允许解包并写 warning
4. 计算每个分区目标大小（max dst extent × block_size），预分配文件
5. 每个操作：
   - `REPLACE`：原始数据流
   - `REPLACE_BZ`：SharpCompress BZip2Stream
   - `REPLACE_XZ`：SharpCompress XZStream
   - `ZERO`/`DISCARD`：分块写零
6. 所有数据段在写之前校验不越过文件末尾
7. 按字节报告进度，支持取消
8. 输出 `images/<partition>.img`

注意：
- 只支持不依赖源包的 REPLACE 系列（含被标为 partial 但自包含的包）；真正依赖源包的增量操作明确报错
- `REPLACE_ZSTD` 等未知/新增操作明确报错
- 不验证签名
- 优先使用 `new_partition_info.size`，缺失时回退到最大 dst extent；落盘后按 `new_partition_info.hash` 校验

### 4.11 PackageExtractor / PackagePacker

`PackageExtractor` 负责格式分派：

- ZIP 内含 payload.bin → 流式提取 payload 到 `%TEMP%`，保留 META-INF，再解 payload
- 纯 img ZIP → `images/` 拍平，文件名重名自动加后缀，保留 META-INF
- `.dat.br/.new.dat` → `DatBrExtractor` 预留接口，明确 NotSupported

输出固定结构，`manifest.json` 使用 camelCase、缩进 JSON，记录工具版本、源包信息、分区映射、原始操作类型、warning。

`PackagePacker` 是格式门面，首版委托给 `ZipImagesPacker`，只做纯 img ZIP：

- 源图片复制到 `%TEMP%/FlashKit/pack-<guid>` 暂存目录
- ZIP 根级 `<partition>.img`，`CompressionLevel.NoCompression`（Store）
- `META-INF/com/google/android/update-binary` 与 `updater-script` 用模板动态生成，Deflate
- 结构校验：脚本存在、引用文件存在、没有多余文件
- 输出先写同目录隐藏临时 ZIP，再 `File.Move(overwrite: true)`，避免半成品；img 设置 0100644、update-binary 设置 0100755 的 ZIP Unix 权限
- 清理暂存目录

模板是通用的 shell/edify 说明性模板，不保证所有 recovery 可执行；README 和 UI 都明确提示“结构正确不等于设备一定能刷”。

### 4.12 TerminalCommandService / WindowManager

终端先 tokenize，只接受 `adb` / `fastboot` 前缀，抽取 `-s` 后调用对应 Client，不开放任意系统命令。非法前缀直接可读错误。

`WindowManager` 位于 App/Services 而非 Services 项目，因为它必须引用 WinUI Window 类型；职责仍是服务层：

- TerminalWindow / LogWindow 单例
- 已打开则恢复、激活、置顶
- 关闭后清空引用，可再次打开
- 主窗口关闭时统一关闭子窗口

### 4.13 UI / MVVM

- 主窗口：`TitleBar` 自定义标题栏 + `NavigationView`（Left，无分组小标题） + `Frame`；宽度 <1000px 自动收起为 104px 图标栏
- 标题栏常驻：应用名、序列号、模式、连接状态、刷写中徽标、驱动警告
- PaneFooter：设置 / 终端 / 原始日志三个横向图标按钮；CompactPaneLength=112 保证折叠时仍能放下
- 设置不是 NavigationView 内置项（`IsSettingsVisible=False`），而是 PaneFooter 第一个按钮
- 页面根容器透明；内容卡片用 `CardBackgroundFillColorDefaultBrush` 等主题资源
- BackupPage / FlashPage / LogPage 设置 `NavigationCacheMode="Required"`
- BackupPage / FlashPage / PackagePage 的耗时操作都在后台任务中，UI 有进度和取消
- 分区备份与刷入的选择、模式切换、文件选择、开始执行都放在一级页面；不使用多级 Tab / 抽屉，只有高风险备份组按需求使用一个默认折叠的 Expander
- 危险操作（刷入、备份）均使用单次汇总确认；执行结果用 InfoBar 或单个结果窗口展示
- 主窗口通过运行时尺寸约束保证最小 800×620

## 5. 关键设计决策

### 5.1 为什么选非 MSIX

需求要求执行内置 adb.exe / fastboot.exe。非 MSIX 直接部署时，`AppContext.BaseDirectory/tools/platform-tools` 可读可执行，路径确定，不需要处理包身份、虚拟化和只读安装目录。因此选择：

```xml
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
```

### 5.2 如果未来改 MSIX

MSIX 方案不是不可行，但需要额外步骤：

1. 首次启动把 `tools/platform-tools/*` 从安装目录复制到
   `%LOCALAPPDATA%\FlashKit\tools\platform-tools\<appVersion>`。
2. 复制前校验内置文件哈希/签名，复制后设置文件属性，避免被替换。
3. 所有 ToolchainService 路径改为该可写目录；安装目录只作为只读源。
4. 升级版本时清理旧版本 tools 目录，或按版本并存。
5. adb server 端口、WORKSPACE 临时目录放到 LocalAppData。

当前版本不采用 MSIX，因此没有这条释放逻辑，但 ToolchainService 已把路径集中，未来替换成本低。

### 5.3 为什么分区目录不缓存成静态清单

不同机型、slot、动态分区布局差异巨大。静态清单不仅容易误导，还会让“大小校验”失效。每次进入页面/刷入前都从真实设备读取；缓存仅 30 秒，且用于交叉校验而非唯一来源。

### 5.4 为什么 payload 解包先算大小再写

payload 的 data_offset 是相对于数据区（头部+manifest+metadata signature 之后）的偏移。先验证所有 data 段范围与目标 extents，可以在写盘前拦截截断包、恶意大小和越界操作，避免留下大量半成品。最终 manifest 写入后，失败会删除本次创建的输出目录。

### 5.5 线程与取消

- 所有进程调用异步，ProcessRunner 负责杀进程树
- 所有大文件复制/解压按 1 MiB 分块并检查 CancellationToken
- UI 进度通过 `IProgress<T>` 回到 UI 线程
- DeviceManager 事件、LoggingService 事件由 ViewModel 显式 DispatcherQueue
- 刷写 CTS 独立于页面生命周期；导航锁定避免页面被切走

### 5.6 字符串集中管理

所有 XAML 中的静态 UI 文案替换为 `{loc:Loc Key=...}`，集中存放在 `Localization/Strings.cs` 的 `ZhCn` 字典中；`LocExtension` 是 WinUI MarkupExtension。后续接入 `.resw` / `ResourceLoader` 时只需替换 `Strings.Get`，XAML 不必逐页修改。

服务层诊断/异常文本目前仍以中文可读消息写在服务里（便于日志定位），后续可平移到独立资源类。这是首版已知的局部债务。


### 5.7 unpackaged publish 的 XBF/PRI 处理

Windows App SDK 2.6.4 的 unpackaged `dotnet publish` 不会自动把 XAML 编译器生成的 `.xbf` 与 `<Assembly>.pri` 复制到 publish 目录，运行时会在 `ms-appx:///MainWindow.xaml` 处抛 XamlParseException。`FlashKit.App.csproj` 中增加了 `CopyWinUIXamlResourcesToPublish` target，在 `AfterTargets="Publish"` 时复制这些资源。直接 `dotnet build` 不受影响；修改发布目标时不要删除该 target。
## 6. 分阶段交付对应

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 1 | 解决方案骨架、Mica 主窗口、NavigationView、PaneFooter、独立窗口管理 | 完成 |
| 2 | ProcessRunner、日志、ADB/Fastboot、DeviceManager、工具自检 | 完成 |
| 3 | 设备信息、容错解析、驱动诊断、驱动引导页 | 完成 |
| 4 | Fastboot 双模式刷入、分区目录、校验、执行、终端、原始日志 | 完成 |
| 5 | payload/ZIP 解包、ZIP 打包、manifest、进度与取消 | 完成 |
| 6 | 内置 platform-tools、编译/运行/自动化测试、README | 完成 |

## 7. 扩展点

- 新增 payload 操作类型：`PayloadOperationType` + `PayloadExtractor` switch；ZSTD 可引入解压库
- 新增解包格式：`PackageFormat` + `PackageExtractor.DetectFormatAsync` 分派
- 新增 packer：实现 `IPackagePacker`，UI 通过接口注入
- 新增终端命令域：扩展 `TerminalCommandService` 白名单与解析
- 新增驱动检测源：替换 `IDriverDiagnosticsService`
- 多语言：替换 `Localization.Strings.Get` 与 UI 资源；服务层文本逐步资源化
- 设备自动发现：DeviceManager 已做事件化，可改成 `adb track-devices`

## 8. 验证状态

- `dotnet build FlashKit.sln -c Debug`：通过（0 warning / 0 error）
- `dotnet test tests/FlashKit.Tests`：43 个测试通过（含 Recovery ADB 备份、二进制回传、super 覆盖、直接按分区名刷入）
- 应用启动、主窗口、设备页、Fastboot 页、卡刷包页、设置页、驱动引导页、终端窗口、日志窗口均做过 UI 启动/截图检查
- 自动化测试覆盖构造 payload（REPLACE/BZ/XZ/ZERO）与 ZIP；另用真实 ColorOS 7.28GB OTA 验证了 47 个分区的 SHA256，全部一致
- 真实 boot/dtbo 打包、ZIP 回环解包和 UI 解包/打包流程已验证，详见 `docs/VALIDATION.md`
- 未在真实安卓设备上执行 flash；实际刷机前必须在目标机型上验证 Fastbootd、分区名、驱动与 recovery











