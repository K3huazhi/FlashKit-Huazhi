# FlashKit 整体设计

> 项目状态：**已停止开发（2026-11）**。这份文档保留当时的架构决策、边界与已知问题，方便后续检索或接手。

## 1. 目标与非目标

FlashKit 是一个 Windows 11 上的原生 Android 刷机工具箱，目标是把常用的 ADB / Fastboot / 分区 / 镜像操作放进一个统一、可审计的桌面应用，而不是用一堆散落的批处理和命令行脚本。

**目标**

- 设备信息、Fastboot 刷入、卡刷包解包/打包
- 分区备份与还原，支持选择性备份和 manifest 校验
- 镜像格式工具（sparse/raw、哈希、payload）
- 设备文件管理（push/pull/browse）
- 文件系统层解包/回打包（ext4/f2fs/erofs）
- 所有危险/重 IO 操作统一后台任务模型、进度、取消、日志
- 使用真实设备边做边验证，不把“能编译”当成“能用”

**明确非目标**

- 不做 payload.bin 回打包
- 不做 `.dat.br` 打包
- 不内置厂商专用驱动
- 不支持官方 Recovery 直接读块设备
- 不承诺所有厂商 fastboot 都暴露分区节点

## 2. 分层

```text
FlashKit.Core
  纯模型 / 接口 / 解析器
  不引用 WinUI，不启动进程

FlashKit.Services
  进程边界、ADB/Fastboot 客户端、设备、分区、刷入
  备份/还原、镜像工具、文件管理、文件系统工具链
  任务队列与历史

FlashKit.App
  WinUI 3 主程序、页面、ViewModel、独立窗口、文案
```

依赖方向固定为 `App -> Services -> Core`。UI 不直接拼接 shell 命令，所有外部进程统一经过 `IProcessRunner`。

## 3. 进程与安全边界

### ProcessRunner

`ProcessRunner` 是唯一的外部进程入口，负责：

- 参数列表而不是拼接命令行（避免空格和注入问题）
- stdout/stderr 流式回调
- 超时、取消、退出码
- 命令历史日志
- `RunToFileAsync`：把 stdout 原始字节写入文件，不做 UTF-8 文本解码（用于 `adb exec-out dd`）

ADB / Fastboot 客户端只是构造参数，不自行启动进程。

### 危险操作

- 刷入、还原、备份、文件系统写操作都进入统一任务队列
- 同一时间只运行一个任务（比“同一设备串行”更保守）
- 危险操作只做一次汇总确认，不串联多个弹窗
- 任务进行中锁导航
- 出错清理半成品；还原默认失败即停

## 4. 任务队列与历史

`TaskQueueService` 使用单个 `SemaphoreSlim(1,1)` 串行化任务，记录：

```text
类型 / 设备序列号 / 输入 / 输出 / 状态
开始/结束/耗时 / 进度 / 错误 / 日志引用
```

历史持久化到 `%LOCALAPPDATA%\FlashKit\tasks.json`，最多 500 条；启动时把上次停在 `Running` 的任务标为失败。排队任务可取消，运行中任务通过 linked token 取消。

现有刷入、解包、打包、备份也通过 `RunAsync` 进入同一队列；页面保留自己的进度展示，但执行入口不再绕过队列。

## 5. 分区备份与还原

### 环境探测

| 模式 | 规则 |
| --- | --- |
| system | `adb shell id` 为 root，或 `adb root` / `su -c id` 成功；否则拒绝 |
| recovery | 必须 `getprop ro.twrp.version` 非空；否则判定官方 Recovery 并终止 |
| sideload | 拒绝 |
| unauthorized | 提示授权 |
| fastboot | 不读分区，提示切到系统/TWRP |

### 读取

使用：

```text
adb exec-out dd if=<block> bs=4M conv=noerror,sync
```

关键发现：toybox 的 `conv=sync` 会把最后一个块补齐到 4MiB，部分 TWRP 还会把 `records in/out` 写到 stdout。服务在传输完成后按 `blockdev --getsize64` 截断到实际分区大小，再校验长度和写 manifest。

输出：

```text
<model>_<serial>_<timestamp>/
  images/<partition>.img
  manifest.json
```

manifest 记录备份模式、是否选择性、已备份分区、未备份分区、源块设备、文件大小、Android/TWRP 版本、是否 super 整体、`ddUsedNoErrorSync`。

### 选择性备份

- 默认不勾选
- 常规 / super 整体与内逻辑 / 高风险分组
- 全选 / 反选 / 仅选常规
- 勾选 super 后，同组逻辑分区标记“已包含于 super 备份”
- 只勾选逻辑分区时提示不阻断
- 未勾选分区不生成文件，只在 manifest 名字列表中记录

