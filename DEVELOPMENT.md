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

仅编译主程序（不包含可交付的更新助手）：

```powershell
dotnet publish .\YogaITS.Tray\YogaITS.Tray.csproj -c Release -r win-x64 --self-contained false -o .\dist
```

非打包 WinUI 需要应用 PRI。项目中的 `AddProjectPriToResolvedFileToPublish` 处理发布资源遗漏；交付必须保留整个发布目录。图标通过 `ApplicationIcon` 内置，窗口和托盘从 EXE 提取，句柄按生命周期释放，不生成临时 ICO 或额外 Assets 目录。

## GitHub Actions 自动构建与 Release

`.github/workflows/build-release.yml` 在推送 `main` 时运行。Windows 2025 runner 使用 .NET 10，复用 `build-clean.ps1`，验证 PRI 与内置图标并运行无硬件逻辑检查，再将完整发布目录压缩上传为 artifact（保留 30 天）。构建或检查失败不会发布 Release；CI 不启动程序，不运行硬件探针、不录音或改硬件设置。

根目录 `VERSION` 保存人工维护的 `x.y`（当前 `5.1`），不再从数字分支推算。发布名、tag、ZIP 统一为 `x.y-yyyyMMddHHmmss-hash`，时间取提交的 committer 时间并转为 UTC，hash 为 SHA 前 8 位；同一提交重跑名称不变。程序内嵌完整 SHA，更新比较使用 GitHub latest 的版本/提交身份而非字符串排序，兼容旧 tag。`scripts/Get-ReleaseVersion.ps1` 及 `Tests/ReleaseVersionChecks.ps1` 验证规则。

版本验收后，从当前 main 创建并推送对应 `x.y` 分支封存，再在 main 修改 VERSION 开始下一版本；数字分支不自动移动，推送它们不触发 Actions。`4.0` 已封存至 `ea2bbf7`；`5.0` 已封存至 `4ece4e7`（包含双路更新竞速）。

发布 job 仅在构建成功后使用 `GITHUB_TOKEN` 的 `contents: write` 权限，以完整提交 SHA 为 tag 目标自动创建 Release 并附带 ZIP，不需要额外 secret 或 GPG 私钥。Actions 固定到官方提交 SHA。重复运行已有版本只更新附件，并验证标签目标相同；不重写提交或移动已有标签，不影响已签名提交的 Verified。

发布前及上传后核对远端 main，只有仍对应 main 的发布才标记 latest，避免旧构建晚完成覆盖最新版。完整交付必须使用 `build-clean.ps1`，该脚本另发布 `YogaITS.Updater`；不要只复制主程序的发布输出。

## 自动更新链路

`AppUpdateService` 启动后延迟 3 秒检查一次，此后每 24 小时检查；仅大窗口“杂项”分组出现单个更新卡片，位于分组标题下、开机自启/退出上方。内层不统一施加位移过渡，而由 `SettingsMotion.RevealBefore` 分别控制：更新卡片填充文字后以零偏移 EntranceThemeTransition 原地淡入，已有按钮行使用 RepositionThemeTransition 平滑移动，避免首次显现从窗口原点滑入或下方按钮瞬移；遵循系统动画开关。后台检查静默失败，不接触硬件或私有配置。点击后使用默认取消的 ContentDialog 确认；确认的版本固定，检查与下载串行，不重复安装。

`GitHubUpdateClient` 同时请求固定仓库 `wfsg00098/YogaITS` 的 `/releases/latest` 与 `https://gh-proxy.org/` 对应代理地址，采用最先解析校验成功的结果，随即取消另一条请求。一条线路失败不抢占另一条仍在进行的有效请求；两条均失败才报错。元数据请求上限均为 20 秒。下载也双路同时进行，各用独立随机 `.part` 文件，先完整下载且通过大小与 SHA-256 校验的包胜出；取消并等待另一条释放文件句柄后发布获胜包、清理临时文件。下载响应头/读取空闲上限均为 20 秒，总时限各 8 分钟，不再按低速阈值切换线路。保留 HttpClient 默认系统代理行为，不发送凭证、序列号或配置。只接收正式版、预期仓库/文件名的 x64 ZIP、合理大小及 GitHub SHA-256 digest。第三方代理同时承载元数据与包时仍存在代理信任边界，SHA-256 并不等价于发行者数字签名。确认对话框不展示下载线路说明。

