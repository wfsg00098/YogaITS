# 开发与维护

用户使用说明见 [README.md](README.md)。本文以仓库内的项目、源码和脚本为准，涵盖克隆、构建、验证、发布及维护；所有命令均在仓库根目录的 **Windows PowerShell 7** 中执行。

## 仓库结构

| 路径 | 职责 |
| --- | --- |
| [YogaITS.Tray/](YogaITS.Tray/) | WinUI 3 应用入口、XAML、管理员运行清单及内置图标。 |
| [Services/](YogaITS.Tray/Services/) | 硬件访问、状态快照、串行调度、系统事件、配置及启动任务。 |
| [Services/Color/](YogaITS.Tray/Services/Color/)、[Elevoc/](YogaITS.Tray/Services/Elevoc/)、[Hpd/](YogaITS.Tray/Services/Hpd/) | 色彩、音频降噪/声纹、人员感知后端。 |
| [Services/GpuMode/](YogaITS.Tray/Services/GpuMode/)、[KeyboardBacklight/](YogaITS.Tray/Services/KeyboardBacklight/) | 显卡工作模式、键盘背光协议与能力检测。 |
| [Services/AlwaysOnUsb/](YogaITS.Tray/Services/AlwaysOnUsb/)、[EnergyDriver.cs](YogaITS.Tray/Services/EnergyDriver.cs) | Always-On USB 三态读写，与充电功能共用的 EnergyDrv 设备访问。 |
| [UI/](YogaITS.Tray/UI/) | `Shell` 窗口、`Controls` 共享控件、`Features` 功能分组、`Layout` 排版、`Animation` 动效、`Accessibility` 无障碍、`Presentation` 材质适配。 |
| [UI/Glass/](YogaITS.Tray/UI/Glass/) | 玻璃控件、物理动效、shader、背景、捕获、运行时、设置和窗口输入；普通版整体排除此目录。 |
| [Build/](YogaITS.Tray/Build/)、[CaptureIdentity/](YogaITS.Tray/CaptureIdentity/) | 两种构建的依赖/清单，以及玻璃版稀疏包身份。 |
| [YogaITS.Updater/](YogaITS.Updater/) | 更新安装、校验、失败回滚及重启。 |
| [YogaITS.dGPUKeepAwake/](YogaITS.dGPUKeepAwake/) | 在指定 NVIDIA 适配器上持有轻量 D3D11 context。 |
| [YogaITS.dGPURestart/](YogaITS.dGPURestart/) | 等待主程序退出、重启独显设备、恢复控制中心。 |
| [Tests/](Tests/) | 策略、发布物、隐藏 WinUI、shader 检查，以及可选只读硬件诊断。 |
| [ThirdParty/](ThirdParty/) | 第三方材质源码、许可原件和组件清单。 |
| [scripts/](scripts/)、[build-clean.ps1](build-clean.ps1) | 版本计算、分发声明和完整构建入口。 |
| [VERSION](VERSION)、[发布工作流](.github/workflows/build-release.yml) | 人工维护版本系列，自动构建两版并发布 Release。 |
| [main.png](main.png)、[mini.png](mini.png) | README 使用的原生界面截图。 |

`bin/`、`obj/`、`dist/`、`publish/` 和 C++ `Output/` 是忽略的生成目录，不是源码依赖。克隆仓库后无需额外取得本地参考工程或压缩包。

## 环境与构建

### 前提

- Windows 11 x64、Git、PowerShell 7、[.NET 10 SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)，并允许 NuGet 还原依赖。
- 玻璃版另需 Visual Studio 2026 C++ x64 工具链 **v145**（含 MSBuild 和 C++ 编译器），以及 Windows SDK **10.0.26100.0** 的 `fxc`、`makepri`、`makeappx`、`mt`；脚本通过 `vswhere` 定位 VS。
- 运行普通版需兼容的系统 Windows App Runtime；当前依赖 Runtime **2.5.1**，可从[微软下载页](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)安装 x64 版本。玻璃版私有部署 SDK 运行文件。两版运行均依赖 .NET 10；运行硬件功能还需要管理员权限及设备对应的联想驱动/服务。

使用 Git 克隆，保留 `.git`：版本脚本读取提交 SHA 和时间，直接下载源码 ZIP 不能代替此流程。

```powershell
git clone https://github.com/wfsg00098/YogaITS.git
Set-Location .\YogaITS
.\build-clean.ps1 -Checks -BuildOnly
.\build-clean.ps1 -LiquidGlass -Checks -BuildOnly
```

产物分别位于 `dist/native/`、`dist/liquid-glass/`。脚本还原并发布主程序和三个助手，准备 PRI、玻璃捕获桥接、HDR shader、运行时清单、身份资源及分发许可；不注册身份、不启动应用、不访问硬件。`-BuildOnly` 是兼容参数，脚本本身始终只构建；`--liquid-glass` 等价于 `-LiquidGlass`。

只清理选定输出目录，正在运行的同目录程序会阻止覆盖。开发时可选择独立输出：

```powershell
.\build-clean.ps1 -LiquidGlass -BuildOnly -OutputDirectory .\publish\dev-glass
```

