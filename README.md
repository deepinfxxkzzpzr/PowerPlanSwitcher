# PowerPlanMonitor

PowerPlanMonitor 是一个专为 **Windows 笔记本电脑** 设计的轻量工具，使用 `.NET 8 + WPF` 开发。它主要解决三件事：**切换笔记本电源模式、监控电脑性能、在屏幕右下角显示清晰的日期和时间**。

> 一个程序同时完成电源管理、性能监控和右下角时钟显示，常驻托盘即可使用。

## 三个核心功能

### 1. 笔记本电源切换

在节能、平衡、卓越性能等 Windows 电源计划之间快速切换，不需要反复进入系统设置。

- **插电自动切性能模式**：接通电源后，自动切换到预设的高性能电源计划。
- **拔电自动切省电模式**：使用电池后，自动切换到平衡或节能计划，帮助延长续航。
- **托盘直接切换**：右键托盘图标即可查看当前模式并选择其他电源计划。
- **快捷键循环切换**：可以自定义全局快捷键，在三个常用模式之间快速循环。
- **切换结果提示**：模式改变后显示简洁的 OSD 提示，不需要打开主窗口确认。

### 2. 性能监控

通过桌面悬浮仪表盘随时了解电脑负载，不需要一直打开大型硬件监控软件。

- 显示 CPU 使用率和当前运行频率。
- 显示 CPU 温度；硬件不支持或驱动不可用时显示 `--°C`。
- 显示内存使用率和剩余可用内存。
- 显示实时网络上传与下载速度。
- 悬浮窗支持缩放、透明度、置顶、自动贴边和隐藏，适合长期放在桌面边缘。

> [!IMPORTANT]
> 如果 CPU **频率**显示为 `--GHz`，或**温度**显示为 `--°C`，请打开软件的“设置”页面，在硬件温度驱动区域点击“安装/修复驱动”，安装 [**PawnIO**](https://github.com/namazso/PawnIO)。安装驱动需要管理员权限；安装成功后 PowerPlanMonitor 会自动刷新并重新识别硬件数据。

![PowerPlanMonitor 悬浮仪表盘](docs/screenshots/dashboard.png)

### 3. 右下角时钟

在 Windows 任务栏右下角显示更容易阅读的双行时钟，让时间和日期同时保持可见。

- 第一行显示当前时间，第二行显示日期。
- 默认支持中国标准时间，也可以在设置中选择其他 Windows 时区。
- 时钟停靠在系统托盘附近，与任务栏布局保持一致。
- 可随程序自动启动，也可以在设置中单独关闭。

## 其他功能

- 支持 Windows 登录后自动启动。
- 所有主要功能都可以从系统托盘管理，主界面无需长期打开。
- 设置自动保存，下次启动时继续使用当前电源、监控和时钟配置。
- 可选使用 PawnIO 驱动读取支持设备的 CPU 温度。

## 设置

所有常用选项均可通过中文图形界面配置，包括插电与拔电模式、性能悬浮窗、快捷键、右下角时钟和开机启动。

![PowerPlanMonitor 设置窗口](docs/screenshots/settings.png)

配置默认保存到：

```text
%APPDATA%\PowerPlanMonitor\setting.ini
```

## 下载与安装

在 [Releases](https://github.com/deepinfxxkzzpzr/PowerPlanMonitor/releases) 页面下载最新的 MSI 安装包。当前版本为 [`v1.0.26`](https://github.com/deepinfxxkzzpzr/PowerPlanMonitor/releases/tag/v1.0.26)。

安装包会把程序安装到 `C:\Program Files\PowerPlanMonitor`，并注册 Windows 登录计划任务。[**PawnIO**](https://github.com/namazso/PawnIO) 硬件访问驱动属于可选功能；当 CPU 频率或温度无法识别时，可以从托盘菜单或设置窗口执行“安装/修复温度驱动”。

## 频率或温度无法识别

如果悬浮窗中的 CPU 频率显示为 `--GHz`，或者 CPU 温度显示为 `--°C`，请按以下步骤处理：

1. 打开 PowerPlanMonitor 的“设置”窗口。
2. 进入“启动”选项卡，找到“硬件温度驱动”。
3. 点击“安装/修复驱动”。
4. 在系统提示时允许管理员权限，完成 [**PawnIO**](https://github.com/namazso/PawnIO) 安装。
5. 安装成功后，程序会自动刷新性能监控数据。

PawnIO 提供读取底层硬件信息所需的访问能力。即使已经安装驱动，个别处理器或主板仍可能因为硬件传感器兼容性而无法提供温度，此时界面会继续安全地显示 `--°C`，不影响电源切换、内存监控、网速监控和右下角时钟功能。

## 系统要求

- Windows 10 1809 或更高版本
- 64 位 Windows（`win-x64`）
- 切换电源计划和安装 [**PawnIO**](https://github.com/namazso/PawnIO) 驱动时需要相应的系统权限
- CPU 频率和温度是否可读取取决于处理器、主板和驱动支持；不可用时界面显示 `--GHz` 或 `--°C`

## 从源码构建

需要 Windows 和 .NET 8 SDK：

```powershell
dotnet restore PowerPlanMonitorApp/PowerPlanMonitor.App/PowerPlanMonitor.App.csproj
dotnet build PowerPlanMonitorApp/PowerPlanMonitor.App/PowerPlanMonitor.App.csproj -c Release
```

发布自包含的单文件程序：

```powershell
dotnet publish PowerPlanMonitorApp/PowerPlanMonitor.App/PowerPlanMonitor.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

安装包使用 WiX Toolset 5 构建，定义文件位于 `PowerPlanMonitorApp/Installer/Product.wxs`。

`PawnIO_Setup.exe` 是可选的第三方发布依赖，不提交到源码仓库。需要在发布包中提供驱动安装功能时，请从 [**PawnIO**](https://github.com/namazso/PawnIO) 的可信发布来源取得安装程序，并放入 `PowerPlanMonitorApp/ThirdParty/` 后再执行发布。

## 项目结构

```text
PowerPlanMonitorApp/
├── PowerPlanMonitor.App/          WPF 主程序
│   ├── Models/                    配置、电源计划和监控数据模型
│   ├── Services/                  电源、指标、温度、快捷键和启动服务
│   └── *.xaml                     悬浮窗、设置页、托盘时钟和 OSD 界面
├── PowerPlanMonitor.Diagnostics/  硬件传感器诊断工具
└── Installer/                     WiX MSI 安装包定义
```

## 第三方组件

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)：硬件传感器读取
- [**PawnIO**](https://github.com/namazso/PawnIO)：频率或温度无法识别时，可从软件设置中安装的底层硬件访问驱动
- [System.Management](https://www.nuget.org/packages/System.Management)：Windows 管理信息访问

## 许可证

本项目采用 [MIT License](LICENSE) 开源。
