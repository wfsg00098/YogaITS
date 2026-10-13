# 开发与测试说明

## 项目结构

本仓库为 YogaITS 的功能集成版本，使用 C#、.NET 10 和 WinUI 3，采用非打包、framework-dependent 的 Windows x64 发布方式。

- `YogaITS.Tray/`：应用入口、XAML、项目及管理员运行清单。
- `YogaITS.Tray/Services/`：ITS、充电、独显、状态缓存、启动任务、系统事件及串行操作基类。
- `Services/Color/`、`Services/Elevoc/`、`Services/Hpd/`：色彩、音频、人员感知服务及协议实现。
- `Services/Color/LenovoTcon/`：最终 TCON 探针的 native WMI、SPB session 和色域回读路径。
- `YogaITS.Tray/UI/`：完整/迷你窗口、信息窗口、功能卡片、原生动画、滚动条和辅助功能。
- `YogaITS.Tray/Assets/YogaITS.ico`：编译进 EXE 的图标源，不单独复制到发布目录。
- `Tests/YogaITS.PolicyChecks/`：不操作硬件的逻辑检查。
- `Tests/YogaITS.ReadOnlyProbe/`：能力与状态的只读诊断。
- `main.png`、`mini.png`：README 使用的用户界面截图。

按现有 C# 风格使用四空格缩进、文件作用域命名空间、可空引用类型；类型/公开成员用 PascalCase，参数/局部变量用 camelCase，私有字段用 `_camelCase`。硬件与协议逻辑放在服务中，UI 不直接访问驱动。优先复用共享控件及串行操作路径，不按机型硬编码高级功能。

## 构建与发布

在 Windows x64 上安装 .NET 10 SDK，并准备可还原 NuGet 的网络。运行程序还需要兼容的 .NET Desktop Runtime、Windows App Runtime、联想驱动/服务及管理员权限。依赖版本以 `YogaITS.Tray.csproj` 为准。

以下 PowerShell 命令从仓库根目录执行：

```powershell
dotnet restore .\YogaITS.Tray\YogaITS.Tray.csproj
dotnet build .\YogaITS.Tray\YogaITS.Tray.csproj -c Debug
.\build-clean.ps1
.\dist\YogaITS.Tray.exe
```

`build-clean.ps1` 删除本项目的 `bin/`、`obj/`、`dist/`，还原并发布 Release，验证 `YogaITS.Tray.pri`，移除 PDB/XML 和中间产物。执行前退出程序，避免发布文件被占用；`dist/` 中如有需要保留的诊断日志，先备份。

只更新发布输出而不清理的命令：

```powershell
dotnet publish .\YogaITS.Tray\YogaITS.Tray.csproj -c Release -r win-x64 --self-contained false -o .\dist
```

非打包 WinUI 需要应用 PRI。项目中的 `AddProjectPriToResolvedFileToPublish` 处理发布资源遗漏；交付必须保留整个发布目录。图标通过 `ApplicationIcon` 内置，窗口和托盘从 EXE 提取，句柄按生命周期释放，不生成临时 ICO 或额外 Assets 目录。

## 架构与刷新

四组 probe 后端整合为独立服务，无常驻辅助进程，不依赖厂商 RPC 客户端 DLL。`SerializedFeatureService` 串行执行同一服务的读、写和关闭；首次检测及不同服务之间可并行。只由用户操作或已选择的显卡智能策略触发写入，不通过试写检测能力，无常态轮询。

`MainWindow` 是隐藏的常驻消息宿主。`TrayMenuWindow` 共用完整/迷你窗口的核心功能；`FeatureCard`、`FeaturePanels` 提供新增功能。开窗读取缓存，获得焦点时刷新，避免重复叠加刷新批次。后台刷新不抢焦点。

普通启动打开完整窗口；独立 `--startup` 参数只驻留托盘。单实例通过当前用户会话的命名事件传递普通启动的开窗请求；再次 `--startup` 不影响当前窗口。旧版与集成版共用单实例标识和启动任务名称。

常驻 Win32 窗口监听 AC/DC、电源状态、睡眠恢复、显示拓扑和设备节点变化。显卡与色彩状态独立于窗口是否打开、是否获焦刷新；显示/色彩事件采用 750 ms 防抖。音频设备变化只刷新能力与状态，不改写服务的偏好模式。

## 能力检测与硬件约束