### 还原

`RestoreService` 读取 manifest，校验：

- 镜像存在
- 文件大小与 manifest 一致
- 路径不越界
- 序列号/型号不一致强警告；跨型号必须显式允许
- 分区高风险提示

路径：

- 系统/KSU/TWRP：`adb push` 到 `/data/local/tmp/flashkit-restore-*`，再 `dd if=<remote> of=<block> bs=4M`
- Fastboot：`fastboot flash <partition> <image>`
- 动态分区提示先 `fastboot reboot fastboot`

## 6. 镜像工具

- sparse ↔ raw：纯 C# 流式转换，支持 RAW/FILL/DON'T CARE/CRC32 chunk
- 格式识别：raw/sparse/ext4/f2fs/erofs/payload
- 哈希：MD5/SHA256 流式
- payload.bin：复用已有解析器，输出 `images/` + manifest
- 输出先写 `.tmp`，成功后替换；失败清理

## 7. 文件管理

- 设备侧 `stat` 枚举名称、大小、权限、owner/group、时间
- push/pull 走 `IAdbClient` 和任务队列
- 写路径拒绝 `/dev`、`/proc`、`/sys`、`/system`、`/vendor`、`/product`、`/odm`、`/metadata`
- 不提供本机/设备双栏；页面保持单页平铺

## 8. 文件系统解包/回打包

能力矩阵：

| 格式 | 解包 | 回打包 |
| --- | --- | --- |
| ext4 | 内置 7-Zip 展开；`7z l -slt` 记录 mode/UID/GID | 设备端 `mke2fs -d` |
| f2fs | 设备端 `dump.f2fs -r -P` | `make_f2fs` + `sload_f2fs -P` |
| erofs | 设备端 `fsck.erofs --extract` | 设备端 `mkfs.erofs` |

封装层（raw/sparse/payload）复用镜像工具。

### 为什么不是 WSL2

设计上优先 WSL2 / Linux 工具链，但测试机没有 WSL2 发行版，也没有 `debugfs`。在不安装额外环境的前提下，采用设备端已有工具 + 内置 7-Zip 完成内容级解包，并明确标注限制。

### 未闭环的硬要求

文件系统模块只达到“内容级”：

- SELinux context 未采集
- capabilities 未采集/回填
- ext4 的 7-Zip 路径只能拿 mode/UID/GID
- erofs 的 `security.selinux` xattr 在 `/data/local/tmp` 写入失败（EOPNOTSUPP），回退 `--no-xattrs`
- ext4 镜像 loop mount 返回 I/O error
- 回打包没有完成开机级验证

这是项目停止时最大的未完成项，不是“已解决但没写文档”。

## 9. UI 约束

- 左侧 `NavigationView` + 右侧 `Frame`
- PaneFooter：设置 / 终端 / 原始日志
- 终端和日志使用独立 Window 单例
- 少二级菜单，页面顶部或底部平铺操作
- 按钮统一高度/间距，图标按钮带 ToolTip
- Mica 不可用时回退纯色

## 10. 测试与真机验证

自动化：`tests/FlashKit.Tests`，43 个测试。

真机：Redmi K70（`vermeer` / Android 16 / KSU）上实际验证过：

- 133 个分区枚举
- `ALIGN_TO_128K_1` 备份，长度 106,496 字节，SHA256 与设备端一致
- 同镜像回写还原，回写后 hash 不变
- `/sdcard` 浏览、`/data/local/tmp` 上传/下载
- ext4/f2fs/erofs 内容级解包/回打包
- 设备未因测试变砖，序列号等隐私信息已从公开文档中移除

## 11. 关键取舍

1. **单一队列全局串行**：比按设备串行更保守，牺牲一点并发换取安全。
2. **manifest 优先**：还原不猜分区来源，manifest 不一致时拒绝或强警告。
3. **dd 截断**：既遵守 `conv=noerror,sync`，又避免 4MiB 块补齐污染镜像。
4. **不做假文件系统解包**：内容级和开机级明确分开；元数据不完整就写警告，不用 raw 复制冒充。
5. **7-Zip 作为 ext4 解包备选**：不是理想 Linux 工具链，但能在没有 WSL2 的 Windows 上实际工作；局限写进 manifest。

## 12. 停止开发时仍然缺什么

- SELinux/capabilities 元数据与开机级回打包
- 15GB `super` 全量备份/还原的超时和断线处理
- AVB/dm-verity 失败救砖流程
- 真实 Fastboot 刷入回归
- 更完整的厂商 ROM 差异测试

如果继续，优先顺序应是：WSL2/ext4 元数据 → 元数据回填 → 真实 boot 分区回打包 → 断线/大文件恢复。

