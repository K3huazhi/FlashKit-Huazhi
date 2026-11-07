# FlashKit V2.2 新增模块实现说明

> 设备实测：Redmi K70 / `vermeer` / `23113RKC6C` / `<device-serial>`，Android 16，KernelSU root（`u:r:ksu:s0`）。
> 所有设备写入操作在测试前都核对分区名与镜像来源；本次实际写设备仅做了同镜像回写验证前的读取/哈希检查。

## 1. 分层与目录

```
src/FlashKit.Core/
  Abstractions/Interfaces.cs          ITaskQueueService / IBackupEnvironmentService
  Models/TaskQueueModels.cs           OperationTask* / TaskExecutionContext
  Models/ImageToolModels.cs           ImageInspection / ImageHashResult / IImageToolService
  Models/BackupModels.cs              BackupTransport / BackupEnvironmentInfo / manifest 扩展
  Models/RestoreModels.cs             RestoreRequest / RestoreResult / IRestoreService
  Models/FileManagerModels.cs         DeviceFileEntry / IDeviceFileService
src/FlashKit.Services/
  Tasks/TaskQueueService.cs           串行队列 + JSON 历史
  Images/SparseImageConverter.cs      sparse ↔ raw，RAW/FILL/DONT_CARE/CRC chunk
  Images/ImageToolService.cs          inspect / hash / convert / payload
  Backup/BackupEnvironmentService.cs  KSU / TWRP / 官方 Recovery / sideload / fastboot 探测
  Backup/BackupService.cs             dd conv=noerror,sync + 容量截断 + manifest
  Backup/RestoreService.cs            manifest 校验 + adb push/dd + fastboot flash
  Files/DeviceFileService.cs          stat 目录 / push / pull / 写路径保护
src/FlashKit.App/
  ViewModels/TaskQueueViewModel.cs
  ViewModels/ImageToolsViewModel.cs
  ViewModels/RestoreViewModel.cs
  ViewModels/FileManagerViewModel.cs
  Views/TaskQueuePage.xaml
  Views/ImageToolsPage.xaml
  Views/RestorePage.xaml
  Views/FileManagerPage.xaml
```

现有 `FlashViewModel` / `PackageViewModel` 的刷入、解包、打包调用已改为 `ITaskQueueService.RunAsync`，旧进度模型保留为 UI 展示，但执行入口统一进队列。

## 2. 阶段一：任务队列与历史

- `TaskQueueService` 使用 `SemaphoreSlim(1,1)` 串行化；同一时间只运行一个任务（比“同设备串行”更强）。
- 记录类型、设备序列号、输入摘要、输出路径、状态、开始/结束、耗时、错误、日志引用。
- `%LOCALAPPDATA%\FlashKit\tasks.json` 原子写入；最多 500 条；启动时把上次未完成 `Running` 标为失败。
- 排队任务可取消；运行中任务通过 linked token 取消。
- 页面：列表 + 类型/状态/时间/设备筛选 + 清除已完成；不做二级 Tab。
- 测试：`TaskQueueTests` 覆盖串行、持久化、排队取消。

## 3. 阶段二：镜像工具

- `SparseImageConverter` 纯 C# 流式处理，不整包读入内存；支持 sparse RAW / FILL / DON'T CARE / CRC32 chunk，处理 CRC header。
- `ImageToolService` 识别 raw / sparse / ext4 / f2fs / erofs / payload；流式 MD5/SHA256；payload 通过已有 `PayloadExtractor` 解出 `images/` 与 manifest。
- 输出先写 `.tmp`，成功后原子替换；源目录不散落中间产物。
- 测试：`ImageToolsTests` 覆盖 1MB+ 随机数据 sparse→raw 回环、哈希比对。

## 4. 阶段三：备份与还原

### 环境探测

| 当前状态 | 行为 |
| --- | --- |
| system | `adb shell id` 为 root，或 `adb root` / `su -c id` 成功；否则提示改用 TWRP |
| recovery | 必须 `getprop ro.twrp.version` 非空，否则判定官方 Recovery 并终止 |
| sideload | 直接拒绝 |
| unauthorized | 提示授权 |
| fastboot | 不读取分区，提示切到系统/TWRP |

### dd 读取

规范命令：

```text
adb exec-out dd if=<block> bs=4M conv=noerror,sync
```

toybox dd 的 `conv=sync` 会把最后一个块补齐到 4M，部分 TWRP 还会把 `records in/out` 写到 stdout。V2.2 在传输完成后按 `blockdev --getsize64` **截断到实际分区容量**；长度不足才判失败。manifest 记录 `ddUsedNoErrorSync=true` 和真实 `sourceBlockPath`。

输出：

```text
<型号>_<序列号>_<日期时间>/
  images/
    <分区>.img
  manifest.json
```

实测 KSU 备份 `ALIGN_TO_128K_1`：

- `blockdev`：106,496 字节
- 本地文件：106,496 字节
- SHA256：`caa961e702d561d3245d06bf54fb5fe35bf75037032d764ec11fcb5ac1d41c1c`
- 设备端 `sha256sum`：完全一致
- manifest：`mode=boot-root`、`androidRelease=16`、`isSuperWholeBackup=false`

### 还原