更新暂存与旧文件备份位于 `%LOCALAPPDATA%\YogaITS\Updates\<GUID>\`，不写 Logs。拒绝 ZIP 路径穿越、重复路径、缺少必需文件及过大展开内容。助手从暂存目录运行，按 PID 和启动时间等待原进程退出，再从原 ZIP 重新验 hash/解压、备份被覆盖文件、复制及逐文件验 hash，最后重新启动原路径。复制、校验或启动失败回滚；回滚不完整时保留备份并明确提示。未运行常驻更新服务，不修改计划任务或硬件设置。

自动检查覆盖模拟 HTTP 双路竞速/取消/坏包、版本身份、ZIP 路径安全、临时目录安装/配置保留及回滚。完整 UI 更新和真实程序重启仍需实机验收。

## 架构与刷新

四组 probe 后端整合为独立服务，无常驻辅助进程，不依赖厂商 RPC 客户端 DLL。`SerializedFeatureService` 串行执行同一服务的读、写和关闭；首次检测及不同服务之间可并行。只由用户操作或已选择的显卡智能策略触发写入，不通过试写检测能力，无常态轮询。

`MainWindow` 是隐藏的常驻消息宿主。`TrayMenuWindow` 共用完整/迷你窗口的核心功能；`FeatureCard`、`FeaturePanels` 提供新增功能。开窗读取缓存，获得焦点时刷新，避免重复叠加刷新批次。后台刷新不抢焦点。

普通启动打开完整窗口；独立 `--startup` 参数只驻留托盘。单实例通过当前用户会话的命名事件传递普通启动的开窗请求；再次 `--startup` 不影响当前窗口。旧版与集成版共用单实例标识和启动任务名称。

开机自启查询 `Yoga ITS Tray` 任务 XML，任务必须启用且 Exec 路径与当前 EXE 一致；忽略大小写，规范化引号及绝对路径，旧安装路径或不可读任务显示关闭。开启时串行执行删除旧任务 → 创建当前路径任务（保留 `--startup`、最高权限、交互登录）→ 回读。删除失败只在 HRESULT 确认任务不存在时继续，创建不用 `/F` 覆盖竞态出现的任务。关闭也删除路径不匹配的旧任务；任何结果按实际回读更新 UI/缓存，避免删除成功、重建失败后错误保留开启状态。查询等待限 10 秒，异步操作限 30 秒；逻辑测试仅解析样例 XML，不改真实任务。

创建任务使用显式 XML：允许电池供电启动、切到电池不停止，无空闲/网络前提，空闲结束不停止；任务和登录触发器的 `ExecutionTimeLimit` 均为 `PT0S`（无限制）。只在当前用户交互登录时启动并保持最高权限；不依赖 schtasks 默认的交流电条件或 72 小时上限。临时 XML 创建完成后才删除旧任务，注册后清理临时文件。

常驻 Win32 窗口监听 AC/DC、电源状态、睡眠恢复、显示拓扑和设备节点变化。显卡与色彩状态独立于窗口是否打开、是否获焦刷新；显示/色彩事件采用 750 ms 防抖。音频设备变化只刷新能力与状态，不改写服务的偏好模式。

## 能力检测与硬件约束

| 功能 | 检测与行为约束 |
| --- | --- |
| ITS 性能选项 | 保留型号筛选；Dispatcher Version ≥8192 走现代服务，查询 2 成功且含 `0x1` 才显示。较低/缺失版本走 LITSSVC，要求旧 MMC 状态键和 EnergyDrv 存在，不套用现代查询 2 门槛。 |
| 极客/创作模式 | 查询 10 满足 `0x00020001`，且 `ITS_CurrentSettingV` 是可读 DWORD/int；不回退旧字段，不要求当前值为 4。电池/未知供电时保持可见但禁用，写入前再次检查 AC。 |
| 显卡工作模式 | `IsSupportIGPUMode.Data > 0`；实际状态来自 `GetIGPUModeStatus`，逻辑模式由应用保存。集显选择要求 `GetIGPUMHDMIStatus == 0`，未知也禁用。 |
| 显卡智能策略 | 电源通知必须可用；AC 目标双显，DC 且外接独显路径未占用时目标集显，供电未知不切换。DC 临时失败最多重试 5 次、间隔 5 秒；新事件或手动选择取消旧重试。 |
| 色域 | 内屏、HWID、动态 WCS DeviceID、机器身份和亮度通道可读取。Factory 缺失是可恢复状态；ICC CxF 必须匹配面板及有效的序列号、Machine Type。仅当后两者同时为 `INVALID` 时接受 Lenovo 的面板级配置，不放宽真实身份不匹配或空字段校验；恢复与实际切换共用校验逻辑。 |
| HDR/ACM | HDR 卡片及色域保护使用实际 `activeColorMode`，ACM 卡片使用独立 `wideColorUserEnabled` 设置。HDR 仅在 `HighDynamicRangeSupported` 为真时显示；ACM 按自身能力/启用偏好显示。未知颜色状态保守阻止色域写入。 |
| 唤醒/睡眠 | `GetAll rc=0` 且 `unAwareAbility` 非零。人员辅助判定还要求 `sensor_type != 4` 和 `capability & 0x40`。 |
| 播放/暂停 | `GetAll rc=0`，且 `sensor_type` 为 3/4 或 `unAwareAbility` 非零。 |
| 音频降噪 | 当前 Capture/Render endpoint 声明 `uplink/downlink`，并且对应 `GetMode` 返回 OK。 |
| 多人/定位/声纹 | 仅内置阵列麦克风；多人模式按端点类型限制，人声定位还要求 `special`，声纹要求 `speaker_extraction` 与声纹查询正常。 |

ITS 校验参考 [LLT ITSModeFeature](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/ITSModeFeature.cs) 及 [ITSModeFeatureDriver](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/ITSModeFeatureDriver.cs)。只按 `LenovoProcessManagement\Performance\PowerSlider\Version` 的 8192 界限选择路径，现代命令失败不回退旧服务。EnergyDrv 查询返回长度必须是 4 字节；未接入 LLT 的 ThinkBook 实验性整套驱动切换。

旧版通过 `LITSSVC`：智能发 135，安静依次 134/146，性能依次 134/148。回读 `LITSSVC\LNBITS\IC\MMC` 的 `(AutomaticModeSetting, CurrentSetting)`，仅 `(2,0)`、`(1,1)`、`(1,3)` 分别识别为智能、安静、性能，其他组合未知。写入串行化，最长约 3 秒、每 200 ms 回读；不直接写注册表，也不强制更改 Windows 电源模式。现代 163/164/165/172 命令和既有能力检测保留。

旧版第四档按 LLT 查询 10 的 `0x00020001` 位检测，不要求现代 `ITS_CurrentSettingV`。开启时先设性能再发 `0x001F100B`，其他模式前尽力发 `0x000F100B` 清除旧极客覆盖；仍要求插电才能开启第四档。旧服务本身只回读三档，第四档标记与 LLT 一样仅在进程内跟踪，重启后或管家外部切换无法可靠区分，不宣称硬件独立回读第四档已验证。

`dotnet run --project Tests/YogaITS.ReadOnlyProbe -- --its-only` 仅输出机型、后端、版本及模式/旧字段，便于在 IAH7 上比较管家切换前后；不发送任何设置命令。纯逻辑测试覆盖路径边界、指令顺序、双字段组合和第四档能力差异。2026-10-18：用户确认旧路径性能选项切换已在小新 Pro IAH7 实机测试成功；不据此扩大为该机型全部功能或所有旧款机型均已验证。

显卡 WMI 直接对枚举实例调用方法，不依赖 ReturnValue，设置后等待并回读。NVIDIA 存在性随后台刷新更新，切至集显后独显休眠分组可动态隐藏；独显操作返回 Task，执行期间显示强调色并防止重复点击，结束后清除。

结束独显占用进程对齐 LLT 的批量路径：NVAPI V2 活动进程列表按原始进程名忽略大小写排除 `dwm.exe`、`explorer.exe`，没有 NVIDIA 容器、SYSTEM 或 Session 0 排除；另保留本程序与无效 PID 的防护、PID 去重。逐个 `Kill(entireProcessTree: true)` 后异步等待根进程退出，单项异常不中断后续目标；撤销额外的 Enter/LeaveDebugMode 和 10 秒退出超时。NVAPI 查询失败按异常处理，不伪装为空列表。大小窗口保留 500 ms 执行前强调色提示。只有 LLT 的单项强制结束菜单允许直接操作桌面进程，本项目不提供该入口。

参考固定版本 [LLT GPUController](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/d0c57bfc7ecc26d4ccf15bb3835fed7ac28a259f/LenovoLegionToolkit.Lib/Controllers/GPUController.cs) 与 [PhyscialGPUExtensions](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/d0c57bfc7ecc26d4ccf15bb3835fed7ac28a259f/LenovoLegionToolkit.Lib/Extensions/PhyscialGPUExtensions.cs)。NVAPIWrapper 的 V2 结构大小、容量 128 和查询失败语义也已核对。逻辑检查覆盖桌面进程大小写排除及容器保留，不实际结束进程；SYSTEM 容器与进程树处理需实机验收。NVAPI 返回数量不等同于 NVIDIA 托盘的可见应用数量，不能据此断言应全部结束。

可选 `dotnet run --project Tests/YogaITS.PolicyChecks -c Release -- --gpu-process-tree` 验证结束进程树，仅创建并结束该测试自己的父/子进程，不访问 NVAPI 或当前系统占用进程；默认 CI 不运行此选项。

入口在启动 WinUI 前为当前 EXE 路径注册 `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` 的 `GpuPreference=1;` 节能默认值。已有显卡偏好（包括让 Windows 决定）不覆盖，其他图形选项保留，不改全局或其他程序；注册失败不阻止启动。该注册表约定不是公开的进程级强制 GPU API，首次注册可能需下次启动生效，不自动重启；实际适配器由 Windows/驱动决定，单显卡自然回退。纯逻辑测试不读写注册表，双显卡分配需实机验证。

夜间优化与三个充电模式互斥：选择夜间优化先开启夜间标志，再设常规模式；选择其他模式先关夜间标志，再写目标。共享串行入口，失败停止后续写入，最后一起回读并一次更新缓存。

色域模式来自 STANDARD，不自动恢复旧 ICC。保留 managed ICC 注册、字节校验、association、STANDARD/WCS 回读、可选亮度及 DWM 刷新；Factory 源不修改。按钮文案映射如下：

ACM 卡片仅按 `WideColorSupported || WideColorUserEnabled` 显示和允许操作，不因实际 HDR 或 `LimitedByPolicy` 禁用。HDR/ACM 请求均只修改对应偏好，随后回读实际状态；不主动关闭另一个功能、不修改 Windows 电源策略。能力不存在仍不允许开启；系统 API 拒绝请求或偏好回读不匹配仍报告真实错误。

ACM 卡片选中状态读取 `wideColorUserEnabled`，不能用 `activeColorMode == WCG` 代替：HDR 期间有效模式为 HDR，无法同时用枚举表示独立 ACM 选项。HDR 卡片仍使用实际 HdrActive；色域阻断/显隐仍使用实际 SDR/WCG/HDR，不能被保存的 ACM 偏好改变。

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

2026-10-18：用户确认小新 Pro 16 IAH10 的在线恢复校色文件及 ICC 色域切换实机通过。联想服务器的 LEN8BAD 七档包使用成对 `INVALID` 机型/序列号占位；仅对这一明确组合按屏幕 HWID 验证，正常逐机文件仍严格匹配身份，恢复与切换共用校验。

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

## 配置与隐私

- `%LOCALAPPDATA%\YogaITS\settings.json`：显卡逻辑模式及色域亮度偏好。
- `%LOCALAPPDATA%\YogaITS\ColorCalibration\Factory\`：验证后的工厂 ICC 源文件。
- 程序不创建 `Logs` 目录或写入日志文件；错误仍通过界面提示，原生 TCON 诊断回调采用无输出适配，不改变探针链路行为。
- 在线恢复弹确认框，默认取消；仅确认后向 Lenovo 发送序列号。下载使用 `Accept: */*`，下载与暂存副本均校验后替换缓存。不能给 ZIP 强制使用 JSON-only Accept。
- 录音只由明确用户操作触发；不将声纹向量或完整序列号写入常规日志。提交前检查端点身份与取消状态，删除声纹需二次确认。

## 自动检查与只读诊断

```powershell
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release -- --icon-executable .\dist\YogaITS.Tray.exe
dotnet run --project .\Tests\YogaITS.ReadOnlyProbe -c Release
```

最近一轮带图标验证的 PolicyChecks 为 **564 项通过**。直接复用原探针 SupportsMode 验证目标能力位，并覆盖 ACM 操作能力/设置与有效模式独立性/策略位组合、未知模式/后端选择矩阵、ITS/供电能力、显卡策略、HPD 能力、HDR/ACM 实际状态与偏好、电池策略、结构大小/偏移、ICC 身份与亮度、声纹数据、串行读写/关闭、启动参数、DPI/工作区及录制文案等。逻辑检查不操作真实硬件、注册表、麦克风或网络；不带图标参数时检查数量不同。

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
