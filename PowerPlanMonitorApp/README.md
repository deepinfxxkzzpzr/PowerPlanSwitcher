# PowerPlanMonitor App

正式版使用 `.NET 8 + WPF` 实现，入口是编译后的 `PowerPlanMonitor.exe`。

## 目录

- `PowerPlanMonitor.App`：WPF 应用源码。
- `Installer`：WiX MSI 安装包定义。
- `../dist/PowerPlanMonitor`：发布后的自包含 exe。

## 构建

```powershell
..\..\.dotnet\dotnet.exe build -c Release
```

## 发布

```powershell
..\..\.dotnet\dotnet.exe publish -c Release -r win-x64 --self-contained true -o ..\..\dist\PowerPlanMonitor /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

## 安装包

```powershell
..\..\.dotnet6\dotnet.exe ..\..\.tools-wix5\.store\wix\5.0.2\wix\5.0.2\tools\net6.0\any\wix.dll build ..\Installer\Product.wxs -arch x64 -out ..\..\dist\PowerPlanMonitorSetup.msi
```

安装包会注册名为 `PowerPlanMonitor` 的 Windows 登录计划任务，任务以最高权限启动
`C:\Program Files\PowerPlanMonitor\PowerPlanMonitor.exe`。