确认另一版本已退出后，选择需要启动的目录：

```powershell
$directory = (Resolve-Path .\dist\liquid-glass).Path
Start-Process (Join-Path $directory 'YogaITS.Tray.exe') -WorkingDirectory $directory -Verb RunAs
```

普通版将目录改为 `dist/native`。启动会读取硬件并可能应用已保存的显卡/保活偏好；构建成功不表示已完成实机验收。最终交付使用完整构建脚本，不能用单独 `dotnet publish` 的结果代替。

### 依赖与隔离

| 构建 | 固定的直接 UI 依赖 |
| --- | --- |
| 普通版 | `Microsoft.WindowsAppSDK.WinUI 2.3.9`、`Runtime 2.5.1`、`InteractiveExperiences 2.1.9`，见 [Native.props](YogaITS.Tray/Build/Native.props)。 |
| 玻璃版 | `Microsoft.WindowsAppSDK 2.2.0`、`Win2D 1.3.2`、`LiquidGlassWinUI 1.0.3`，见 [LiquidGlass.props](YogaITS.Tray/Build/LiquidGlass.props)。 |

两版都使用 `System.Management`、`System.ServiceProcess.ServiceController 10.0.0`。材质的 managed/shader 实现来自仓库内 `ThirdParty/LiquidGlassWinUI`；native effect 从固定 NuGet 包取得。传递依赖及分发许可见 [components.json](ThirdParty/Licenses/components.json)。

[Directory.Build.props](Directory.Build.props) 与 [Directory.Build.targets](Directory.Build.targets) 按构建类型和项目隔离 `obj/`、`bin/`；检查宿主再加 `-ui-checks` 后缀。不得跨构建复用还原结果或混合 DLL。普通版不编译玻璃代码，不携带捕获身份、本地 SDK、Win2D 或材质 DLL。

## 检查与诊断

`build-clean.ps1 -Checks` 是完整构建检查入口，检查数量随代码和产物变化，不使用历史数量作为验收标准。

| 检查 | 内容与边界 |
| --- | --- |
| [ReleaseVersionChecks.ps1](Tests/ReleaseVersionChecks.ps1) | 版本系列、UTC 时间、SHA 和包名。 |
| [DistributionChecks.ps1](Tests/DistributionChecks.ps1) | SHA-256 已知向量、清单路径与编码、重复生成及源文件变化。 |
| [YogaITS.PolicyChecks](Tests/YogaITS.PolicyChecks/) | 真实生产策略、ABI、并发/超时、更新竞速、ZIP 安全、安装回滚、助手身份和发布程序集。使用模拟后端、临时目录及测试自建进程。 |
| [YogaITS.UiPolicyChecks](Tests/YogaITS.UiPolicyChecks/) | DPI、布局、选择/手势、玻璃物理、捕获生命周期、图标身份和窗口缩放状态。部分检查创建不显示的 HWND。 |
| [UiContractChecks.ps1](Tests/UiContractChecks.ps1) | 重编译同一套 UI 为隔离检查宿主，在 WinUI dispatcher 下验证控件、真实布局及构建边界，不显示窗口或初始化硬件服务。 |
| [IdentityIconChecks.ps1](Tests/IdentityIconChecks.ps1) | 玻璃版图标透明度、资源索引、身份版本；不注册包。 |
| [YogaITS.ShaderChecks](Tests/YogaITS.ShaderChecks/) | C++ 编译及 WARP 合成图像，验证 shader 边界、曲面、DPI 和采样；由玻璃版脚本调用，不捕获桌面。 |

可单独运行：

```powershell
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release
dotnet run --project .\Tests\YogaITS.UiPolicyChecks -c Release
.\Tests\ReleaseVersionChecks.ps1
.\Tests\UiContractChecks.ps1 -PublishedDirectory .\dist\native
```

WinUI 契约检查需要已完成的对应发布目录、Windows SDK 26100 的 `mt.exe`，普通版还需系统运行时；玻璃版已包含在 `-Checks` 中。宿主只在临时目录将自身改为 asInvoker，生产程序集不包含检查实现或分发入口。完整包校验及可选进程树检查分别使用：

```powershell
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release -- --check-package .\dist\native
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release -- --gpu-process-tree
```

进程树检查仅结束它自己创建的父/子进程，默认 CI 不运行此选项。自动检查不操作真实驱动、麦克风、计划任务或网络。

仓库内另有 [YogaITS.ReadOnlyProbe](Tests/YogaITS.ReadOnlyProbe/)，用于需要硬件的只读诊断。它与上述自动检查不同，应在管理员终端按需运行：

```powershell
dotnet run --project .\Tests\YogaITS.ReadOnlyProbe -c Release
dotnet run --project .\Tests\YogaITS.ReadOnlyProbe -c Release -- --its-only
```

默认读取显卡、人员感知、色彩及音频能力，不执行显卡偏好初始化、不切换模式、不录音或下载；TCON 读取会临时打开 SPB 会话。`--its-only` 仅读取 ITS 状态。可选 `--check-calibration-http` 会向 Lenovo 发送本机序列号并查询元数据，默认不执行，不下载校色包。拒绝访问不能视为设备不支持，诊断输出不能替代设置与回读的实机测试。