| 功能 | 检测与行为约束 |
| --- | --- |
| ITS 性能选项 | 保留型号筛选和现代服务版本门槛；SMART_POWER 查询 2 成功且含 `0x1` 有效位才显示。 |
| 极客/创作模式 | 查询 10 满足 `0x00020001`，且 `ITS_CurrentSettingV` 是可读 DWORD/int；不回退旧字段，不要求当前值为 4。电池/未知供电时保持可见但禁用，写入前再次检查 AC。 |
| 显卡工作模式 | `IsSupportIGPUMode.Data > 0`；实际状态来自 `GetIGPUModeStatus`，逻辑模式由应用保存。集显选择要求 `GetIGPUMHDMIStatus == 0`，未知也禁用。 |
| 显卡智能策略 | 电源通知必须可用；AC 目标双显，DC 且外接独显路径未占用时目标集显，供电未知不切换。DC 临时失败最多重试 5 次、间隔 5 秒；新事件或手动选择取消旧重试。 |
| 色域 | 内屏、HWID、动态 WCS DeviceID、机器身份和亮度通道可读取。Factory 缺失是可恢复状态；ICC CxF 必须匹配序列号、面板及 Machine Type。 |
| HDR/ACM | 卡片及色域保护使用实际 `activeColorMode`，不使用偏好位代替实际状态。HDR 仅在 `HighDynamicRangeSupported` 为真时显示；ACM 按自身能力/启用偏好显示。未知颜色状态保守阻止写入。 |
| 唤醒/睡眠 | `GetAll rc=0` 且 `unAwareAbility` 非零。人员辅助判定还要求 `sensor_type != 4` 和 `capability & 0x40`。 |
| 播放/暂停 | `GetAll rc=0`，且 `sensor_type` 为 3/4 或 `unAwareAbility` 非零。 |
| 音频降噪 | 当前 Capture/Render endpoint 声明 `uplink/downlink`，并且对应 `GetMode` 返回 OK。 |
| 多人/定位/声纹 | 仅内置阵列麦克风；多人模式按端点类型限制，人声定位还要求 `special`，声纹要求 `speaker_extraction` 与声纹查询正常。 |

ITS 校验参考 [LLT ITSModeFeatureDriver](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/ITSModeFeatureDriver.cs)。EnergyDrv 查询返回长度必须是 4 字节，失败保守隐藏，不启用实验性驱动写入。现有服务控制命令保持不变。

显卡 WMI 直接对枚举实例调用方法，不依赖 ReturnValue，设置后等待并回读。NVIDIA 存在性随后台刷新更新，切至集显后独显休眠分组可动态隐藏；独显操作返回 Task，执行期间显示强调色并防止重复点击，结束后清除。

夜间优化与三个充电模式互斥：选择夜间优化先开启夜间标志，再设常规模式；选择其他模式先关夜间标志，再写目标。共享串行入口，失败停止后续写入，最后一起回读并一次更新缓存。

色域模式来自 STANDARD，不自动恢复旧 ICC。保留 managed ICC 注册、字节校验、association、STANDARD/WCS 回读、可选亮度及 DWM 刷新；Factory 源不修改。按钮文案映射如下：

| 配置标识 | 用户名称（颜色配置） |
| --- | --- |
| Default | 出厂默认（D65） |
| Native | 鲜艳（Native） |
| sRGB | 网页办公（sRGB-D65） |
| DisplayP3 | 摄影/数码影院（P3-D65） |
| DCIP3 | 原生数码影院（DCI-P3） |
| AdobeRGB | 摄影印刷（Adobe-RGB-D65） |
| REC709 | SDTV视频（REC709） |

可附加 ICC 建议亮度。HDR/ACM 写入只反转启用偏好，随后刷新实际模式；HDR 不覆盖 Windows 的电池策略，也不要求实际模式立即等于偏好。实际 SDR 可切色域，即使电池导致 HDR 暂时关闭但偏好仍为真。实际 HDR/ACM 时隐藏色域、亮度联动和恢复操作，只显示不适用提示；恢复 SDR 后重新显示。

HPD 保留 ContextCreate/ContextClose 生命周期，检查设置返回码并回读；不暴露距离或 YogaMode。自动睡眠关闭时隐藏速度与人员辅助判定。自动唤醒/睡眠、播放/暂停仅开启时显示对应说明。

## TCON 与 ICC 自动选择

