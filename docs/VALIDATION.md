# 验证记录

## 环境

- Windows 11 Build 26300（当前测试机）
- .NET SDK 8.0.427 / 运行时 8.0.33
- Windows App SDK 2.6.4
- 内置 Google platform-tools 37.0.1
- 测试包：
  `D:\PhoneBackup\PJA110_xigua\ColorOS PJA110_13.1.1.552(CN01) A.20\891a80780997458cb5deaf8e6e963453.zip`
  复制为 `samples/PJA110_OTA.zip`，大小 7,814,986,791 字节

## 真实 ColorOS OTA 解包

使用 `tools/FlashKit.PackageProbe` 调用与 UI 相同的 `PackageExtractor`：

- 输入：7.28 GiB ZIP，内含 stored `payload.bin` 7,814,981,223 字节
- payload manifest：47 个分区，5343 个安装操作
- 操作类型统计：`REPLACE=446`、`REPLACE_BZ=92`、`ZERO=237`、`REPLACE_XZ=4568`
- `minor_version=7`、`partial_update=true`，但所有操作均为自包含 REPLACE，不依赖源包
- 输出：`samples/PJA110_OTA/images/` 47 个 raw img，共 **10.38 GiB**
- 耗时：约 **19 分 17 秒**（单线程 SharpCompress XZ 解压为主）
- `manifest.json` 正常生成，META-INF 从源 ZIP 保留
- **47/47 个镜像的 SHA256 与 payload manifest `new_partition_info.hash` 完全一致，大小也一致**

这说明本工具能正确解出该 ColorOS 全量/部分自包含 payload，包括 XZ、BZip2、raw、ZERO 混合操作。

### 修复点

原逻辑把 `minor_version != 0` 或 `partial_update=true` 直接判为“增量 OTA”并拒绝。该 ColorOS 包虽然是 partial 标记，但所有操作都是 REPLACE，可以独立解出。现在改为：

- 检测到 `SOURCE_COPY` / `SOURCE_BSDIFF` / `BROTLI_BSDIFF` / `PUFFDIFF` / `ZUCCHINI` / `MOVE` / `BSDIFF` 等真正依赖源包的操作才抛 `IncrementalOtaException`
- minor/partial 但全部为自包含 REPLACE 时允许解包，并写入 warning，提示未包含的分区仍需源包
- 新增 `new_partition_info` 的 size/hash 解析：输出文件按声明大小补齐，并在每个镜像写完后校验 SHA256
- ZIP 内 payload.bin 的提取进度从每 1 MiB 上报一次改为每 0.1% 一次，避免 7GB 包产生数千条 UI 进度消息

## 真实镜像打包

使用从上述 OTA 解出的真实镜像：

- 输入：`boot.img` (201,326,592 B)、`dtbo.img` (25,165,824 B)
- 输出：`samples/packed_boot_dtbo.zip`，226,494,299 B
- ZIP 结构：
  - 根级 `boot.img`、`dtbo.img`，Store（compressed size == uncompressed size）
  - `META-INF/com/google/android/update-binary`，Deflate，外部属性 `0100755`
  - `META-INF/com/google/android/updater-script`，Deflate，外部属性 `0100644`
  - 无多余文件
- `updater-script` 动态包含：
  `package_extract_file("boot.img", "/dev/block/by-name/boot");`
  `package_extract_file("dtbo.img", "/dev/block/by-name/dtbo");`
- 两个 img 的 SHA256 与源文件一致
- 使用本工具再次解包该 ZIP，输出两个 img，SHA256 与源文件一致
- UI 中已实际走通解包：选择卡刷包 → 输出目录已存在提示 → 删除并重新解包 → 2 个镜像完成
- UI 中已实际走通打包：添加 boot.img / dtbo.img → 选择输出路径 → 开始打包 → 生成 226,494,303 B 的 `ui_packed2.zip`
- 修复“另存为”会预创建 0 字节文件、导致新文件也弹出覆盖确认的问题；0 字节预创建文件按新文件处理

> 注意：ZIP 结构正确不代表所有 recovery 都能执行。`update-binary` 是通用 shell 模板，依赖 recovery 提供 `unzip`/`busybox`、`dd` 与 `by-name` 分区节点；实际刷入前仍应在目标机型验证。