## 架构与状态规则

入口 [Program.cs](YogaITS.Tray/Program.cs) 在 WinUI/COM 代理前初始化 TCON 所需的进程级 COM 安全，并负责玻璃版的捕获身份校验与本地运行时初始化。[MainWindow](YogaITS.Tray/MainWindow.xaml.cs) 是常驻消息宿主，`TrayMenuWindow` 共用完整/迷你窗口，`FeaturePanels` 与共享控件将服务快照呈现为界面。

- **能力与状态分离**：按实际能力显示，不通过试写检测。已确认能力后的临时读取失败保留功能、禁用操作；明确端点/设备消失才隐藏。键盘背光无效读取按其后端规则隐藏。
- **串行调度**：同一服务读写和关闭共用队列；读刷新合并、写入优先，功能版本号阻止晚到读取覆盖新设置。超时不释放仍在调用的 native 资源，也不为重试创建并发调用。
- **确认后显示**：请求状态与确认状态分离，普通设置确认后才切换说明。显卡卡片显示偏好，实际状态另行呈现；未知状态不得伪选第一项或冒充关闭。
- **事件刷新**：开窗/获焦读取缓存并安排刷新；电源、显示拓扑、PnP、音频变化独立于窗口焦点。显示/色彩事件防抖，息屏事件直接协调保活。
- **有限等待**：核心读调用约 5 秒，普通服务读取 10 秒/写入 20 秒，声纹写入 60 秒，色彩读取 90 秒/写入 210 秒；录音取消有独立路径。退出清理最多等待 10 秒，最终由进程结束释放资源。

Local RPC 的 call-timeout 不能代替调用方的等待边界。不得在超时后提前释放句柄或反复重发已经进入 native 调用的命令。

## 硬件后端

### 性能、充电与人员感知

ITS 在保留原有型号范围的基础上，根据 `LenovoProcessManagement\Performance\PowerSlider\Version` 选择路径：版本 ≥8192 使用现代服务，查询 2 成功且含 `0x1` 才支持；较低/缺失版本使用 `LITSSVC`，要求旧 MMC 状态键和 EnergyDrv。现代调用失败不会改走旧服务。极客/创作模式另查能力位，电池或未知供电时可见但禁用，写入前再次确认 AC。

旧版智能发送 135，安静发送 134/146，性能发送 134/148；回读 `(AutomaticModeSetting, CurrentSetting)`，不强制联动 Windows 电源模式。旧第四档通过额外驱动命令实现，进程内跟踪覆盖标记，不能宣称重启后可独立回读。实现见 [LenovoItsHelper.cs](YogaITS.Tray/Services/LenovoItsHelper.cs)。

充电设置与驱动访问串行，夜间优化作为独立选项映射到“常规充电 + 夜间优化开启”。开盖开机操作后才追加重启生效提示。HPD 通过能力表分别判断唤醒/睡眠、人员辅助和播放/暂停；自动睡眠关闭时隐藏速度与辅助项，关闭的功能不重复显示关闭说明。

### 色彩管理

[ColorGamutService](YogaITS.Tray/Services/Color/ColorGamutService.cs) 优先执行 TCON 实时能力检测，未满足时使用有效 ICC；不按产品线或年份选择。TCON 检测不确定且没有有效 ICC 时，不盲目提供 ICC 恢复。

TCON 路径位于 [LenovoTcon/](YogaITS.Tray/Services/Color/LenovoTcon/)：native WMI 预检后，经 `SetTconSwitch` 和 SPB 会话读取 `F3 00` 总能力及 `F0 1D` GET；以实时能力和模式回读决定是否可用，类不存在时不打开 SPB。总能力 `0x0008`，P3/sRGB/Adobe RGB 位分别为 `0x0100/0x0200/0x0400`，raw 为 `0x04/0x06/0x08`；未知值保留未知。枚举/mutex 等待上限 30 秒，事务在同一工作线程使用和释放资源。初始化、清理 gate 和传输字节计数不额外作为阻断条件；设置以 WriteFile 成功及最终有限回读确认，不增加与协议无关的附加门槛。

Factory Calibration 扫描实际取得的全部 `.icc`/`.icm`，提取完整后缀，大小写不敏感去重并保留原始拼写。既有七种模式顺序不变，再排列 DICOM Dim/Office，未知模式追加；无固定数量、Native、模式组合或全包 MHC2 要求。

| 后缀 | 显示名称 |
| --- | --- |
| `_Default` | 默认 (D65) |
| `_Native` | 鲜艳 (Native) |
| `_sRGB` | 网页办公 (sRGB-D65) |
| `_DisplayP3` | 摄影/数码影院 (P3-D65) |
| `_DCIP3` | 原生数码影院 (P3-DCI) |
| `_AdobeRGB` | 摄影印刷 (Adobe RGB) |
| `_REC709` | HDTV视频 (REC709) |
| `_DICOM_Dim` | 暗室医学影像模拟 (DICOM Dim SIM) |
| `_DICOM_Office` | 办公室医学影像模拟 (DICOM Office SIM) |
| 其他 | 完整原始后缀，包括大小写和下划线。 |

