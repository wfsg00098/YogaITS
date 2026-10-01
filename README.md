# YogaITS

一个面向部分联想笔记本的轻量级 Windows 托盘控制中心。

YogaITS 目前主要用于快速切换 **ITS 性能模式、充电模式、夜间充电优化、开盖开机、NVIDIA 独显休眠相关操作和开机自启**。界面基于 WinUI 3，常驻任务栏托盘，尽量只保留日常真正会用到的功能。

> 当前主要开发 / 验证机型：\\\*\\\*Yoga Pro 16 IPH11\\\*\\\*  
> 其他机型采用 capability / driver / model family 动态检测，出现对应选项并不代表所有同系列机型都已经实机验证。

\---

## 功能

### 性能选项

支持 Lenovo ITS 模式切换：

* 智能模式
* 安静模式
* 性能模式
* 创作模式 / 极客模式（设备支持时自动显示）

程序会检测：

* 机型系列
* `LenovoProcessManagement`
* `PowerSlider.Version`
* `ITS\\\_FN\\\_Capability`
* `EnergyDrv` 的第四档模式 capability

第四档名称会根据设备能力自动显示为 **创作模式** 或 **极客模式**。

切换后会重新读取实际状态进行验证，而不是直接把 UI 当成已经成功。

\---

### 充电选项

支持：

* 养护模式
* 常规模式
* 快充模式
* 夜间充电优化（设备支持时显示）

充电模式通过 Lenovo `EnergyDrv` 直接读取和设置，并在切换后验证实际硬件状态。

为了兼容 Lenovo / Vantage 生态，充电模式成功切换后还会同步：

```text
HKCU\\\\Software\\\\Lenovo\\\\VantageService\\\\AddinData\\\\IdeaNotebookAddin
BatteryChargeMode
```

硬件状态仍然是最终依据。

\---

### 独显休眠

检测到 NVIDIA 显示设备时显示：

* **结束独显占用进程**
* **重启独显设备**

其中：

* 独显占用进程通过 NVAPI 查询
* 系统 / 驱动 / 受保护进程会被过滤
* 候选用户进程会直接结束
* 独显设备重启使用 `pnputil /restart-device`

这两个操作现在都不会关闭控制中心窗口。

> 当前独显功能仅针对 NVIDIA 实现。

\---

### 杂项

支持：

* 开盖开机
* 开机自启

开盖开机会优先使用 Lenovo capability 接口；不可用时再尝试 UEFI `FBSWIF` 后端。

开机自启通过 Windows Task Scheduler 创建：

```text
Yoga ITS Tray
```

登录后以最高权限启动，因此后续登录启动不会再次弹出 UAC。

\---

## 界面与交互

YogaITS 不是普通主窗口应用，而是一个原生托盘常驻程序。

当前界面特性包括：

* WinUI 3
* Windows 11 风格圆角
* DWM transient Acrylic
* 强制使用当前系统 Accent Color 作为窗口边框
* 固定宽度紧凑宫格布局
* 不显示任务切换器入口
* 不激活式托盘弹窗
* 点击任务栏隐藏图标区域时不会把 overflow panel 顶掉
* 启动时不闪空白窗口 / 黑框
* 单实例运行

性能模式和充电模式点击后 **不会自动关闭窗口**。

切换期间不会叠加额外的 `ProgressRing`、省略号或 badge；使用按钮自身的按压 / 状态变化作为即时反馈，验证结束后再按实际读取结果刷新最终选中项。

\---

## 快速响应与状态缓存

为了避免每次打开托盘菜单都同步查询 WMI、驱动、注册表和计划任务，YogaITS 使用内存状态缓存。

启动时一次性读取：

* 机型信息
* ITS capability
* ITS 当前模式
* 充电模式
* 夜间充电优化
* 开盖开机
* NVIDIA 设备存在性
* 开机自启状态

之后打开托盘菜单时：

1. 先直接使用缓存立即显示 UI
2. 菜单显示后再后台刷新动态状态
3. 如果窗口仍然打开，则原位更新 UI

用户刚完成的设置不会被较早启动的后台刷新覆盖。

托盘 tooltip 也优先使用缓存，只进行节流后的后台 ITS 状态刷新。

**没有持续后台轮询。**

\---

## Explorer 重启恢复

如果 `explorer.exe` 崩溃或被手动重启，Windows 通知区域会被重新创建，普通托盘图标会因此消失。

YogaITS 会监听：

```text
TaskbarCreated
```

收到该系统广播后自动重新执行：

```text
NIM\\\_ADD
NIM\\\_SETVERSION (NOTIFYICON\\\_VERSION\\\_4)
```

因此无需重启 YogaITS，托盘图标会自动重新出现。

\---

## 兼容性

### ITS

当前 ITS 实现只使用现代 `LenovoProcessManagement` 路径。

模型家族判断参考 Lenovo Legion Toolkit 的兼容性逻辑，目前包含：

* IdeaPad（排除 IdeaPad Gaming）
* ThinkBook
* Lenovo Slim
* Motobook
* XiaoXin
* YOGA

同时要求：

```text
PowerSlider.Version >= 0x2000
```

如果设备不满足条件，性能模式区域不会被错误暴露。

第四档模式还会额外查询 `EnergyDrv` capability。

### 电池

充电相关功能依赖：

```text
\\\\\\\\.\\\\EnergyDrv
```

读取失败时相应功能会隐藏或显示为不可用。

### 开盖开机

依次尝试：

1. Lenovo WMI capability：`FlipToStart`
2. UEFI variable：`FBSWIF`

均不可用时不显示该开关。