## 真实 TWRP 分区备份（2026-10-30）

设备：PJA110 / `<twrp-serial>`，TWRP Recovery，`adb shell id` 返回 `uid=0(root)`。

- Recovery 枚举到 **164 个分区**，容量由 `blockdev --getsize64` 读取成功
- 逻辑分区通过 `/dev/block/mapper/*` 可见（如 `my_carrier_a`、`system_a`）
- 选择 `ALIGN_TO_128K_1`（104 KB）和 `boot`（192 MB）实际备份成功
- 完成摘要：`已备份 2 个分区，未包含 162 个分区`
- 实际输出：`<backup-dir>`
- 本地文件大小与 `blockdev` 容量一致
- SHA256 与设备端 `sha256sum` 完全一致：
  - `ALIGN_TO_128K_1.img`：`caa961e702d561d3245d06bf54fb5fe35bf75037032d764ec11fcb5ac1d41c1c`
  - `boot.img`：`a0128b0c067453151b9672c5fbacc92cf910c3e1f3c91155e7d219fb9acdfa17`
- 直接对 mapper 逻辑分区 `my_carrier_a` 执行 `adb exec-out cat`，本地 SHA256 与设备端一致

### 失败根因与修复

第一次实际备份 `apdp` 失败：文件比 `blockdev` 容量多 79 字节，内容尾部是：

```text
0+1 records in
0+1 records out
262144 bytes (256 K) copied...
```

TWRP/toybox 的 `dd` 把状态写到了 **stdout**，`adb exec-out` 把这些文本一起写进了镜像，导致长度校验失败。V1.3 改为：

```text
adb exec-out cat <block-device>
```

`cat` 只输出块设备原始数据。修复后 `ALIGN_TO_128K_1`、`boot` 均备份成功且哈希一致。

> 尚未测试 15GB 的 super 全量备份、长时间传输中断和恢复流程；这些需要更多磁盘空间与时间。

## 真实 K70 / Android 16 / KSU（V2.2，2026-10-30）

设备实测（不盲信声明）：

```text
ro.build.version.release = 16
ro.product.device        = vermeer
ro.product.model         = 23113RKC6C
serial                   = <device-serial>
adb shell id             = uid=0(root) ... context=u:r:ksu:s0
```

- 环境探测：**开机 Root / KSU**，枚举 **133 个分区**，容量由 `blockdev` 读取成功
- 备份 `ALIGN_TO_128K_1`：
  - 输出目录：`<backup-dir>`
  - 实际文件：`images/ALIGN_TO_128K_1.img`，106,496 字节
  - 本地 SHA256 与设备端 `sha256sum` 一致：`caa961e702d561d3245d06bf54fb5fe35bf75037032d764ec11fcb5ac1d41c1c`
  - manifest：`mode=boot-root`、`ddUsedNoErrorSync=true`、`androidRelease=16`、`isSuperWholeBackup=false`
- dd 行为：K70 toybox 的 `dd ... bs=4M conv=noerror,sync` 会把块补齐到 4 MiB（104 KB 分区实测输出 4,194,304 字节）；服务按 `blockdev` 容量截断到 106,496 字节后才校验/落 manifest。
- 还原：使用上述同一镜像回写同一分区，服务返回 `已还原 1 个分区，失败 0 个`；回写后设备端 SHA256 仍为 `caa961e...1d41c1c`，设备保持 `vermeer` + KSU root。
- 文件管理：`/sdcard` 列出 18 项；`/data/local/tmp` 上传、下载回读、删除探针文件成功。
- 文件系统：
  - erofs：`system_dlkm`（102,400 字节）经设备端 `fsck.erofs --extract` 解出 `etc/`、`lib/`；`security.selinux` xattr 回退警告已记录。
  - f2fs：64MB 测试镜像经 `make_f2fs + sload_f2fs` 打包、`dump.f2fs -r -P` 解包，测试通过。
  - ext4：64MB 测试镜像经 `mke2fs -d` 打包、内置 7-Zip 解包，`etc/probe.txt` 回读成功；`7z l -slt` 同时记录 mode/UID/GID 到 `metadata.jsonl`，SELinux/capabilities 仍未保留。
  - erofs：`mkfs.erofs` 打包后回读，测试通过。