[IccCalibrationData](YogaITS.Tray/Services/Color/IccCalibrationData.cs) 验证头、大小、tag table 与校色数据边界；描述性元数据、机型、序列号和面板 HWID 不作为剔除完整校色数据的条件。文件不被修补或改写，安装、关联、激活及回读仍由 Windows 完成。缺少建议亮度时不改变亮度。普通刷新缓存文件校验，实际切换及恢复后重新验证。

在线恢复先确认发送序列号，在三分钟总边界内初次下载并最多重试三次。若四次候选文件指纹一致、仍有坏文件且至少有一个好模式，可跳过坏文件；结果变化、取消、超时或全部损坏均不替换缓存。ZIP 路径、大小、展开数量及候选副本都验证后才替换 Factory。

HDR 卡片和色域保护使用实际 `activeColorMode`；ACM 卡片使用独立 `wideColorUserEnabled` 设置。HDR 仅在支持时显示，只修改偏好后刷新，尊重 Windows 电池策略。ACM 独立于 HDR 操作；未知颜色状态保守阻止色域写入。实际 HDR/ACM 时隐藏 ICC/TCON 色域及 ICC 恢复/亮度联动，实际 SDR 时恢复可用操作。

### 显卡模式与独显操作

[GpuMode/](YogaITS.Tray/Services/GpuMode/) 封装 WMI、NVAPI 显示连接查询及事务；[LenovoGpuWorkingModeService](YogaITS.Tray/Services/LenovoGpuWorkingModeService.cs) 保存偏好并协调事件。能力入口仅为有效且大于零的 `IsSupportIGPUMode.Data`。NVAPI 遍历物理 GPU，以 `NvAPI_GPU_GetConnectedDisplayIds` 返回 Connected / Disconnected / Unknown；未知不得当作未连接。

`PreferredMode` 为保存的偏好；`DesiredMode` 为受电源等条件约束的目标；WMI 原值、确认状态、实际模式及界面缓冲分别保留。智能模式 AC→双显、DC→集显。只有设备消失且无连接冲突才确认集显，WMI=1 本身不够；显示器连接或 NVAPI 成功且设备存在可表明实际双显。有限补查期间保留上一条已知文字，最终仍未知才显示未知。

唯一自动改写偏好的情况：检测到外接显示器通过独显输出，禁用集显选项，并将已有集显偏好改为智能。启动、电源/显示变化协调所有偏好；明确的集显切换失败最多追加 5 次、间隔 5 秒的重试，新选择或事件取消旧调度。切集显前暂停保活，操作结束后按实际条件恢复。

结束进程使用 NVAPI 活动进程列表，保护控制中心、保活助手、DWM、Explorer 和无效 PID；按 PID+启动时间复核，未知或复用时跳过。其余进程连同子进程结束，最多等待 5 秒；不按 SYSTEM、Session 0 或 NVIDIA 容器名称额外排除。UI 先显示 500 ms 执行状态，操作完成清除。

重启独显先确认，再交接至 `YogaITS.dGPURestart`。助手验证同目录父进程、PID、启动时间和权限；主程序退出并释放保活后才执行一次 `pnputil /restart-device`，等待上限 30 秒，再恢复原窗口。助手未就绪不退出主程序，父进程等待超时不强杀或重置。开启保活不禁用此操作。交接、更新及退出互斥，不在持有 WinUI 图形资源的主进程内重置设备。

### 独显保活

主程序持有策略：默认关闭，配置项 `KeepDGpuAwake`。仅 AC、亮屏、非睡眠、非集显且状态允许时运行；离电、息屏或状态未知立即暂停，保留偏好。集显时隐藏整个独显控制分组。主程序处理电源/PnP/显示事件，每 10 分钟监督 helper 健康；暂停条件优先于恢复和周期监督。

助手枚举硬件 NVIDIA adapter（VendorId `0x10DE`，排除软件设备），显式创建 D3D11 device/context，不创建窗口、swap chain 或提交渲染。设备失效由主程序停止并重新启动 helper；助手不自行恢复。helper 每 10 分钟核对父进程身份及存活，正常退出由主程序立即停止。P8/Idle 由驱动决定，不锁频、不保证固定功耗状态。主程序自身与助手使用独立 GPU 偏好。

### 键盘背光

[KeyboardBacklight/](YogaITS.Tray/Services/KeyboardBacklight/) 仅使用 EnergyDrv，不实现 ThinkPad 的 IBMPmDrv。设备以读写及共享读写权限打开 `\\.\EnergyDrv`，所有命令使用 IOCTL `0x83102144`。

| `levels` | 状态编码 | GET | SET |
| --- | --- | --- | --- |
| 1 | 0=关闭，1=开启 | `0x12` | `(state << 16) \| 0x13` |
| 2 | 0=关闭，1=低亮度，2=高亮度 | `0x22` | `(state << 16) \| 0x23` |
| 3 | 0=关闭，1=低亮度，2=高亮度，3=自动 | `0x32` | `(state << 16) \| 0x33` |

