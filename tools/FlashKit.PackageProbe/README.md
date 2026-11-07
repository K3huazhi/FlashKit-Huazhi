# PackageProbe

仅用于开发/验收的轻量命令行工具，不随应用发布。

它调用与 WinUI 应用相同的 `PackageExtractor` / `PackagePacker`，用于在无 UI 环境下验证真实 OTA 包：

```powershell
# 解包（输出到源文件同目录的同名文件夹）
dotnet run --project tools/FlashKit.PackageProbe -- extract "D:\path\ota.zip"

# 打包（partition=image 可重复）
dotnet run --project tools/FlashKit.PackageProbe -- pack "D:\out.zip" "boot=D:\images\boot.img" "dtbo=D:\images\dtbo.img"
```

真实 ColorOS 包的验证记录见 `docs/VALIDATION.md`。运行 Probe 时不要再编译同一个输出目录，否则 Windows 会锁住 `FlashKit.Services.dll`。