- 自动化测试：**43/43 通过**；真实设备探针仅在显式设置 `FLASHKIT_ALLOW_WRITE=1` + `FLASHKIT_REAL_SERIAL` 时执行。

> V2.2 文件系统已达到“内容级解包/回打包”，但 SELinux/capabilities/UID/GID 的完整恢复仍未闭环；ext4 的 7-Zip 解包不保留这些元数据。详见 `docs/V2_IMPLEMENTATION.md` 第 6 节。
> 尚未测试 15GB super 全量备份、还原后 AVB 失败救砖、官方 Recovery 分区读取。

## UI 调整（V1.3）

- 页面 padding：28 → 20；标题 28 → 24；Card padding 20 → 14；按钮统一 32px 高、主要按钮最小宽 80
- NavigationView 仅保留 4 个一级入口：设备信息 / 分区备份 / Fastboot 刷入 / 卡刷包工具
- 除“分区备份”的高风险分组按需求默认折叠外，不再使用二级 Expander；警告统一用 InfoBar
- Fastboot 改为按目标分区名直接刷入：
  - 手动模式：目标分区名文本框 + 选择镜像；留空由文件名推导
  - 文件夹模式：递归扫描 `.img`，已识别 / 未匹配分组，所有条目均可编辑目标分区名
  - 提供全选 / 反选 / 仅选已识别分区
  - 每条任务的 partition name 是 TextBox，可直接改实际刷入目标
- 分区备份页：改为 **Recovery + ADB root** 读取，不依赖 fastboot 分区节点；Fastboot 模式只提示重启到 Recovery
- 分区备份页：默认不勾选；常规 / super 整体与内逻辑 / 高风险三组平铺；提供全选 / 反选 / 仅选常规；super 选中后逻辑分区标注已包含并禁用
- 已检查设备在 Fastboot 时的空态：显示“ADB root 未确认 / 当前不是 Recovery”，开始备份禁用
- 卡刷包页解包/打包操作按钮统一放在各自底部操作行
- 窗口宽度 < 1000px 时自动收起导航为 104px 图标栏，800px 最小宽度下表格仍可用
- 已检查 1240×840 与 800×700 两种窗口尺寸：设备页、分区备份页（无设备空态）、Fastboot 页（手动/文件夹模式）、卡刷包页布局正常
- 备份与刷入均使用单次汇总确认，不串联多级弹窗

## 自动化测试

`dotnet test tests/FlashKit.Tests`：**43/43 通过**

新增/覆盖：

- 构造 payload 的 REPLACE/BZ/XZ/ZERO 解包
- 仅 partial 标记但自包含的 payload 解包并输出 warning
- `new_partition_info` size/hash 校验；错误 hash 失败并清理输出
- 真实镜像打包 ZIP 结构、Store/Deflate、Unix 权限、回环解包
- 直接按编辑后的分区名刷入（原样传给 fastboot）
- 容量超限、未知分区、动态分区、代号不一致只 warning；非法分区名阻断
- 选择性备份只写勾选分区，manifest `isSelective` / `notBackedUpPartitions` 正确
- 选择 super 时覆盖逻辑分区且不重复写文件
- Recovery ADB root 检查 + `adb exec-out` 写入路径（fake ADB）
- `ProcessRunner.RunToFileAsync` 二进制字节不被 UTF-8 编码破坏（0x00/0xFF 回环）
- 解析器、ProcessRunner

## 未验证

- 真实 Android 设备的 ADB/Fastboot 连接、驱动、Fastbootd 与逐项 flash
- Recovery 的 ADB root 权限、`/dev/block/by-name` / `/dev/block/mapper` 节点在不同厂商/TWRP 版本上的实际可用性
- `adb exec-out cat` 在超大分区（如 super）上的实际速率与断线行为
- 真实分区备份输出与恢复流程
- 目标 recovery 对生成 ZIP 的实际执行
- 真实厂商 OTA 的签名校验（本工具不解签名）

以上需要在目标设备上继续验证。