`ColorGamutService` 统一选择 backend；不按 ThinkBook/Yoga 名称或代际硬编码。内屏存在后先执行 TCON native WMI preflight，只有 `LENOVO_TCON_DATA`、`SetTconSwitch`、实例、live `F3 00` 总能力/模式位和 `F0 1D` GET 均通过才选择 TCON。类不存在立即返回，不打开 SPB。已验证的 ICC 文件仍可作为非 TCON 路径；TCON 检测不确定且没有有效 ICC 时，不提供盲目的 ICC 在线恢复。

TCON 模式位为 `0x0100=P3`、`0x0200=sRGB`、`0x0400=Adobe RGB`，必须同时有总能力 `0x0008`。raw 为 `0x04/0x06/0x08`，其他值保持未知。UI 仅按能力显示对应按钮；TCON 不提供 ICC 亮度联动或恢复，HDR/ACM 状态判断与原 ICC 路径共用。TCON 模式 ID 与 ICC ID 分离，旧 UI 回调不能跨 backend 写入。

应用入口先执行最终探针的 `ProcessComSecurity.InitializeEarly()`，以 DEFAULT/IMPERSONATE/EOAC_NONE 配置进程级 COM 安全，再启动 SDK 生成的 WinUI 主循环，退出时平衡主线程 COM。只读诊断入口同样在任何 WMI 之前初始化。不能仅在 TCON worker 中补调：此时 RPC_E_TOO_LATE 只说明进程安全已经固定，不能证明原设置适合实例枚举。

G7+ 的初次集成反馈日志显示类和方法存在，但实例枚举 Next（含 enum blanket 重试）返回 `WBEM_E_ACCESS_DENIED`，尚未访问 SPB；排查发现遗漏了探针入口的早期初始化。补齐后，本机主程序及只读诊断日志均确认进程级 `CoInitializeSecurity=S_OK`，Yoga ICC 检测仍正常；G7+ 修正后需再次实机验收。

每个事务保留 `Global\\TconDriverConnectorParallelMutex`、独占 SPB handle、`SetTconSwitch` gate、`0x04001C00` 初始化与 `0x04001C10` 读/WriteFile 顺序。WMI、COM、mutex 在同一 worker 线程创建/使用/释放。按用户要求仅将探针的无限枚举/mutex 等待替换为 30 秒后台等待；其他 native WMI/transport/protocol 代码保持探针原样。初始化结果、gate OFF、bytesReturned/bytesWritten 不额外阻断，仍由原探针的 live capability、GET、WriteFile 成功和最终回读判定。设置前再次验证目标 mode bit 与当前 GET；写入后最多回读 6 次、间隔 30 ms，匹配才成功。

独立探针在 ThinkBook 14 G6+ AHP、G7+ IAH 上验证 P3/sRGB；Yoga Pro 16 IPH11 为 TCON 负样本。集成版需对应 ThinkBook 再验收，不将原先 Yoga UI 全通过记录当作新增 TCON 写入已通过。Adobe RGB 的验证限制只写在 README，应用不显示测试备注。

## 音频与声纹安全

Elevoc 使用自建 Direct RPC：RPCRT4、`ncalrpc:ElevocRpcEndpoint`、NDR32，启动执行只读 ABI 自检。不加载 ElevocUAPO.dll，不依赖版本、SHA256 或 RVA。Core Audio 使用完整 x64 PROPVARIANT（24 字节，指针偏移 8），内置阵列按 JackSubType 的 MIC_ARRAY/PROCESSING_MIC_ARRAY 判断，不靠设备名猜测。

麦克风模式为 `environmental/private/shared/special/voiceid`。关闭统一写入 `environmental`，不区分耳机/内置；`off` 仅保留旧状态恢复兼容。设置后等待 150 ms 并回读，不匹配提示请求/实际模式和设备。耳机不提供多人通话；仅听机主只在 private/voiceid 下显示，人声定位只在 shared/special 下显示，且均要求内置麦克风及对应能力。未注册声纹时仅听机主禁用。

声纹注册顺序：保存原模式 → private → 48 kHz/20 秒录音 → mono FLOAT32 WAV → SetSpeakerID(file) → registered → voiceid → 有效 data[192] → 最终复核。不修改 timestamp，不依赖 debug PCM 或声纹磁盘格式；private 返回 metadata-only 不视为声纹丢失。