能力命令 `0x01` 要求 bit0=1，`levels=raw >> 1`；GET 也要求 bit0=1，levels=3 还需 extended flag `(raw >> 1) & 0x8000`，状态取 `(raw >> 1) & 0x7FFF`。SET 前重新验证能力及 GET，SET 后立即回读匹配才确认；无效/异常时隐藏分组，不乐观更新或自动重试写入。

界面不保存档位、不设周期刷新或自动场景。启动、获焦及操作时读取 EC 状态，与 Fn+Space 共用底层状态，不与 Hot Keys 通信。四档顺序是编码 `[0,3,1,2]`；分组位于麦克风/声纹之后、Always-On USB 之前，仅完整窗口提供。

### Always-On USB

[AlwaysOnUsb/](YogaITS.Tray/Services/AlwaysOnUsb/) 通过 EnergyDrv 的 `0x831020E8` 设置接口读取与 BIOS 一致的三态。协议核对来源为 LLT 当前 [master `0bb045be`](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/0bb045be29c4fd772ba9887a3507b6a9b0b56d58/LenovoLegionToolkit.Lib/Features/AlwaysOnUsbFeature.cs) / dev `5db70ed2`，两者行为一致；使用协议事实独立实现，不引入 LLT 依赖。

GET 输入 `0x02`，调用成功即表示支持，不另加机型或能力位条件。直接解码原始 DWORD 的 bit7（供电开启）和 bit15（始终开启），等价于 LLT 字节序转换后的 bit31/bit23。

| 状态 | 按顺序发送的 SET 输入 |
| --- | --- |
| 关闭 | `0x0B`、`0x12` |
| 仅睡眠时开启 | `0x0A`、`0x12` |
| 始终开启 | `0x0A`、`0x13` |

每次写入前重新 GET，再发送两条命令，随后立即回读；最多读取 10 次，间隔 50 ms，不重发 SET。仅发布实际回读状态，未确认目标则报告失败。设备按 LLT 的 `FILE_READ_DATA | FILE_WRITE_DATA`、共享读写权限打开；与充电功能复用 [EnergyDriver.cs](YogaITS.Tray/Services/EnergyDriver.cs) 和整笔事务的串行锁，键盘背光保持原有独立访问协议。

启动、完整窗口获焦及操作时读取，不保存偏好、不设定时器。初次 GET 失败隐藏分组，已确认能力后的临时失败保留分组但禁用；明确设备消失或接口不支持时隐藏。完整窗口中位于键盘背光之后、开盖开机之前，普通版与玻璃版共用选择和回读逻辑。无硬件策略检查覆盖命令顺序、字节序、回读重试/失败、串行及设备恢复；新增硬件功能尚待实机验收。

### 音频与声纹

[Elevoc/](YogaITS.Tray/Services/Elevoc/) 使用自建 Direct RPC（`ncalrpc:ElevocRpcEndpoint`、NDR32），不加载厂商 UAPO DLL。按当前 Capture/Render endpoint 的函数和 GET 返回检测能力；内置阵列按 JackSubType 判断，不靠设备名称猜测。

麦克风关闭统一写 `environmental`，单人/多人/定位/声纹模式分别为 `private/shared/special/voiceid`。SET 后等待并有限回读，不重发设置。耳机不提供多人；定位和仅听机主只在对应基础模式、内置麦克风及能力允许时显示，未注册声纹时禁用仅听机主。

录制/注册期间禁用两类降噪，共用串行队列；停止丢弃录音，不提交声纹。注册使用 48 kHz、20 秒录音并通过有效数据回读确认；删除前先关闭仅听机主并确认。设备变化取消录制，不向新端点恢复旧模式。WinMM 按 Stop→Reset→Unprepare→Close 释放，失败不释放驱动仍可能使用的内存，结束后清理临时文件。

## 窗口与材质

两版共用 `ChoiceBar`、`HardwareChoiceGroup` 和服务快照；`WindowMaterial` 隔离材质差异，普通版不实例化隐藏玻璃树。选项 ID 与标签分离，重排保留确认状态，能力/端点变化使过期请求失效。文字按实测宽度分行、每行铺满，同排卡片等高；原生卡片使用灰色/强调色边框，讲述人名称包含功能分组。

完整窗口默认四周留白 8 DIP，滚动条位于右侧留白内；系统始终显示滚动条时额外避让。标题保留系统控制按钮，初始焦点放标题，Tab 从首个可用按钮开始；复用控件保持键盘操作后的焦点。原生界面的大窗口获焦时强制强调色细边框，失焦恢复系统默认；玻璃界面由材质轮廓与 DWM 收边。

普通界面使用系统 MicaBackdrop 和原生主题/悬停/显隐过渡。玻璃物理动效仅在实际玻璃材质启用且系统允许动画时运行，切回原生或降级立即归位；原生卡片网格缩放直接跟随布局。避免单独修改已验证的 shader、折射数学、DPI 或积分参数。