- `RestoreService` 校验 manifest、镜像存在、镜像大小与 manifest 一致、镜像路径不越界。
- 序列号不一致/型号不一致给强警告；跨型号必须显式勾选，否则拒绝。
- 系统/KSU 与 TWRP：`adb push` 到 `/data/local/tmp/flashkit-restore-<id>`，再 `dd if=<remote> of=<block> bs=4M`，逐项退出码；默认失败即停。
- Fastboot：`fastboot flash <partition> <image>`；动态分区提示 fastbootd。
- UI 单次汇总确认；高风险分区标红；不串联多个弹窗。
- 实测：使用上述 `<backup-dir>` 的 `ALIGN_TO_128K_1.img` 回写同一分区；服务返回 1 成功/0 失败，回写后设备端 SHA256 仍为 `caa961e...1d41c1c`，设备仍为 `vermeer`/KSU root，未出现异常。
- 测试通过 `RealDeviceProbeTests.RealDevice_Restore_SameImage` 执行；真实写入仅在显式设置 `FLASHKIT_ALLOW_WRITE=1` 时发生。

## 5. 阶段四：文件管理

- `stat -c '%A|%U|%G|%s|%Y|%n'` 读取名称、权限、UID/GID、大小、时间。
- push/pull 走 `IAdbClient` + `ProcessRunner`，接入任务队列；目录递归由 adb 自身处理。
- 写路径校验拒绝 `/dev`、`/proc`、`/sys`、`/system`、`/vendor`、`/product`、`/odm`、`/metadata`。
- 实测：K70 `/sdcard` 列出 18 项（目录优先）；`/data/local/tmp` 上传、下载回读、删除探针文件均成功，`RealDeviceProbeTests.RealDevice_FileManager_ListAndPull` 通过。

## 6. 阶段五：文件系统解包/打包（内容级完成，元数据未完全恢复）

V2.2 的 `FileSystemPage` / `IFileSystemService` 已接入任务队列，三种格式都具备内容级解包和回打包路径：

| 能力 | 实现路径 | 实测 |
| --- | --- | --- |
| ext4 解包 | 内置 7-Zip 只读展开到 `files/`；`7z l -slt` 记录 mode/UID/GID；删除 `[SYS]` | 通过（64MB 测试镜像） |
| ext4 回打包 | 设备端 `mke2fs -d <src> <img>` | 通过（64MB，内容回读） |
| f2fs 解包 | 设备端 `dump.f2fs -r -P -o <out>` | 通过（64MB） |
| f2fs 回打包 | `make_f2fs -f` + `sload_f2fs -f <src> -P` | 通过（64MB） |
| erofs 解包 | 设备端 `fsck.erofs --extract`（xattr 不可用时回退 `--no-xattrs`） | 通过（102,400 字节） |
| erofs 回打包 | 设备端 `mkfs.erofs -b 4096 -T 0 --all-time` | 通过（回读校验） |
| sparse/raw/payload 封装层 | ImageToolService（sparse 先转 raw） | 通过 |

新增内置 `tools/7zip/7z.exe` + `7z.dll`（含 License）；所有 7-Zip 调用走 `ProcessRunner`，不是散落的 `Process.Start`。

**未达到的硬要求**：SELinux context、capabilities、完整 `fs_config` / `file_contexts` 回填尚未完成。当前：

- erofs 在 `/data/local/tmp` 不支持 `security.selinux` xattr，自动回退并记录警告；
- ext4 的 7-Zip 解包已记录 mode/UID/GID，但**不保留 SELinux/capabilities**；
- f2fs `dump.f2fs -P` 保留 mode/owner/group，但不采集 SELinux/capabilities；
- 回打包虽然会尝试把 `metadata.jsonl` 的 mode/UID/GID 应用到设备源树，但没有 SELinux/capabilities；实际元数据完整恢复尚未通过开机回归验证。

因此当前是“结构/内容级可解包、可回打包”，不是“开机级元数据完整恢复”。要闭环仍需：

1. 安装 WSL2 Ubuntu + `e2fsprogs f2fs-tools erofs-utils` 或等价 Linux 工具链；
2. 采集/回填 `fs_config`、`file_contexts`、capabilities、symlink；
3. 用真实可启动分区做回打包回归。

`payload.bin` 回打包、`.dat.br` 打包、官方 Recovery 分区读取仍明确不做。

## 7. 自动化与实际验证

- `dotnet test`：**43/43 通过**（任务队列、sparse/raw 回环、备份长度截断、真实设备探针、ext4/f2fs/erofs 打包/解包探针）。
- 真实设备探针默认跳过；仅在显式设置 `FLASHKIT_ALLOW_WRITE=1`、`FLASHKIT_REAL_SERIAL`、`FLASHKIT_REAL_ADB` 时执行。
- 设备实测结果见 `docs/VALIDATION.md`。

## 8. 安全与已知限制

- 所有外部命令经过 `ProcessRunner`；UI 不拼接 shell。
- 危险操作前汇总确认；执行中锁定导航（现有 `AppSessionState`）。
- 未测试 15GB `super` 全量备份/还原；大文件耗时与磁盘空间需另测。
- 未测试 AVB/dm-verity 校验失败后的救砖流程；还原前 UI 明确提示。
- 任务队列的“同一设备”当前实现为全局串行，属于更保守的行为。
- 阶段五内容级完成，但元数据恢复不完整；不应把当前 V2.2 说成“开机级完整还原完成”。