录制/注册期间禁用麦克风及扬声器降噪，所有音频操作共用原来的串行队列。实测录制期间并发操作扬声器可令 Elevoc 状态异常，已撤回独立 downlink RPC 通道；不要重新引入。用户确认重启本机 `ElevocService` 即可恢复此异常：先停止录制并退出应用，再重启服务、重新启动程序，不自动替用户重启服务。

录制按钮复用为停止录制，朗读示例只在录制期间显示且位于状态下方；倒计时保留最新进度，其他面板刷新不重置。停止丢弃本次录音，不提交声纹；提交阶段按钮禁用。删除前先关闭仅听机主并通过回读验证，再删除数据。设备变化取消录制，不向新端点恢复旧模式。

WinMM 清理按 Stop → Reset → Unprepare → Close → 释放内存；任一步失败保留驱动可能仍引用的内存，不强行释放。正常结束检查缓冲区长度和 Reset 结果。注册失败尽力恢复原模式，完成/失败/取消后清理临时文件。

## UI 与辅助功能

- 大窗口为可滚动纵向分组，默认宽度 492 DIP；四周 Padding 8 DIP，组标题 14 DIP SemiBold，卡片最小高 48 DIP、字体 12.5 DIP。同排按钮 Stretch、允许换行，不固定高度截断文字。
- 小窗口提供性能、充电、独显休眠和独立退出；无型号、杂项、自启、开盖开机。退出最小高 28 DIP，宽度与功能组总宽度对齐。
- 分组只用独立灰色分割线，按钮无静态填充，灰色/强调色边框表示状态。原生指针各状态共享同一个 Outline 画刷，修改 Color 即时刷新，不替换 Style。等待回读保留原选中项，给请求项加待确认边框，说明确认后才切换。
- 复用按钮、网格、说明容器及最新回调，操作期间保留状态行，不临时禁用普通按钮导致焦点跳转；通过忙碌标志阻止重复执行。真实能力/录制限制仍禁用。开盖开机只有操作后追加重启提示。
- 两窗口使用原生 MicaBackdrop、透明根背景，由系统处理获焦/失焦过渡和纯色回退；大窗口只获焦时强制强调色细边框，失焦恢复系统默认，小窗口保持原有边框行为。
- 大窗口内容延伸至标题栏、拖动区到顶，保留主题适配的系统控制按钮，按 Inset/DPI 避让。标题获得初始焦点但不画焦点框，随后 Tab 从首个可用按钮开始；开窗请求前台，后台更新不抢焦点。
- RepositionThemeTransition 移动组/分割线，ContentThemeTransition 只用于实际变化的说明，BrushTransition 100 ms 提供悬停高光，不缩放。遵循系统动画开关，主题次要文字不叠加 Opacity。
- 滚动条首次提示约 1 秒，滚动及触屏惯性期间显示，结束后约 180 ms 隐藏，原生淡入/淡出；悬停/拖动保持可见。普通模式放在右侧 8 DIP 留白内；系统始终显示滚动条时保留原生宽度并额外避让，不覆盖分割线。
- 讲述人名称包含当前功能分组，例如“性能选项中的性能模式”；选项状态及完成/错误通知可读，录制倒计时不逐秒播报。支持 Tab/Shift+Tab/空格、Esc、触屏及高对比度。

桌面上直接订阅 AccessibilitySettings.HighContrastChanged 曾抛出 `0x80070490`。使用常驻窗口的 WM_SETTINGCHANGE/WM_THEMECHANGED 更新对比度及滚动条偏好，保留已验证的 UISettings.ColorValuesChanged；不要重新引入不兼容的事件订阅。框架主题资源不通过 Application.Resources 索引直接读取。

## 配置、日志与隐私