[GlassResizeRegions](YogaITS.Tray/UI/Glass/Windowing/GlassResizeRegions.cs) 在四边及四角提供内侧 4 DIP 的透明 WinUI 输入区，系统光标由 `ProtectedCursor` 管理。[WindowResizeGesture](YogaITS.Tray/UI/Glass/Windowing/WindowResizeGesture.cs) 在同一指针捕获会话内处理按下、移动、松开和取消，以屏幕物理坐标调整 AppWindow，避免转交原生模态循环后丢失松开。按系统最小尺寸限制，Escape 还原；失去捕获、切换材质或关闭时结束。最大化、原生模式及迷你窗口隐藏这些输入区。

背景选项保存为 `GlassNativeUI`、`GlassBackgroundSource`、`GlassImagePath`，默认原生 WinUI 3。静态图像使用支持管理员进程的 `Microsoft.Windows.Storage.Pickers.FileOpenPicker`，通过 `AppWindow.Id` 绑定当前窗口；先完整复制到应用数据目录，再公布路径，取消/失败和晚到导入不能覆盖较新选择。静态图像/壁纸不捕获桌面，透明桌面使用系统授权的捕获源，不保存帧到文件。

玻璃版稀疏身份为 `YogaITS` / `CN=YogaITS`。启动校验路径及 manifest 版本，需要时由独立 PowerShell 等待原进程退出后注册并恢复。脚本不启用开发者模式或伪造授权；注册受限时保留可用背景或原生回退。构建不注册包。

[Prepare-Identity.ps1](YogaITS.Tray/Prepare-Identity.ps1) 从内置 ICO 生成透明亮暗图标变体，并将 shell 图标与应用/依赖 PRI 合并，复制到发布根和 `CaptureIdentity/`。不能用仅含图标的 `resources.pri` 遮盖 WinUI 主题资源；保留 `YogaITS.Tray.pri`。修改身份资源需同步递增 manifest 版本，更新包必须完整携带索引及变体。

## 自动更新与发布

### 应用更新

[Updates/](YogaITS.Tray/Services/Updates/) 在启动后延迟 3 秒、之后每 24 小时检查正式 Release。更新卡片仅在完整窗口“杂项”中，位于“开机自启、关于、退出”三列按钮上方；玻璃版的 UI选项分组在杂项之前。关于按钮复用共享卡片材质，使用绑定当前窗口的原生 `ContentDialog` 展示版本、原仓库与许可/免责说明，两种材质均适用，不在迷你窗口增加入口。普通版选择普通附件，玻璃版选择 `-lg` 附件，不自动跨构建切换；不区分材质的旧版会选择普通附件。