### NVIDIA

只有检测到：

```text
PCI\\\\VEN\\\_10DE
```

的显示设备时才显示独显休眠区域。

仅仅打开托盘菜单不会为了判断显卡存在性而初始化 NVAPI。

\---

## 系统要求

运行环境：

* Windows 10 2004 / Build 19041 或更高
* x64
* .NET 10 Desktop Runtime
* 与项目引用版本兼容的 Windows App Runtime
* 对应的 Lenovo 驱动 / 服务
* 管理员权限

应用 manifest 当前使用：

```xml
<requestedExecutionLevel level="requireAdministrator" />
```

这是因为性能模式、驱动访问、设备重启和计划任务等功能都需要较高权限。

\---

## 编译

### 环境

建议准备：

* .NET 10 SDK
* Windows 10/11 x64
* 可正常还原 NuGet 包的网络环境

克隆仓库：

```powershell
git clone https://github.com/wfsg00098/YogaITS.git
cd YogaITS
```

直接运行：

```powershell
.\\\\build-clean.ps1
```

脚本会：

1. 清理旧的 `bin` / `obj` / `dist`
2. `dotnet restore`
3. 以 `Release + win-x64` 进行 framework-dependent publish
4. 验证 `YogaITS.Tray.pri` 是否正确进入发布目录
5. 删除 `.pdb` / `.xml`
6. 再次清理 `bin` / `obj`

最终输出位于：

```text
dist\\\\
```

启动：

```text
dist\\\\YogaITS.Tray.exe
```

\---

## 发布方式

当前使用 **framework-dependent** 发布，而不是 self-contained / single-file。

原因很简单：WinUI 3 和 Windows App SDK 如果全部自包含，会显著增加体积；而为了强行做单文件再把大量运行库塞进 EXE，对这个小工具没有实际收益。

当前配置：

```xml
<WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>
<SelfContained>false</SelfContained>
<PublishTrimmed>false</PublishTrimmed>
<PublishReadyToRun>false</PublishReadyToRun>
```

项目也没有引用 Windows App SDK 的 umbrella/meta package，而是只保留实际需要的组件，避免无意义地带入 AI / ML / ONNX / DirectML 等依赖。

\---

## PRI 发布修复

在当前 `.NET 10 + unpackaged WinUI 3` 组合下，`dotnet publish` 可能已经生成应用自身的 `.pri`，却没有把它加入最终 publish 输出。

YogaITS 在 `.csproj` 中增加了 publish target，将：

```text
$(ProjectPriFullPath)
```

显式加入 `ResolvedFileToPublish`。

`build-clean.ps1` 还会检查：

```text
dist\\\\YogaITS.Tray.pri
```

如果文件缺失则直接终止发布，避免得到一个编译成功但启动即退出的包。

\---

## 主要实现

|功能|实现|
|-|-|
|UI|WinUI 3|
|托盘|Win32 `Shell\\\_NotifyIcon`|
|Explorer 重启恢复|`TaskbarCreated`|
|ITS 切换|`LenovoProcessManagement` custom service command|
|ITS 状态读取|`PowerSlider` registry|
|第四档 capability|`EnergyDrv`|
|充电模式|`EnergyDrv`|
|夜间充电优化|`EnergyDrv`|
|开盖开机|Lenovo WMI capability / UEFI fallback|
|NVIDIA 占用查询|NVAPI|
|NVIDIA 设备重启|`pnputil`|
|开机自启|Task Scheduler|
|机型 / PnP 查询|WMI|
|状态刷新|cache-first + async refresh|

\---

## 项目结构

```text
YogaITS.Tray/
├─ Assets/
│  └─ YogaITS.ico
├─ Services/
│  ├─ AppStateCache.cs
│  ├─ LenovoBatteryHelper.cs
│  ├─ LenovoFlipToStartHelper.cs
│  ├─ LenovoItsHelper.cs
│  ├─ LenovoMachineInfoHelper.cs
│  ├─ NativeTrayIcon.cs
│  ├─ NvidiaGpuHelper.cs
│  └─ StartupTaskHelper.cs
├─ UI/
│  ├─ InfoWindow.cs
│  ├─ TrayMenuWindow.cs
│  └─ WindowHelpers.cs
├─ App.xaml
├─ App.xaml.cs
├─ MainWindow.xaml
├─ MainWindow.xaml.cs
├─ app.manifest
└─ YogaITS.Tray.csproj
```

\---

## 已知限制

* 目前主要在 **Yoga Pro 16 IPH11** 上开发和验证。
* 不同联想产品线即使存在同名功能，底层实现也可能不同。
* 当前只实现现代 `LenovoProcessManagement` ITS 路径，不支持旧 `LITSSVC` 后端。
* 独显休眠功能目前仅支持 NVIDIA。
* “结束独显占用进程”会直接结束符合条件的用户进程，不提供二次确认。
* capability 检测失败时，YogaITS 倾向于隐藏功能，而不是强行尝试写入未知接口。

\---

## 致谢

项目中的 Lenovo ITS、EnergyDrv、充电模式、Flip to Start 等兼容性判断和硬件协议实现大量参考了：

**Lenovo Legion Toolkit**  
https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit

YogaITS 的目标不是复刻完整的 Legion Toolkit，而是把自己日常需要的少数功能整理成一个更小、更直接的托盘控制中心。

\---

## 免责声明

YogaITS 是非官方项目，与 Lenovo / 联想没有关联。

本程序会直接调用 Lenovo 服务、驱动接口、UEFI 配置以及 Windows 设备管理命令。请只在你了解并接受这些行为的设备上使用。