- `%LOCALAPPDATA%\YogaITS\settings.json`：显卡逻辑模式及色域亮度偏好。
- `%LOCALAPPDATA%\YogaITS\ColorCalibration\Factory\`：验证后的工厂 ICC 源文件。
- 发布目录的 `Logs/ui-errors.log`：窗口构造和未处理 XAML 异常，日志失败不影响主流程。
- 发布目录的 `Logs/color-diagnostics.log`：TCON WMI、session、IO 返回缓冲区及高级颜色状态诊断；重复检测内容去重，不记录序列号。
- 在线恢复弹确认框，默认取消；仅确认后向 Lenovo 发送序列号。下载使用 `Accept: */*`，下载与暂存副本均校验后替换缓存。不能给 ZIP 强制使用 JSON-only Accept。
- 录音只由明确用户操作触发；不将声纹向量或完整序列号写入常规日志。提交前检查端点身份与取消状态，删除声纹需二次确认。

## 自动检查与只读诊断

```powershell
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release -- --icon-executable .\dist\YogaITS.Tray.exe
dotnet run --project .\Tests\YogaITS.ReadOnlyProbe -c Release
```

最近一轮带图标验证的 PolicyChecks 为 **520 项通过**。直接复用原探针 SupportsMode 验证目标能力位，并覆盖未知模式/后端选择矩阵、ITS/供电能力、显卡策略、HPD 能力、HDR/ACM 实际状态与偏好、电池策略、结构大小/偏移、ICC 身份与亮度、声纹数据、串行读写/关闭、启动参数、DPI/工作区及录制文案等。逻辑检查不操作真实硬件、注册表、麦克风或网络；不带图标参数时检查数量不同。

2026-10-13 TCON 集成后，本机只读诊断确认选择 `Icc`，7 个有效工厂模式及 HDR 实际状态读取正常；未执行 TCON/ICC 色域写入、HDR 切换、音频设置、录音或在线下载。非管理员 GPU 探测仍为拒绝访问，不能据此判定能力不存在。

ReadOnlyProbe 并行读取四组能力与状态，不调用 setter、不录音、不下载。故意不调用 GPU InitializeAsync，避免保存的 Auto 策略触发写入；建议管理员运行，拒绝访问不能据此判定能力不存在。

可选网络诊断：

```powershell
dotnet run --project .\Tests\YogaITS.ReadOnlyProbe -c Release -- --check-calibration-http
```

该选项发送本机序列号，比较 Lenovo 元数据接口的 Accept 请求头，不下载 ZIP、不修改缓存。只有明确同意发送序列号时才能执行。自动检查与只读探测不能替代写入及 UI 实机验收。

## 实机验收记录

**验收设备：Yoga Pro 16 IPH11。用户确认本机全部实机验收项目通过。** 以下为用户实测结论，不是自动化测试的硬件操作记录；其他机型尚未验收，不将本机结果推广为全系列兼容性承诺。

- [x] 启动/UAC、单实例、普通启动与开机静默启动、托盘左右键、左右窗口同步及 Explorer 重启恢复。
- [x] 大小窗口首次/反复打开、关闭/退出、前台焦点、标题拖动/最大化及控制按钮，不报错、不闪退。
- [x] 亮/暗主题、Mica 获焦/失焦、透明效果回退、四种高对比度主题、强调色及悬停/布局/说明动画。
- [x] DPI 100%/150%/200%、文字大小 100%/150%/225%、窄窗口换行/同排等高、长信息窗口滚动/复制。
- [x] 滚轮、键盘、触屏惯性、滚动条显隐与始终显示偏好，分割线不被覆盖，关闭系统动画后的行为。
- [x] Tab/Shift+Tab/空格焦点保留、Esc、讲述人分组名称和状态、完成/错误通知，录制倒计时不反复播报。
- [x] 性能/充电切换、夜间优化互斥、极客/创作模式电池禁用、开盖开机及重启生效提示。
- [x] 独显进程处理/设备重启、显卡双显/集显/智能、AC/DC 与外接屏事件、路径保护和 Auto 恢复。
- [x] 唤醒/睡眠、速度、人员辅助、播放/暂停开关及回读、自动睡眠子项显隐。
- [x] HDR/ACM 实际状态、电池 HDR 偏好、色域/亮度切换、色域操作显隐、在线恢复与校色身份保护。
- [x] 麦克风/扬声器模式、耳机能力变化、多人/定位/仅听机主显隐、设备变化不覆盖偏好。
- [x] 20 秒声纹录制、停止丢弃、设备变化取消、重新注册/读取/删除、录制期间降噪禁用及提示保留。
- [x] 本机能力动态显示/隐藏、恢复后重新检测、焦点刷新不叠加、失焦/最小化/关闭后的后台状态刷新。

跨机型验收仍须由对应硬件用户执行；ThinkBook 等不同平台需重点复核 ITS 扩展模式、传感器、面板及音频端点能力。

## 提交与维护

使用简明祈使句提交标题，例如 `Fix tray recovery after Explorer restart`。提交说明应区分 UI 变化、硬件写入与只读检查；PR 提供行为变化、相关问题、构建/硬件验证记录和 UI 截图，不提交生成产物或敏感日志。保留可回滚的独立提交，涉及驱动、声纹或校色写入时明确验证范围。