关于的两个网页链接通过 [DesktopLinkLauncher](YogaITS.Tray/Services/DesktopLinkLauncher.cs) 找到现有桌面 Shell 的自动化对象，由它按交互用户的默认浏览器关联打开 HTTPS；不设置 `NavigateUri`，避免从管理员/稀疏包上下文自动启动浏览器。桌面不可用或调用失败时保留对话框并显示可复制的网址，不修改默认浏览器、不强制指定浏览器，也不通过命令解释器传递地址。接口依据见微软的[桌面 Shell 启动说明](https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643/)与 [FindWindowSW](https://learn.microsoft.com/windows/win32/api/exdisp/nf-exdisp-ishellwindows-findwindowsw)。

版本格式为 `x.y-yyyyMMddHHmmss-hash`。仅当远端数字系列不低于本地、UTC 日期号严格更新且完整源提交不同，才提示更新。无日期的本地旧版只升级至更高系列的带日期版本；无日期的远端版本不提示更新。

GitHub 与 `https://gh-proxy.org/` 同时请求元数据、同时下载，各自独立暂存，采用最先解析/校验成功的结果；一条失败不取消仍有效的另一条，胜出后取消并等待另一条释放资源。使用默认系统代理，不硬编码代理端口。元数据上限 20 秒，下载读取空闲 20 秒、总时限 8 分钟。每条下载在竞速胜出前必须对照 Release 资产的 `digest` 完成 ZIP SHA-256 校验；缺少有效摘要、大小不符或内容损坏均不能胜出。

安装验证构建类型、ZIP 路径、重复项、大小和主程序/三个助手/PRI 的完整性。新包的 `build-flavor.json` 声明 `checksumManifest: SHA256SUMS.txt`，解压后逐项验证全部文件 SHA-256：拒绝缺失、损坏、重复、越界、自身引用和未覆盖的文件；旧包没有此声明/清单时仍须通过 ZIP 摘要。玻璃版继续校验身份、本地 SDK 版本及 DLL 哈希。助手核对父进程 PID+启动时间后等待退出，从原包再次校验 ZIP 和逐文件清单，备份、替换并比较每个源/目标文件的 SHA-256，失败尝试回滚，成功才标记完成并重启。设置和缓存位于独立数据目录，不改计划任务。只清理超过 7 天且完成标记有效的成功暂存，失败、未知目录及链接保留。

摘要的信任来源仍是所取得的 GitHub Release 元数据。包内清单增加文件完整性检查，不是另一个独立信任源；SHA-256、文件属性和版本文字均不能证明发行者身份，也不能防御同时替换包与元数据的攻击，不替代 Authenticode 或发行签名。

### GitHub Actions

[build-release.yml](.github/workflows/build-release.yml) 仅在推送 `main` 时运行，无路径过滤，文档提交也会触发。使用 Windows VS 2026 runner，先计算共享版本，再分别构建 `native`、`liquid-glass` 并执行检查。每版上传完整 ZIP artifact，保留 30 天；只有两版均成功才发布一个含两个 ZIP 及 `SHA256SUMS.txt` 的 Release。

[Get-ReleaseVersion.ps1](scripts/Get-ReleaseVersion.ps1) 读取 `VERSION`、提交 committer 时间（UTC）和 SHA 前 8 位。相同提交重跑名称不变：

```text
YogaITS-x.y-yyyyMMddHHmmss-hash-win-x64.zip
YogaITS-x.y-yyyyMMddHHmmss-hash-lg-win-x64.zip
SHA256SUMS.txt
```

[Write-Checksums.ps1](scripts/Write-Checksums.ps1) 在发布目录准备完成后生成逐文件清单，覆盖所有文件但排除清单自身，使用相对路径、SHA-256、UTF-8 无 BOM 和 LF。Release job 对两个最终 ZIP 另生成同名清单，附到 Release 并在说明正文展示哈希、源码提交和 Actions 运行链接；重跑会更新该段而保留生成的变更说明。

四个 YogaITS EXE 的 Windows 文件属性包含版权与原仓库地址，程序集保留 `OriginalRepositoryUrl`；`build-flavor.json` 保存来源、版本、提交、构建类型及清单声明。主程序的 `--version` 在转发单实例、身份注册和硬件初始化之前输出这些版本信息，不打开窗口。需要从非管理员终端只读查看时，可在解压目录执行 `dotnet .\YogaITS.Tray.dll --version`；直接启动 EXE 仍遵循其管理员清单。这些标识可被修改或复制，不能用来认定二次编译即为原始发布物。

发布使用 `GITHUB_TOKEN` 的 `contents: write`，不需要私钥。验证标签目标，已有版本只更新附件，不移动标签或改写提交。发布前后核对远端 main，过期构建不覆盖 latest。自建 fork 发布时还需调整应用中固定的更新仓库，不能只改工作流。

### 封存版本

根目录 [VERSION](VERSION) 是版本系列唯一来源，不从分支名计算。验收完成后，在当前 main 创建同名 `x.y` 分支并推送，再在 main 修改 VERSION 开始下一版。封存分支不自动移动，推送数字分支不触发工作流；勿对已发布标签或封存分支覆盖写入。

当前 `6.0` 分支封存于 `e629ee12`，main 的版本系列为 `6.1`。

## 配置与故障排查

| 位置 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\YogaITS\settings.json` | 显卡偏好、保活、色域亮度联动及玻璃 UI 设置；两版共用。 |
| `%LOCALAPPDATA%\YogaITS\ColorCalibration\Factory\` | 验证后的工厂 ICC/ICM 缓存。 |
| `%LOCALAPPDATA%\YogaITS\Backgrounds\` | 托管静态背景图像。 |
| `%LOCALAPPDATA%\YogaITS\Updates\` | 更新包、计划、备份及完成标记。 |

自启任务名为 `Yoga ITS Tray`。查询要求任务启用且 Exec 绝对路径与当前程序一致；查询失败保留未知状态，不伪装为关闭。开启时先准备 XML，再删除旧任务、创建并回读；使用当前用户交互登录、最高权限和 `--startup`。允许电池启动、离电不停止，不设空闲/网络条件或运行时限。

程序不创建 Logs。常规更新请求不发送机型、序列号或配置；校色恢复经确认发送序列号。录音由明确操作触发，取消/完成后清理临时文件，声纹数据不写入常规日志。

- **无法启动**：检查 x64 .NET 10、普通版系统运行时、完整 PRI/DLL、UAC，以及是否仍运行另一版本。先使用标准构建脚本重建，不混入另一构建的 DLL。
- **功能不见或禁用**：核对驱动、服务、当前端点、供电、显示模式；区分不支持与读取失败，用仓库内只读诊断收集状态，不通过试写确认。
- **玻璃身份或透明桌面不可用**：先选择原生/壁纸/静态背景；需要手工准备身份时，在已发布的玻璃目录运行 `Start-YogaITS.ps1 -RegisterOnly`。松散身份注册需要系统允许开发者模式，脚本不会替用户更改此设置。
- **迁移/卸载**：先关闭自启并退出，保留数据目录可保留偏好。若要清理玻璃版自身的稀疏身份，可使用下列命令；不删除微软运行时或其他应用。

```powershell
Get-AppxPackage -Name YogaITS | Where-Object Publisher -eq 'CN=YogaITS' | Remove-AppxPackage
```

反馈附程序版本、构建类型、机型、Windows/驱动版本、复现步骤及必要截图；删除输出中的完整序列号、私人录音及其他敏感信息。

## 实机验收

[README 兼容性表](README.md#兼容性与验证) 列出的六款机型均有完善测试记录；重点路径分别为基准功能、旧 ITS、ICC 和 TCON P3/sRGB。小新 Pro 14 IRH8 为显卡工作模式专项验证，换回普通版电脑管家后仍有效，不扩大为整机验收。

| 范围 | 已确认结果 |
| --- | --- |
| Yoga Pro 16 IPH11 的既有功能 | 5.2 全功能回归通过，包含启动/UAC、单实例与托盘、原生窗口/无障碍/DPI/触屏、性能与充电、显卡、人员感知、色彩、音频/声纹、后台刷新及息屏暂停/亮屏恢复保活。 |
| 键盘背光 | Yoga Pro 16 IPH11 实机通过；其他同协议机型仍需对应功能实测。 |
| 显卡模式更新 | Yoga Pro 16 IPH11、小新 Pro 14 IRH8 通过，包含实际模式与偏好分离的路径。 |
| 6.0 玻璃窗口缩放 | 2026-10-26 用户确认四边缩放、按住拖动及松开后停止的修复通过。 |

既有硬件验证不自动覆盖后续界面/后端修改。6.0 双构建的完整功能组合仍需逐项回归；构建、模拟和隐藏窗口检查不能代替实机验收。建议回归顺序：启动/窗口/输入 → 能力显隐及读写确认 → 电源/显示器/耳机切换 → 声纹取消/设备变化 → 更新安装/失败回滚 → 退出及后台助手释放。记录机型、程序版本、前提和观察结果，不使用仓库外测试版本作为当前代码的验收依据。

## 第三方组件与许可

原创部分采用根目录 [LICENSE](LICENSE) 的 **BSD-3-Clause**，分发时须保留版权、许可条件及免责条款。[NOTICE](NOTICE) 说明独立项目、不背书及第三方范围。驱动、服务、固件和校色文件不包含在原创代码授权中。

发布时保留 README 和 NOTICE 中的硬件/系统风险、非官方身份及衍生版本说明；上游许可原文保持不变。

| 组件/来源 | 许可与分发边界 |
| --- | --- |
| LiquidGlassWinUI 1.0.3、LiquidGlassTabBars 动效 | MIT，保留各自 LICENSE；本地材质修改见 [UPSTREAM](ThirdParty/LiquidGlassWinUI/UPSTREAM)。native effect 为固定包内未修改二进制。 |
| Windows App SDK、WinUI、Windows SDK 投影 | 各自微软成品条款及 NOTICE；不能用上游源代码 MIT 标记替代 NuGet 二进制许可。 |
| Win2D 1.3.2 | 源码 MIT 与 NuGet 二进制 EULA 分开保留。包原许可链接失效后取回的微软原始文档和来源/哈希已存入清单，不将二进制改标为 MIT。 |
| .NET 库、C#/WinRT、C++/WinRT | MIT，保留相应第三方声明。 |
| WebView2 SDK 传递依赖 | 保留 BSD 三条款式许可及 NOTICE，不打包浏览器运行时。 |
| [Lenovo Legion Toolkit](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit) | GPLv3，作为协议/设计核对来源，不链接、分发或将其类实现改标为本项目 BSD。核对基准为 master `d0c57bfc`、dev `9baf5a01`；新增复制/改编须重新评估 GPL 边界。 |
| [KeyboardBacklightForLenovo](https://github.com/SaltwaterC/KeyboardBacklightForLenovo) / [gonwan 背光研究](https://github.com/gonwan/ideapad-keyboard-backlight) | 前者 MIT，后者未确认源码许可；使用接口事实及独立实现，不把公开可读视为允许复制。 |
| NVIDIA、联想及音频厂商接口 | 调用本机已安装驱动，不分发 NVAPI/OEM 驱动 DLL、固件或校色文件。 |

[ThirdParty/Licenses/components.json](ThirdParty/Licenses/components.json) 固定组件版本、适用构建、来源、许可原件及 SHA-256，是分发核对依据。许可原件按字节保存，不重排、翻译或清理其内容。新增外部依赖须先登记来源和许可，不凭短片段比较或未引用 DLL 作法律结论。

[Copy-DistributionNotices.ps1](scripts/Copy-DistributionNotices.ps1) 按实际发布资产生成 `ThirdPartyNotices/`，发现未登记版本或许可哈希变化则终止。两版均完整保留 LICENSE、NOTICE、对应清单与原件；四个 YogaITS EXE 标注本项目版权，第三方项目保持原归属。

## 提交约定

使用四空格缩进、文件作用域命名空间及可空注解；类型/公开成员 PascalCase，参数/局部变量 camelCase，私有字段 `_camelCase`。硬件逻辑留在服务，优先复用共享控件和串行路径。

提交标题简明说明行为，例如 `Fix tray recovery after Explorer restart`。PR 写明问题、最终行为、构建/自动检查及实机验证范围，UI 修改附截图；不要提交生成产物、私人配置、序列号或录音。涉及硬件协议、状态机、助手、更新和外部依赖时，同步更新本文及相关检查。
