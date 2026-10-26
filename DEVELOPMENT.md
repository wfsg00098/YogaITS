# 开发与测试说明

## 项目结构

本仓库为 YogaITS 的功能集成版本，使用 C#、.NET 10 和 WinUI 3，采用非打包、framework-dependent 的 Windows x64 发布方式。

- `YogaITS.Tray/`：应用入口、XAML、项目及管理员运行清单。
- `YogaITS.Tray/Services/`：ITS、充电、独显、状态缓存、启动任务、系统事件及串行操作基类。
- `Services/Color/`、`Services/Elevoc/`、`Services/Hpd/`：色彩、音频、人员感知服务及协议实现。
- `Services/Color/LenovoTcon/`：最终 TCON 探针的 native WMI、SPB session 和色域回读路径。
- `YogaITS.Tray/UI/Shell/`：完整/迷你窗口、信息窗口及 Win32 窗口辅助。
- `YogaITS.Tray/UI/Controls/`、`Layout/`、`Animation/`、`Accessibility/`、`Features/`、`Presentation/`：两种构建共用的控件、排版、原生动效、无障碍、功能分组及材质适配入口。
- `YogaITS.Tray/UI/Glass/`：可整体排除的玻璃实现，按 `Controls/Animation/Layout/Rendering/Backgrounds/Capture/Runtime/Settings/Windowing` 划分；捕获 native bridge 与 HDR shader 位于对应组件内。
- `ThirdParty/LiquidGlassWinUI/`：固定版本的第三方材质项目、shader、LICENSE 和 UPSTREAM；`ThirdParty/LiquidGlassTabBars/` 保留参考动效的许可。
- `YogaITS.Tray/Assets/YogaITS.ico`：编译进 EXE 的图标源，不单独复制到发布目录。
- `YogaITS.dGPUKeepAwake/`：独立 NVIDIA 空闲 context 助手，不承载保活策略。
- `YogaITS.dGPURestart/`：等待主程序退出、重启独显设备并恢复控制中心窗口的独立助手，不加载 WinUI。
- `YogaITS.Tray/Services/KeyboardBacklight/`：键盘背光协议、EnergyDrv 设备访问、Probe/Get/Set 后端及串行服务，无状态持久化或自动场景。
- `YogaITS.Updater/`：等待主程序退出、校验/安装及失败回滚的更新助手。
- `Tests/YogaITS.PolicyChecks/`：业务策略、更新包和发布程序集边界检查，不操作硬件。
- `Tests/YogaITS.UiPolicyChecks/`：直接链接生产 UI 策略、布局和物理状态的无硬件检查。
- `Tests/YogaITS.UiChecks/`、`Tests/UiContractChecks.ps1`：真实 WinUI 控件契约，仅编入隔离的检查构建；不进入正式发布物。
- `Tests/YogaITS.ReadOnlyProbe/`：能力与状态的只读诊断。
- `main.png`、`mini.png`：README 使用的用户界面截图。

按现有 C# 风格使用四空格缩进、文件作用域命名空间、可空引用类型；类型/公开成员用 PascalCase，参数/局部变量用 camelCase，私有字段用 `_camelCase`。硬件与协议逻辑放在服务中，UI 不直接访问驱动。优先复用共享控件及串行操作路径，不按机型硬编码高级功能。

## 构建与发布

Windows x64、PowerShell 7 与 .NET 10 SDK。普通版使用系统 Windows App Runtime；玻璃版沿用 UI Probe 的固定组合：应用本地 Windows App SDK **2.2.0**、Win2D **1.3.2**、LiquidGlassWinUI **1.0.3** 的 native effect 和本地 Material 源码。只私有部署 Windows App SDK，.NET 10 仍为 framework-dependent。玻璃版构建额外需要 VS 2026 C++ v145 工具链和 Windows SDK 10.0.26100.0（MSBuild、fxc、makeappx）；不升级全局 SDK、不修改系统开发者模式。

从仓库根目录运行：

```powershell
.\build-clean.ps1 -Checks -BuildOnly
.\build-clean.ps1 -LiquidGlass -Checks -BuildOnly
# 可替代 -LiquidGlass：--liquid-glass
.\dist\native\YogaITS.Tray.exe
.\dist\liquid-glass\YogaITS.Tray.exe
```

两版名称均为 Yoga控制中心，应用标识 YogaITS，EXE 均为 `YogaITS.Tray.exe`；仅包名以 `-lg` 区分材质构建。所有 helper 使用相同构建标志。`Directory.Build.props/targets` 将 restore/build 中间产物隔离至根目录 `obj/<flavor>/<project>/`、`bin/<flavor>/<project>/`，检查构建另加 `-ui-checks` 后缀，不能跨 flavor 或生产/检查构建复用 assets。普通版不编译玻璃/Win2D/捕获/物理引擎，也不携带捕获身份或本地 SDK。

脚本仅清理选定输出目录；默认分别是 `dist/native` 和 `dist/liquid-glass`，不会删除另一版。使用 `-OutputDirectory ./publish/<独立目录>` 可保留正在运行的构建；只允许仓库 `dist/`、`publish/` 下的专用目录，不覆盖当前运行的程序。不要直接对四个项目逐一裸 publish 作为玻璃版交付，否则会漏掉 C++ bridge、HDR shader、runtime lock 或 sparse identity。

完整构建发布主程序及更新、独显保活、独显重启三个助手，统一内嵌版本/完整 SHA，验证 PRI、依赖隔离和 .NET 运行模式。玻璃版先构建捕获桥接、HDR shader 与原生 DWM shader 检查，再生成全部 DLL 的 `runtime.lock.json`，准备 `CaptureIdentity` 清单/徽标和第三方许可。构建不注册身份、不启动硬件控制器、不捕获桌面；`-BuildOnly` 保留参考项目的命令形式。图标继续使用 YogaITS 原图并内置于 EXE；身份所需的 PNG 是系统包徽标。

`-Checks` 执行版本命名检查、UI/捕获/HDR/物理与自适应布局策略检查及应用策略检查；玻璃版还执行 WARP 合成像素检查和真实 WinUI dispatcher 下的无可见窗口控件契约检查。后者由 `Tests/UiContractChecks.ps1` 显式使用 `EnableUiChecks=true` 重编译同一套 UI 到临时检查宿主，独立还原、独立输出，只对该宿主改为 asInvoker 后执行 `--check-ui-contract`；不实例化 `MainWindow` 或真实服务，不影响交付文件。生产构建默认 `EnableUiChecks=false`，不包含测试实现；若误传检查参数则立即拒绝，不启动硬件控制器。普通版可在装有兼容系统运行时的本机手工运行 `./Tests/UiContractChecks.ps1 -PublishedDirectory ./dist/native`。

非打包 WinUI 必须保留 PRI，`AddProjectPriToResolvedFileToPublish` 补充 SDK 的发布遗漏。交付需完整解压选中 ZIP，禁止混合两个构建的运行文件。

## GitHub Actions 自动构建与 Release

`.github/workflows/build-release.yml` 仅由推送 `main` 触发。共用提交版本，matrix 分别构建 `native` 和 `liquid-glass`；使用 [windows-2025-vs2026 runner](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-VS2026-Readme.md) 提供玻璃版所需 v145。每版独立执行 `build-clean.ps1 -Checks -BuildOnly` 并上传完整 ZIP artifact（保留 30 天）；普通版名沿用历史约定 `YogaITS-<version>-win-x64.zip`，玻璃版为 `YogaITS-<version>-lg-win-x64.zip`，artifact 名去掉 `.zip`。仅两版均成功后，一个 release job 校验并上传两个附件；任一失败不发布新 Release。CI 不初始化真实硬件服务，不注册捕获身份、不录音或修改硬件设置。

根目录 `VERSION` 保存人工维护的 `x.y`（当前 `6.0`），不再从数字分支推算。发布名、tag、ZIP 统一为 `x.y-yyyyMMddHHmmss-hash`，时间取提交的 committer 时间并转为 UTC，hash 为 SHA 前 8 位；同一提交重跑名称不变。程序内嵌完整 SHA，相同源提交不提示更新。远端 x.y 按数字比较须不低于本地，且日期号须严格更新；不按 hash 排序，较旧或同日期的发布不提示。旧 tag 仍可解析；无日期的远端版本不提示，无日期的本地版本仅允许升级至更高 x.y 的带日期版本。`scripts/Get-ReleaseVersion.ps1` 及 `Tests/ReleaseVersionChecks.ps1` 验证命名规则。

版本验收后，从当前 main 创建并推送对应 `x.y` 分支封存，再在 main 修改 VERSION 开始下一版本；数字分支不自动移动，推送它们不触发 Actions。`4.0` 已封存至 `ea2bbf7`；`5.0` 已封存至 `4ece4e7`（包含双路更新竞速）。

`5.1` 封存分支固定在 `56b725e`，包含独显保活、离电暂停及集显隐藏分组；用户已完成上一轮保活测试。用户已于 2026-10-19 确认 5.2 优化后的所有功能在 Yoga Pro 16 IPH11 上重新验证通过；其他机型的既有验证不自动视为本轮验收通过。

2026-10-21：`5.2` 封存分支固定在 `319d7bb`，包含本轮稳定性优化、息屏暂停/亮屏恢复独显保活及完整验收记录。main 开始 `5.3` 开发；5.2 的验收结论不自动覆盖后续 5.3 改动。

5.3 首个新增功能为 EnergyDrv 键盘背光，按实时 capability 动态显示。用户已于 2026-10-21 确认在 Yoga Pro 16 IPH11 上完成集成版实机测试；自动检查、协议样本与实机验收分别记录，不将此结论扩大至其他机型。

2026-10-23：合入显卡工作模式更新前，将 main 的 `86f2fea` 原样封存为 `5.3` 分支并推送。新版 `VERSION` 为 `5.4`，包含经实机验证的 NVAPI 显卡控制、状态确认及独立设备重启助手；正式界面移除实验诊断入口。

2026-10-25：将 main 的 `d7c74050` 原样封存并推送为 `5.4` 分支，包含显卡工作模式更新、独立设备重启助手、保活开启时允许重启及独显控制单行三列布局。main 的 `VERSION` 更新为 `6.0`，开始下一阶段开发；既有功能与验证记录保留。

发布 job 仅在构建成功后使用 `GITHUB_TOKEN` 的 `contents: write` 权限，以完整提交 SHA 为 tag 目标自动创建 Release 并附带 ZIP，不需要额外 secret 或 GPG 私钥。Actions 固定到官方提交 SHA。重复运行已有版本只更新附件，并验证标签目标相同；不重写提交或移动已有标签，不影响已签名提交的 Verified。

发布前及上传后核对远端 main，只有仍对应 main 的发布才标记 latest，避免旧构建晚完成覆盖最新版。标准交付使用 `build-clean.ps1`；独立目录构建同样需要发布四个项目，保留主程序、PRI、三个助手及所有运行依赖。

## 代码结构与发布边界

正式 UI 不以演示项目作为依赖入口。两种构建共用 `UI/Controls` 与硬件服务；原生/玻璃差异集中在 `WindowMaterial`、`ChoiceBar` 的适配点。普通版通过一条 `Compile Remove="UI/Glass/**"` 排除整套可选实现，不再维护容易漏项的文件名通配符列表。第三方材质作为独立项目位于 `ThirdParty`，保留原始命名空间、许可和版本信息，不与应用业务代码混编。

仅做职责归位、名称与构建引用整理；不调整已验证的物理参数、HLSL、材质数学或硬件协议。演示控件 `FluidTabBar`/`FluidTabIndicator` 及仅服务旧演示的几何/手势夹具已删除；相关自测同步移除，现有生产物理、手势、可变行布局和控件契约继续运行。`PreviewKeyDown`、指针 preview/pending 和硬件 Probe/Get 是真实产品/API 语义，不做关键词式删除。

发布检查直接读取 PE 元数据，确认生产 DLL 不包含 `YogaITS.Tray.Tests`、`YogaITS.Tray.Demos` 或 UI 测试分发实现，普通版不含 `UI.Glass` 类型及 Win2D/材质引用。检查宿主的 `obj/bin` 与正式构建隔离，脚本不会修改正在交付或运行的程序，Actions 仍复用同一构建入口。

窗口首次显示前即确定尺寸：完整窗口在内容及材质初始化之前设置目标竖屏边界；迷你窗口按实际客户区宽度测量自然高度，并单独计入系统非客户区尺寸，不用固定高度补偿。`AnimatedOptionHost` 在 Measure/Arrange 中显式测量 Canvas 内的内容，保留原有展开相位与动效参数；Loaded 后仅合并一次迷你窗口尺寸校正。隐藏 WinUI 检查覆盖显示前新增内容、窄宽度折行、迷你窗口全部功能和退出按钮的布局边界，以及完整窗口首次显示前的竖屏尺寸。该检查不等同于屏幕上首次呈现的实机验收。

## 串行调度与稳定性

以下为当前实现的共同规则，最初的稳定性优化在 5.2 完成。用户已于 2026-10-19 确认 5.2 所有功能在 Yoga Pro 16 IPH11 上重新验证通过，包含息屏暂停及亮屏按条件恢复独显 helper；5.3/5.4 的后续验证见实机验收记录。

- **安全与生命周期**：结束占用进程保留 PID+启动时间，执行前再次保护 DWM、Explorer 和保活助手；身份不明或复用则跳过。等待进程结束上限 5 秒，pnputil 等待上限 30 秒。helper 停止失败不丢弃所有权，不启动第二份。主程序退出先取消录音/网络恢复，清理最多等待 10 秒；超时直接结束进程，不提前释放仍在使用的 native 资源。
- **刷新调度**：串行队列保留最多一次在途和一次待补刷新，写入优先于未开始的刷新，写入之间保持顺序。充电的读写共用驱动门锁。托盘悬停只使用固定提示，不再查询 ITS。状态缓存启动为空，先建立托盘/窗口再后台探测；ITS 能力失败最多三次探测。核心读探针每次最多等待 5 秒，超时保留在途调用、不重复创建工作线程；晚到结果丢弃，不覆盖之后的设置。
- **状态与回读**：音频、HPD、显卡和色彩运行态临时异常保留已确认能力，以不可用标志禁用操作；真正检测到端点/内屏消失仍隐藏，显卡能力探测本身失败按能力表隐藏。核心充电/开盖/自启保留最后确认值并标记可读性，不把读取失败冒充关闭；按功能独立的 mutation revision 防止旧读取覆盖新设置，其他分组仍正常合并。显卡偏好独立于实际模式，启动和电源/显示事件协调全部偏好；未知运行态暂停保活。HPD/音频设置在有限窗口内确认。普通服务读取等待上限 10 秒、写入 20 秒，录音写入 60 秒，色彩读取 90 秒/写入 210 秒以保留 TCON 等待及三分钟恢复；超时不释放在途资源、不并发重试，尚未开始的过期写入直接取消。成功刷新清理旧卡片错误。

- **色彩与更新**：普通刷新按面板与 ICC 文件路径/大小/修改时间缓存校验和亮度元数据；实际 ICC 切换、在线恢复后仍完整校验，TCON transport/能力顺序不变。校色元数据上限 2 MiB、ZIP 128 MiB、实际展开 256 MiB/2048 项，下载空闲 20 秒、总时限 3 分钟，退出可取消。更新包必须含主程序与三个助手的完整文件集。仅清理成功更新且超过 7 天的 GUID 暂存；失败/回滚目录和未知目录、符号链接均保留。

RPC 的 1 秒/2 秒 call-timeout 设置仅尽力请求，不能宣称约束本地 `ncalrpc`。微软说明该选项对 local RPC 可忽略：[RpcBindingSetOption](https://learn.microsoft.com/en-us/windows/win32/api/rpcdce/nf-rpcdce-rpcbindingsetoption)。实际调用方边界由串行队列实现，原 native 调用返回前继续占有资源；若最终始终不返回，退出的进程级边界负责释放。停止录音仍可独立取消，不受状态未知时的普通按钮禁用影响。

验收重点：首次启动托盘响应、服务延迟启动后能力恢复、管家外部切换显卡模式、插拔电与设备后不积压刷新、快速操作两窗口的充电设置、录音取消/换设备、HPD/降噪延迟回读、ICC/TCON/HDR/ACM 原路径、更新失败回滚及退出响应。自动检查使用独立临时文件夹与模拟后端，不访问真实驱动或网络；不能替代上述实机验证。

## 自动更新链路

`AppUpdateService` 启动后延迟 3 秒检查一次，此后每 24 小时检查；仅大窗口“杂项”分组出现单个更新卡片，位于分组标题下、开机自启/退出上方。内层不统一施加位移过渡，而由 `SettingsMotion.RevealBefore` 分别控制：更新卡片填充文字后以零偏移 EntranceThemeTransition 原地淡入，已有按钮行使用 RepositionThemeTransition 平滑移动，避免首次显现从窗口原点滑入或下方按钮瞬移；遵循系统动画开关。后台检查静默失败，不接触硬件或私有配置。点击后使用默认取消的 ContentDialog 确认；确认的版本固定，检查与下载串行，不重复安装。

`GitHubUpdateClient` 同时请求固定仓库 `wfsg00098/YogaITS` 的 `/releases/latest` 与 `https://gh-proxy.org/` 对应代理地址，采用最先解析校验成功的结果，随即取消另一条请求。一条线路失败不抢占另一条仍在进行的有效请求；两条均失败才报错。元数据请求上限均为 20 秒。下载也双路同时进行，各用独立随机 `.part` 文件，先完整下载且通过大小与 SHA-256 校验的包胜出；取消并等待另一条释放文件句柄后发布获胜包、清理临时文件。下载响应头/读取空闲上限均为 20 秒，总时限各 8 分钟，不再按低速阈值切换线路。保留 HttpClient 默认系统代理行为，不发送凭证、序列号或配置。只接收正式版、预期仓库/文件名的 x64 ZIP、合理大小及 GitHub SHA-256 digest。第三方代理同时承载元数据与包时仍存在代理信任边界，SHA-256 并不等价于发行者数字签名。确认对话框不展示下载线路说明。

更新暂存与旧文件备份位于 `%LOCALAPPDATA%\YogaITS\Updates\<GUID>\`，不写 Logs。拒绝 ZIP 路径穿越、重复路径、缺少必需文件及过大展开内容；必需清单包括主程序、PRI 和三个助手的 exe/dll/deps/runtimeconfig。更新助手从暂存目录运行，按 PID 和启动时间等待原进程退出，再从原 ZIP 重新验 hash/解压、备份被覆盖文件、复制及逐文件验 hash，最后重新启动原路径。复制、校验或启动失败回滚；回滚不完整时保留备份并明确提示。成功重启才写入 `completed` 标记，后续仅清理超过 7 天、标记内容正确且不含链接的 GUID 暂存。失败或未知目录保留。未运行常驻更新服务，不修改计划任务或硬件设置。

自动检查覆盖模拟 HTTP 双路竞速/取消/坏包、版本身份、双附件 flavor 选择、跨构建拒绝、玻璃 SDK 版本/哈希/必需文件、ZIP 路径安全及两版临时目录安装/配置保留/回滚。`--check-package <发布目录>` 还将真实产物打包后经 updater 的解压/校验入口检查，覆盖 SDK 文件名大小写差异。完整 UI 更新和真实程序重启仍需实机验收。

## 6.0 材质接入与边界

参考 `YogaITS.UIProbe-source.zip`，原样复用其玻璃材质、shader、背景采样、DPI/裁剪、物理动效及捕获生命周期；生产命名空间改为 `YogaITS.Tray`，未引入 ProbePreview、模拟状态、演示窗口或假延时。已移除 `Demos/` 与演示专用控件、夹具及未启用的预览校验分支；实际使用的 renderer/physics 已归入 `UI/Glass/Rendering`、`Animation`、`Layout`。公共控件样式从旧版演示控件拆出为 `GlassChoiceStyle`，布局和状态分别使用 `ChoiceTrackLayout`、`SelectionMotionState`。两版共用窗口和硬件服务，不复制业务后端。

`HardwareChoiceGroup` 将性能、充电、显卡偏好、色域、麦克风、睡眠速度和背光的实际选项 ID 绑定到 ChoiceBar/native cards。未知状态不伪选第一项，也不发送补偿 SET；pending 与 confirmed 分离，完成后只读服务 snapshot。选项改名/重排保留语义 ID；音频 endpoint、显示设备/backend 形成请求域，能力变化/关闭/过期异步完成不覆盖新 UI。复用未变化的控件以保留焦点。HDR/ACM、录音期间禁用和其他能力条件继续由真实 snapshot 决定。

玻璃 UI 选项保存到现有配置的 `GlassNativeUI`、`GlassBackgroundSource`、`GlassImagePath`，不保存硬件逻辑状态。无配置时默认 WinUI3；首次窗口 Loaded 后恢复保存的偏好，启动默认值和临时能力降级不覆盖用户配置。用户切换时先保存再发布状态，写入失败保留原选项。

加速度模型仅在液态玻璃材质实际启用且系统允许动画时运行；布局、输入和渲染回调都重新检查条件，切回 WinUI3 或材质降级立即丢弃速度并归位。原生卡片网格缩放直接跟随列宽，不施加位置过渡；分组显隐仍使用系统动画。玻璃窗口在全客户区最上层放置透明的 WinUI 缩放区：四边和四角各占内侧 4 DIP，中间区域不参与命中。光标由 `ProtectedCursor` / `InputSystemCursor` 管理；同一个 WinUI 指针捕获会话处理按下、移动、松开和取消，以屏幕物理坐标更新 AppWindow 尺寸，避免把事件交给另一套原生模态循环后丢失松开。按系统 DPI 的最小跟踪尺寸限制缩放；Escape 还原，失去捕获或切回原生模式结束操作。窗口缩放期间复用背景捕获的扩容策略，松开后收紧缓冲。最大化、迷你窗口及原生模式隐藏缩放区，不改变已验证的玻璃边角。策略检查覆盖四边/四角、最小尺寸、负坐标及松开后停止；WinUI 契约检查覆盖真实布局和材质切换，仍需可见窗口实测。[微软 WinUI 系统光标示例](https://github.com/microsoft/WindowsAppSDK-Samples/blob/main/Samples/Input/cs-winui/InputCursor.xaml.cs)。

`BackgroundImageStore` 异步将静态图像完整复制到 `%LOCALAPPDATA%\YogaITS\Backgrounds\<GUID>\<原文件名>`，临时文件完成后才公布路径，不修改原图或覆盖正在读取的旧图。重启复用已托管文件，旧配置的外部图片路径在首次开窗时迁移；失败保留配置供重试。图片选择的版本号与材质/来源切换分开，较早的导入完成不能覆盖新图片，也不会撤销之后的样式选择。静态图像/壁纸不请求桌面捕获；透明桌面沿用参考实现的系统授权和边框规则。普通版完全不读取这些材质选项。无硬件检查覆盖复制后删除原图、同名图片、重复恢复、取消和失败清理，均使用独立临时目录。

玻璃版 sparse identity 使用 `YogaITS` / `CN=YogaITS`，不复用 Probe 身份。启动前检查捕获身份与当前路径；需要修复时由独立 PowerShell 等当前进程退出后注册并重新打开，失败也会恢复启动且不循环重试。`Start-YogaITS.ps1 -RegisterOnly` 仅操作自身身份，不改变开发者模式；未启用 Windows Developer Mode 时注册可能失败，保留系统捕获提示或可用背景。不得通过注册表伪造授权，不生成 Logs，不覆盖系统 WinUI DLL。

两版共享旧版单实例与任务名称，入口在身份注册前优先转发开窗请求；启动 GPU 偏好仍只针对自身和专用保活助手。TCON 的进程级 COM 安全初始化仍先于 WinUI/本地 SDK 的 COM 使用。三个助手和更新 plan 同步 flavor；旧版普通 ZIP 名称不变，保留 5.x 到 6.0 的更新兼容性。

自动更新只选择运行构建对应的附件；同一 tag 缺失对应附件时静默检查失败，不自动换版。安装前及 updater 内验证 flavor marker；玻璃版还要求身份文件、本地运行时、SDK 2.2.0 清单与 DLL 哈希。原有直连/gh-proxy 竞速、系统代理默认行为、日期比较、暂存、回滚和计划任务路径规则不变。

既有 5.x 实机结论继续保留。用户确认 Probe 玻璃材质已完成测试；6.0 新 UI 与真实服务的组合尚待实机回归，构建、策略、WinUI 契约与 shader 检查不能写作整机验收。

玻璃版稀疏身份的任务栏图标使用 `CaptureIdentity/Square44x44Logo.targetsize-*` 的默认、`unplated` 和 `lightunplated` 三种变体，覆盖 16–256 像素的系统目标尺寸，均由原始 ICO 导出并保留 alpha；原生窗口和托盘继续使用内置 HICON。默认 `resources.pri` 合并原有应用/依赖 PRI 和 shell 图标，避免仅含图标的索引遮住 WinUI 主题资源；相同副本部署到包根 `CaptureIdentity/` 和实际外部内容根目录，满足 Shell 从有效外部位置加载索引的路径。图标资源 URI 保留 `Files/CaptureIdentity/` 前缀，不覆盖应用的 `YogaITS.Tray.pri`。更新包要求完整携带这些变体和索引。修改身份资源时递增 manifest 的身份版本；启动同时校验目录和该版本，原目录覆盖更新也会交接给独立注册进程刷新身份。`Tests/IdentityIconChecks.ps1` 验证实际 PNG 的尺寸/透明度、亮暗变体一致性、索引 URI/qualifier 及资源版本，不注册身份或启动可见窗口。隔离 WinUI 检查宿主同时复制已发布的默认 PRI 和身份资源，按真实发布布局检查 XAML/主题加载。

## 架构与刷新

四组 probe 后端整合为独立服务，不依赖厂商 RPC 客户端 DLL。`SerializedFeatureService` 串行执行同一服务的读、写和关闭；首次检测及不同服务之间可并行。写入由用户操作或已保存显卡偏好的受约束协调触发，不通过试写检测能力。保活 helper 只在用户启用且当前模式允许时驻留；其低频检查独立于原有硬件状态刷新。

`MainWindow` 是隐藏的常驻消息宿主。`TrayMenuWindow` 共用完整/迷你窗口的核心功能；`FeatureCard`、`FeaturePanels` 提供新增功能。开窗读取缓存，获得焦点时刷新，避免重复叠加刷新批次。后台刷新不抢焦点。

普通启动打开完整窗口；独立 `--startup` 参数只驻留托盘。单实例通过当前用户会话的命名事件传递普通启动的开窗请求；再次 `--startup` 不影响当前窗口。旧版与集成版共用单实例标识和启动任务名称。

开机自启查询 `Yoga ITS Tray` 任务 XML，任务必须启用且 Exec 路径与当前 EXE 一致；忽略大小写，规范化引号及绝对路径，任务不存在或旧安装路径显示关闭。查询失败为未知：保留已确认状态并禁用操作，不伪装为关闭。开启时串行执行删除旧任务 → 创建当前路径任务（保留 `--startup`、最高权限、交互登录）→ 回读。删除失败只在 HRESULT 确认任务不存在时继续，创建不用 `/F` 覆盖竞态出现的任务。关闭也删除路径不匹配的旧任务；任何结果按实际回读更新 UI/缓存，无法回读时返回 nullable unknown，避免重建失败或查询失败后错误更新开关。同步查询上限 10 秒，单次异步 schtasks 上限 30 秒；UI 操作由共享串行服务提供 20 秒等待边界，不提前释放后台任务。逻辑测试不改真实任务。

创建任务使用显式 XML：允许电池供电启动、切到电池不停止，无空闲/网络前提，空闲结束不停止；任务和登录触发器的 `ExecutionTimeLimit` 均为 `PT0S`（无限制）。只在当前用户交互登录时启动并保持最高权限；不依赖 schtasks 默认的交流电条件或 72 小时上限。临时 XML 创建完成后才删除旧任务，注册后清理临时文件。

常驻 Win32 窗口监听 AC/DC、电源状态、睡眠恢复、显示拓扑和设备节点变化。显卡与色彩状态独立于窗口是否打开、是否获焦刷新；显示/色彩事件采用 750 ms 防抖。音频设备变化只刷新能力与状态，不改写服务的偏好模式。

显示电源另注册 `GUID_SESSION_DISPLAY_STATUS`，适用于交互式用户会话（[Windows 通知定义](https://learn.microsoft.com/windows/win32/power/power-setting-guids)）。校验 DWORD 数据长度后，0 表示息屏、1/2 表示亮屏/变暗，未知值不覆盖最后状态。该事件不防抖、不触发 WMI/色彩查询，直接进入主程序保活串行队列：息屏停止 helper，亮屏按保存偏好、AC、实际显卡模式及睡眠状态恢复。息屏标志独立于睡眠，电源/拓扑事件及十分钟监督不能越过此条件；helper 无须改动。自动检查覆盖状态解码和全部保活条件组合；用户已确认本轮 dev 版息屏/亮屏测试成功。

## 能力检测与硬件约束

| 功能 | 检测与行为约束 |
| --- | --- |
| ITS 性能选项 | 保留型号筛选；Dispatcher Version ≥8192 走现代服务，查询 2 成功且含 `0x1` 才显示。较低/缺失版本走 LITSSVC，要求旧 MMC 状态键和 EnergyDrv 存在，不套用现代查询 2 门槛。 |
| 极客/创作模式 | 查询 10 满足 `0x00020001`，且 `ITS_CurrentSettingV` 是可读 DWORD/int；不回退旧字段，不要求当前值为 4。电池/未知供电时保持可见但禁用，写入前再次检查 AC。 |
| 显卡工作模式 | `IsSupportIGPUMode.Data > 0` 且类型正常才显示；能力探测失败也隐藏整卡。偏好独立保存，实际状态另行显示。NVAPI `Disconnected` 才允许执行集显 SET；`Connected` 禁用集显偏好并将已有集显改为智能，`Unknown` 保留偏好但不执行关闭，不再读取 HDMI WMI guard。 |
| 显卡偏好协调 | 手动集显/双显目标不随供电改变；Auto 要求电源通知可用，AC 目标双显、DC 目标集显、供电未知不切换。启动和电源/显示事件协调全部偏好；明确的集显切换失败最多追加 5 次重试、间隔 5 秒。手动点击立即 SET 最多一次，新事件或选择取消旧调度。 |
| 色域 | 内屏、动态 WCS DeviceID 及原有 ICC 控制通道可读取。Factory 缺失可在线恢复；按所有实际 ICC/ICM 的结构完整性生成模式，不要求数量、Native 或固定组合。序列号、机型及面板 HWID 等描述性元数据不作为筛选条件；恢复和切换都检查校色数据完整性。 |
| HDR/ACM | HDR 卡片及色域保护使用实际 `activeColorMode`，ACM 卡片使用独立 `wideColorUserEnabled` 设置。HDR 仅在 `HighDynamicRangeSupported` 为真时显示；ACM 按自身能力/启用偏好显示。未知颜色状态保守阻止色域写入。 |
| 唤醒/睡眠 | `GetAll rc=0` 且 `unAwareAbility` 非零。人员辅助判定还要求 `sensor_type != 4` 和 `capability & 0x40`。 |
| 播放/暂停 | `GetAll rc=0`，且 `sensor_type` 为 3/4 或 `unAwareAbility` 非零。 |
| 音频降噪 | 当前 Capture/Render endpoint 声明 `uplink/downlink`，并且对应 `GetMode` 返回 OK。 |
| 多人/定位/声纹 | 仅内置阵列麦克风；多人模式按端点类型限制，人声定位还要求 `special`，声纹要求 `speaker_extraction` 与声纹查询正常。 |
| 键盘背光 | EnergyDrv `0x83102144` 查询 `0x01` 返回 bit0=1，`raw >> 1` 为 1/2/3，并且对应 GET 合法才显示；无型号/产品线/年份筛选。unsupported、异常或超时期间隐藏整个分组。 |

ITS 校验参考 [LLT ITSModeFeature](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/ITSModeFeature.cs) 及 [ITSModeFeatureDriver](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/ITSModeFeatureDriver.cs)。只按 `LenovoProcessManagement\Performance\PowerSlider\Version` 的 8192 界限选择路径，现代命令失败不回退旧服务。EnergyDrv 查询返回长度必须是 4 字节；未接入 LLT 的 ThinkBook 实验性整套驱动切换。

旧版通过 `LITSSVC`：智能发 135，安静依次 134/146，性能依次 134/148。回读 `LITSSVC\LNBITS\IC\MMC` 的 `(AutomaticModeSetting, CurrentSetting)`，仅 `(2,0)`、`(1,1)`、`(1,3)` 分别识别为智能、安静、性能，其他组合未知。写入串行化，最长约 3 秒、每 200 ms 回读；不直接写注册表，也不强制更改 Windows 电源模式。现代 163/164/165/172 命令和既有能力检测保留。

旧版第四档按 LLT 查询 10 的 `0x00020001` 位检测，不要求现代 `ITS_CurrentSettingV`。开启时先设性能再发 `0x001F100B`，其他模式前尽力发 `0x000F100B` 清除旧极客覆盖；仍要求插电才能开启第四档。旧服务本身只回读三档，第四档标记与 LLT 一样仅在进程内跟踪，重启后或管家外部切换无法可靠区分，不宣称硬件独立回读第四档已验证。

`dotnet run --project Tests/YogaITS.ReadOnlyProbe -- --its-only` 仅输出机型、后端、版本及模式/旧字段，便于在 IAH7 上比较管家切换前后；不发送任何设置命令。纯逻辑测试覆盖路径边界、指令顺序、双字段组合和第四档能力差异。2026-10-18：用户确认小新 Pro 16 IAH7 的旧路径性能选项切换成功；完整机型验收范围以文末表格为准，不推广到所有旧款机型。

显卡 WMI 直接对枚举实例调用方法，不依赖 ReturnValue，设置后等待并回读。NVIDIA 存在性随后台刷新更新；独显控制分组还检查实际集显模式，即使设备节点暂未消失或保存了保活开启配置也隐藏。显卡工作模式分组仍按 WMI 能力显示，允许切回双显。独显操作返回 Task，执行期间显示强调色并防止重复点击，结束后清除。

结束独显占用进程保留 LLT 的批量路径：NVAPI V2 活动进程列表排除 `dwm.exe`、`explorer.exe`，并保护本程序、保活助手及无效 PID；没有 NVIDIA 容器、SYSTEM 或 Session 0 排除。枚举时保存进程启动时间，执行前再次检查当前进程名和 PID+启动时间，身份未知或复用则跳过。逐个 `Kill(entireProcessTree: true)` 后最多等待 5 秒，单项异常不中断后续目标；不切换进程级 debug privilege。设备重启的 pnputil 子进程等待上限 30 秒，超时提示结果待确认、不重复发送命令。NVAPI 查询失败按异常处理，不伪装为空列表。大小窗口保留 500 ms 执行前强调色提示；不提供直接强制结束桌面进程入口。

参考固定版本 [LLT GPUController](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/d0c57bfc7ecc26d4ccf15bb3835fed7ac28a259f/LenovoLegionToolkit.Lib/Controllers/GPUController.cs) 与 [PhyscialGPUExtensions](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/d0c57bfc7ecc26d4ccf15bb3835fed7ac28a259f/LenovoLegionToolkit.Lib/Extensions/PhyscialGPUExtensions.cs)。NVAPIWrapper 的 V2 结构大小、容量 128 和查询失败语义也已核对。逻辑检查覆盖桌面进程大小写排除及容器保留，不实际结束进程；SYSTEM 容器与进程树处理需实机验收。NVAPI 返回数量不等同于 NVIDIA 托盘的可见应用数量，不能据此断言应全部结束。

可选 `dotnet run --project Tests/YogaITS.PolicyChecks -c Release -- --gpu-process-tree` 验证结束进程树，仅创建并结束该测试自己的父/子进程，不访问 NVAPI 或当前系统占用进程；默认 CI 不运行此选项。

入口在启动 WinUI 前为当前 EXE 路径注册 `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` 的 `GpuPreference=1;` 节能默认值。已有显卡偏好（包括让 Windows 决定）不覆盖，其他图形选项保留，不改全局或其他程序；注册失败不阻止启动。该注册表约定不是公开的进程级强制 GPU API，首次注册可能需下次启动生效，不自动重启；实际适配器由 Windows/驱动决定，单显卡自然回退。纯逻辑测试不读写注册表，双显卡分配需实机验证。

夜间优化与三个充电模式互斥：选择夜间优化先开启夜间标志，再设常规模式；选择其他模式先关夜间标志，再写目标。共享串行入口，失败停止后续写入，最后一起回读并一次更新缓存。

色域模式来自 STANDARD，不自动恢复旧 ICC。保留 managed ICC 注册、字节校验、association、STANDARD/WCS 回读、可选亮度及 DWM 刷新；Factory 源不修改。按钮文案映射如下：

ACM 卡片仅按 `WideColorSupported || WideColorUserEnabled` 显示和允许操作，不因实际 HDR 或 `LimitedByPolicy` 禁用。HDR/ACM 请求均只修改对应偏好，随后回读实际状态；不主动关闭另一个功能、不修改 Windows 电源策略。能力不存在仍不允许开启；系统 API 拒绝请求或偏好回读不匹配仍报告真实错误。

ACM 卡片选中状态读取 `wideColorUserEnabled`，不能用 `activeColorMode == WCG` 代替：HDR 期间有效模式为 HDR，无法同时用枚举表示独立 ACM 选项。HDR 卡片仍使用实际 HdrActive；色域阻断/显隐仍使用实际 SDR/WCG/HDR，不能被保存的 ACM 偏好改变。

| 配置标识 | 用户名称（颜色配置） |
| --- | --- |
| Default | 默认 (D65) |
| Native | 鲜艳 (Native) |
| sRGB | 网页办公 (sRGB-D65) |
| DisplayP3 | 摄影/数码影院 (P3-D65) |
| DCIP3 | 原生数码影院 (P3-DCI) |
| AdobeRGB | 摄影印刷 (Adobe RGB) |
| REC709 | HDTV视频 (REC709) |
| DICOM_Dim | 暗室医学影像模拟 (DICOM Dim SIM) |
| DICOM_Office | 办公室医学影像模拟 (DICOM Office SIM) |
| 其他后缀 | 原始后缀，保留大小写及下划线 |

可附加有效的 ICC 建议亮度；元数据缺失/无效时省略数值并保留当前亮度，不阻止 ICC 激活。HDR/ACM 写入只反转启用偏好，随后刷新实际模式；HDR 不覆盖 Windows 的电池策略，也不要求实际模式立即等于偏好。实际 SDR 可切色域，即使电池导致 HDR 暂时关闭但偏好仍为真。实际 HDR/ACM 时隐藏色域、亮度联动和恢复操作，只显示不适用提示；恢复 SDR 后重新显示。

HPD 保留 ContextCreate/ContextClose 生命周期，检查设置返回码并回读；不暴露距离或 YogaMode。自动睡眠关闭时隐藏速度与人员辅助判定。自动唤醒/睡眠、播放/暂停仅开启时显示对应说明。

## Factory Calibration 动态发现（6.0）

`ProfileService.EnumerateProfiles` 同时扫描 `.icc`/`.icm`，文件扩展名及已知名称映射不区分大小写，保留模式原始拼写。解析 `TPLCD_<面板>_<完整后缀>`、`<面板>_<完整后缀>`；自己的安装副本为 `YogaITS_<完整后缀>.icm`。下划线不再删除，DICOM_Dim 与其他未知复合名称可完整回读；既有七种模式的安装名不变。已有七种模式相对顺序不变，随后是 DICOM Dim/Office，再是其他模式。未知名称不作为错误或 Default/Native 的别名。同模式大小写重复项稳定去重；文件内容不修改。

`IccCalibrationData` 检查 ICC 头、大小、tag table、校色标签范围和已知矩阵/TRC/LUT/MHC2 数据边界；无全包 Native、数量或 MHC2 要求。参照 [ICC 官方格式说明](https://github.com/InternationalColorConsortium/iccDEV/blob/master/iccdev-mcp/docs/icc-format-guide.md) 和 [微软 MHC2 结构](https://learn.microsoft.com/windows/win32/wcs/display-calibration-mhc)，MHC2 的零矩阵/零 LUT 偏移按规范允许 identity transform，不要求所有模式都有 MHC2。CxF、meta 等描述性信息缺失、损坏或身份不匹配不剔除完整的校色数据。本地发现与应用不依赖 SMBIOS 身份可读；只有在线下载才读取请求所需的机型/序列号。原有 `ValidateIdentity` 保留为诊断工具，已退出发现和应用的资格判定。Windows 安装/关联/STANDARD/WCS/字节比对/DWM 刷新链路保留，最终可用性仍由系统调用及回读确认；不修补或重写 ICC 内容。

在线恢复取得用户确认后，在同一个三分钟总等待边界内执行初次下载及最多三次重新下载。只有四次候选文件指纹一致且仍含坏文件、同时至少有一个好模式时，才跳过坏文件；指纹变化、取消、超时或全部损坏都不接受部分包。下载和每次解压相互隔离，缓存复制时排除坏文件，再完整验证新副本后替换 Factory；最后 ZIP 保留下载原件。普通刷新不自行上传序列号或发起恢复。`CalibrationDownloadPolicy` 通过模拟下载验证次数、修复后提前结束、稳定部分包、变化结果和取消。

色域专用的 `HardwareChoiceGroup` 测量当前字体下的完整标签，`ChoiceLayout` 贪心分行后分配剩余宽度；普通卡片与玻璃选择条共用行计划，最终行也铺满。`ChoiceTrackLayout` 支持可变行长，命中、上下键和胶囊目标均跟随实际行；不设置 16 项模式上限。单个名称长到必须多行，或不同原始模式具有相同显示名时，保留原生卡片呈现及完整文字，不通过省略或隐藏模式处理。玻璃材质和物理积分算法不变。

## TCON 与 ICC 自动选择

2026-10-18：用户确认小新 Pro 16 IAH10 的在线恢复校色文件及 ICC 色域切换实机通过。当时 LEN8BAD 包的成对 `INVALID` 身份占位按面板匹配兼容。此为历史版本记录；6.0 已按用户明确要求改为校色数据完整即可，机型、序列号及面板 HWID 不匹配也不剔除。

`ColorGamutService` 统一选择 backend；不按 ThinkBook/Yoga 名称或代际硬编码。内屏存在后先执行 TCON native WMI preflight，只有 `LENOVO_TCON_DATA`、`SetTconSwitch`、实例、live `F3 00` 总能力/模式位和 `F0 1D` GET 均通过才选择 TCON。类不存在立即返回，不打开 SPB。已验证的 ICC 文件仍可作为非 TCON 路径；TCON 检测不确定且没有有效 ICC 时，不提供盲目的 ICC 在线恢复。

TCON 模式位为 `0x0100=P3`、`0x0200=sRGB`、`0x0400=Adobe RGB`，必须同时有总能力 `0x0008`。raw 为 `0x04/0x06/0x08`，其他值保持未知。UI 仅按能力显示对应按钮；TCON 不提供 ICC 亮度联动或恢复，HDR/ACM 状态判断与原 ICC 路径共用。TCON 模式 ID 与 ICC ID 分离，旧 UI 回调不能跨 backend 写入。

应用入口先执行最终探针的 `ProcessComSecurity.InitializeEarly()`，以 DEFAULT/IMPERSONATE/EOAC_NONE 配置进程级 COM 安全，再启动 SDK 生成的 WinUI 主循环，退出时平衡主线程 COM。只读诊断入口同样在任何 WMI 之前初始化。不能仅在 TCON worker 中补调：此时 RPC_E_TOO_LATE 只说明进程安全已经固定，不能证明原设置适合实例枚举。

G7+ 的历史集成反馈显示类和方法存在，但实例枚举 Next（含 enum blanket 重试）返回 `WBEM_E_ACCESS_DENIED`，尚未访问 SPB；排查发现遗漏了探针入口的早期初始化。补齐后，主程序及只读诊断确认进程级 `CoInitializeSecurity=S_OK`，Yoga ICC 检测仍正常。后续用户已将 ThinkBook 14 G7+ IAH 列入完善测试机型，重点验证 TCON P3/sRGB 路径；5.2 的新增状态保持和调度仍需回归。

每个事务保留 `Global\\TconDriverConnectorParallelMutex`、独占 SPB handle、`SetTconSwitch` gate、`0x04001C00` 初始化与 `0x04001C10` 读/WriteFile 顺序。WMI、COM、mutex 在同一 worker 线程创建/使用/释放。按用户要求仅将探针的无限枚举/mutex 等待替换为 30 秒后台等待；其他 native WMI/transport/protocol 代码保持探针原样。初始化结果、gate OFF、bytesReturned/bytesWritten 不额外阻断，仍由原探针的 live capability、GET、WriteFile 成功和最终回读判定。设置前再次验证目标 mode bit 与当前 GET；写入后最多回读 6 次、间隔 30 ms，匹配才成功。

用户确认的集成版完善测试机型包含 ThinkBook 14 G6+ AHP、G6+ IMH、G7+ IAH，重点验证 TCON P3/sRGB；Yoga Pro 16 IPH11 为 TCON 负样本。这些是既有版本的验证记录，不替代 5.2 回归。Adobe RGB 的验证限制只写在 README，应用不显示测试备注。

## 显卡工作模式（5.4）

`Services/GpuMode/` 分离 Lenovo WMI 后端、NVAPI 显示连接查询与单次切换事务；`LenovoGpuWorkingModeService` 保存偏好、处理事件和有限回读。该路径已在 Yoga Pro 16 IPH11 与小新 Pro 14 IRH8 验证，按底层能力显示，不依赖电脑管家的功能入口。

### 能力与显示连接

- 只以 `LENOVO_GAMEZONE_DATA.IsSupportIGPUMode.Data` 为能力入口：调用成功、类型为 Int32/UInt32 且大于零才显示。未知类型、类/实例/方法或字段读取失败均隐藏；区分探测失败与明确不支持。能力成功后的运行态读取失败保留分组并禁用操作。
- 使用 [NVIDIA nvapi.h](https://github.com/NVIDIA/nvapi/blob/main/nvapi.h) 和 [接口 ID](https://github.com/NVIDIA/nvapi/blob/main/nvapi_interface.h) 定义的 physical GPU 枚举与 `NvAPI_GPU_GetConnectedDisplayIds`（`0x0078DBA2`）。所有适配器均参与查询，不按 HDMI 端口或机型筛选，不再使用旧 HDMI WMI guard。
- Initialize 成功后才解析业务入口；数量查询后按需分配，使用 UNCACHED。`NV_GPU_DISPLAYIDS_VER2` 为 16 字节、实际修订号 3（`0x30010`），连接位为 bit6。枚举最多 64 个 GPU、每个最多 4096 条显示记录，检查返回数量及热插拔引发的增长。
- 查询结果只有 Connected / Disconnected / Unknown。缺 DLL/入口、初始化或枚举失败、异常数量及查询异常均为 Unknown；未知不能当作未连接。NVAPI 初始化/卸载与独显占用查询共用进程内锁，PnP/WMI 设备存在性独立读取。

### 偏好与实际状态

| 状态 | 含义与用途 |
| --- | --- |
| `PreferredMode` | 用户偏好，沿用 `settings.json` 的 `GpuWorkingMode`，Default=0、IGPUOnly=1、Auto=2；卡片选中项使用此值。 |
| `DesiredMode` | 手动双显/集显目标固定为 0/1；智能模式为 AC→0、DC→1，未知供电没有目标。 |
| `RawWmiMode` / `EffectiveMode` | 分别保留 WMI 原始值与设置确认。只有 WMI=1、设备消失且无 Connected 冲突才确认集显；单独 WMI=1 不代表完成。 |
| `ActualMode` | 本次读取有效且 NVAPI=Connected，或 NVAPI 成功枚举为 Disconnected 且设备存在时，显示实际双显；其余沿用确认结果。读取不可用时为未知。 |
| `DisplayedActualMode` / `StatusMessage` | 界面显示缓冲，有限补查期间保持上一条已知文字；不参与 guard、偏好达成或保活判断。 |

显式选择先保存偏好，受阻或执行失败也保留；普通读取不跟随固件状态改写偏好。没有配置时仅在首次有效 WMI 读取初始化一次。启动、电源及显示事件协调所有偏好；Auto 另外要求电源通知可用。实际双显不替代 SET0 所需的 WMI=0 确认。

唯一自动修改偏好的例外：实时确认 Connected 时，集显选项禁用；原偏好若为 IGPUOnly，改为 Auto 并保存一次，其他偏好保持。启动、刷新、事件、补查及 SET 前的最新 guard 均处理该例外。Unknown 不触发转换；拔屏后重新开放集显选项，保留已改成的 Auto。

### 切换、回读与生命周期

- 发送集显 SET 前重新查询 NVAPI，必须为 Disconnected；暂停并确认释放保活助手，随后 SET1 一次，等待 1.5 秒，再 GET 并验证设备消失。已经确认处于目标状态时不重复 SET。保活停止失败或 guard 受阻均不发送关闭请求。
- 返回双显不受显示连接 guard 阻止。回读确认后按当前供电、睡眠、屏幕及保存偏好恢复保活；助手需要健康应答。辅助恢复失败为 SuccessWithWarning，不覆盖核心切换结果。`KeepDGpuAwake` 始终保留。
- 手动集显/双显点击立即 SET 最多一次。后台明确的 SET 失败或设备未移除，最多追加五次、每次间隔 5 秒；每次重新核对目标和显示约束。Connected/Unknown、目标改变、取消、能力丢失及 Indeterminate 终止重试，不自动添加 0→1 往返切换。
- 每轮事件或未知状态最多补查五次、间隔 750 ms，调度窗口 6 秒。补查不借未知回读重发 SET；若原先因约束受阻且约束随后解除，可延后执行一次经同一 guard 的偏好协调。后续明确失败仍受独立重试上限约束。
- 新事件、手动选择及退出取消旧调度。取消已发出的 SET 不跳过回读，也不盲目恢复保活。快照与协调携带 revision，旧结果不能恢复刚暂停的助手。串行服务读取等待 10 秒、操作等待 20 秒；native 调用不能强制中断，超时后仍独占资源直至返回。

已知状态立即更新原有文字。短暂未知时保留上一条已知模式及输出说明；首次尚无结果时暂不显示，有限回读结束仍不能确认才显示未知。最终显示更新与读取在同一队列提交，取消的旧任务不能覆盖新任务。只有状态文字缓冲，操作可用性和硬件策略始终读取真实快照。

界面仅保留模式卡片及简洁实际状态；Connected 时追加“外接显示器正在通过独显输出。”这条说明。已移除详细信息/诊断按钮、弹窗、剪贴板复制、最近八条记录、在途事务文本回调和累计诊断字符串。结构化 `GpuModeOperationResult` 保留结果、阶段、后端、原始错误与 SET 是否完成，供安全协调及有限重试使用；开发追踪仅写 Trace，不建立日志目录。

## 独显设备重启助手（5.4）

设备重置期间退出旧 WinUI 进程，完成后从相同目录重启控制中心，重建界面与 Mica。重启功能由 `YogaITS.dGPURestart.exe` 执行，Tray 不直接重启显卡设备；两种窗口均先显示默认取消的确认对话框。

- 大小窗口的重启按钮和执行入口均不以 `KeepDGpuAwake` 开启偏好作为禁用条件；保活设置正在执行时仅保留短暂操作互斥。确认后与更新、退出和重复设备操作互斥。助手启动时传入 PID、UTC 启动时间、私有就绪事件和大/小窗口恢复标志；验证同目录父进程身份、名称、路径与管理员权限，并持有父进程句柄避免 PID 复用。
- 助手先持有会话级重启互斥，再通知就绪。Tray 最多等待就绪 10 秒；助手未就绪则保留主程序，仅停止自己创建的助手。正常退出路径先取消后台操作、停止录制并释放保活，最多清理 10 秒；不清除保活配置，新 Tray 初始化后按保存偏好、供电、屏幕及显卡模式恢复助手。
- 助手等待父进程退出最多 90 秒，超时不重置、不终止父进程；退出后仅调用一次 `pnputil /restart-device`，最多等 30 秒，超时不重发。助手使用 asInvoker 继承权限，不加载 WinUI 或持有独显 context。
- 设备命令完成或失败后均尝试从同目录恢复控制中心；失败通过原生错误窗口报告。新的 Tray 在创建图形资源前最多等待重启互斥 45 秒，避免手动启动过早进入重置过程；窗口恢复参数不改变普通启动和 `--startup` 语义。

发布及更新包必需清单包含主程序、PRI 和更新/保活/重启三个助手的完整文件集。模拟检查覆盖身份校验、父进程等待、超时、重置失败和窗口恢复；发布物仅测试助手对非法参数的拒绝，不执行真实设备重启。早期设备重启测试曾出现 NVIDIA 错误 43，用户重启系统后恢复，具体触发原因尚未定位；进程交接检查本身不证明驱动恢复能力。

## 保持 NVIDIA 独显唤醒

实现核对了 2026-10-19 查询到的 LLT 最新 dev [`9baf5a01`](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/9baf5a01a95155a36aab7b48e7296316499cee52/LenovoLegionToolkit.Lib/System/DgpuAwakeManager.cs)（2026-10-17），而非首次引入版本；核对时 master 为 `d0c57bfc`。重点参考 [`0bb32908`](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/commit/0bb32908) 修正的 Vendor ID 选择和 GetDeviceRemovedReason 重建，以及该版本的 HybridMode/电源策略。该版本 GPUController 对 RTX 50 的 GC6/GcOff 与 GpuNotPowered 状态处理用于核对边界，本功能不移植其遥测轮询，不根据一次 NVAPI 休眠回读反复销毁/创建 context，也不引入 P-State 锁定或超频。

`YogaITS.dGPUKeepAwake` 是单独的 WinExe：枚举 DXGI 的硬件 NVIDIA（VendorId=0x10DE、排除软件适配器），以显式 adapter + D3D_DRIVER_TYPE_UNKNOWN + BGRA_SUPPORT 创建 D3D11 device/context；不创建窗口或 swap chain、不提交渲染，不在 Tray 进程加载这些图形调用。助手仅创建一次 context、等待停止/查询命令并释放资源，不监听电源、不定时检查设备、不自行重建。主程序每 10 分钟请求 GetDeviceRemovedReason 健康回读；失效、助手退出或 3 秒无响应时停止旧助手并重新启动，由新助手重新枚举 NVIDIA adapter。PnP/显示变化、恢复和供电事件另行即时触发主程序协调；睡眠由主程序停止助手、恢复后按条件启动。P8/Idle 是驱动决定的空闲状态，不保证固定 P8。

父进程传入 PID、启动时间及私有停止/健康查询事件。helper 创建图形资源前检查 parent 名称/PID/启动时间，保留进程句柄避免 PID 复用；其唯一自主周期任务是每 10 分钟检查父进程是否已退出。正常关闭发停止事件、等待退出，超时仅结束自己创建的 helper；异常父进程退出时 helper 自动终止，OS 释放 context。主程序通过 stdout 的 ACTIVE/INVALID 状态更新 UI，健康回复先完成等待再异步更新 UI，避免串行门锁互相等待；不查询独显功耗，不因打开窗口或托盘悬停而轮询设备健康。

偏好保存为 settings.json 的 `KeepDGpuAwake`，默认 false。与 LLT 仅在 Auto 离电暂停的条件不同，本项目所有模式都必须接 AC 才保活；离电或电源状态未知时一律停止助手但不清除偏好。主程序独立订阅电源事件并直接读取供电状态，不等待显卡模式 WMI 回读；十分钟监督也再次检查供电，作为事件丢失时的兜底。NVIDIA 不可用、集显模式、状态未知、睡眠或息屏时暂停；条件允许后恢复。切集显前先停 helper 并等待释放，模式操作结束后按实际模式恢复；停止失败保留进程所有权，不启动第二份。集显模式下独显控制分组隐藏，开启配置保留；只有完整窗口提供保活卡片；两窗口均允许在保活开启时重启独显，重启交接中的正常退出流程释放保活助手，新进程按条件恢复。Main 的核显偏好保留用户选择；helper 的 GPU=2 偏好单独设置且保留其他图形选项。完整发布另包含 helper 的 exe/dll/deps/runtimeconfig，不要仅复制主 EXE。5.1 保活已获用户测试完成确认，5.2 调度优化及息屏/亮屏行为亦已在 Yoga Pro 16 IPH11 上重新验证通过；不据此宣称所有混合显卡、RTX 50 或重置/睡眠组合均已验证。

## 键盘背光（5.3）

参考 [SaltwaterC 的 EnergyDrv 配置](https://github.com/SaltwaterC/KeyboardBacklightForLenovo/blob/main/KeyboardBacklightForLenovo.Core/DriversConfig.json)、[设备访问实现](https://github.com/SaltwaterC/KeyboardBacklightForLenovo/blob/main/KeyboardBacklightForLenovo.Core/KeyboardBacklightController.cs) 和 [gonwan 逆向实现](https://github.com/gonwan/ideapad-keyboard-backlight/blob/main/ideapad_keyboard_backlight.c)。仅重用协议知识，独立实现 capability-driven 后端；不移植其持久化、定时恢复、机型判断或 ThinkPad IBMPmDrv。

每次事务以 `GENERIC_READ | GENERIC_WRITE`、`FILE_SHARE_READ | FILE_SHARE_WRITE`、`OPEN_EXISTING` 打开 `\\.\EnergyDrv`，所有命令使用 IOCTL `0x83102144`，DWORD 输入、非空 16 字节输出缓冲，读取至少要求返回 4 字节。句柄在事务结束时释放，不与充电逻辑混用其不同的访问权限。

| capability 的 levels | 可用状态编码 | GET | SET |
| --- | --- | --- | --- |
| 1 | 0=关闭，1=开启 | `0x12` | `(state << 16) | 0x13` |
| 2 | 0=关闭，1=低亮度，2=高亮度 | `0x22` | `(state << 16) | 0x23` |
| 3 | 0=关闭，1=低亮度，2=高亮度，3=自动 | `0x32` | `(state << 16) | 0x33` |

先查 `0x01`，bit0 必须为 1，levels=`raw >> 1`；未知值停止探测，不盲试其他协议族。GET 同样要求 bit0=1；levels=3 额外要求 `(raw >> 1) & 0x8000`，再取 `(raw >> 1) & 0x7FFF` 为状态；其余族直接取 `raw >> 1`。所有状态须在 capability 范围内。用户提供的 Yoga Pro 16 IPH11 / 83SF 实测样本为 capability `0x07`、GET `0x10001/0x10003/0x10005/0x10007`，测试将这些数据作为协议样本，不用来筛选设备。

`KeyboardBacklightService` 复用 `SerializedFeatureService` 的串行队列及有限等待：启动/完整窗口获焦只 Probe/Get；显式设置时重新 Probe/Get，验证当前族及目标档后才 SET，随后立即 GET，匹配才发布确认状态。SET 返回值不作为档位；失败隐藏分组，不乐观更新、不自动重试写入。下一次成功刷新可恢复分组。UI 仅保存最近一次硬件快照用于渲染，不保存目标偏好，不设定时器，不写 OEM 注册表，不读取 SmartEngine，不与 Hot Keys 通信，不创建自动场景。

完整窗口分组顺序为麦克风降噪、声纹管理、键盘背光、开盖开机，键盘背光复用现有卡片、pending 边框、分组讲述人名称和等高布局；迷你窗口不新增此项。所有档位单行排列，四档 UI 顺序为“关闭、自动、低亮度、高亮度”，对应底层编码 `[0, 3, 1, 2]`，不改变协议编码。Fn+Space 与程序均作用于底层 EC，界面在下一次获焦/操作时重新读取，不使用全局键盘钩子。保留 LenovoFnAndFunctionKeys 正常运行，其 OSD 不由本项目接管。

自动检查仅使用模拟设备，覆盖三族全部档位/命令、extended flag、越界/无效能力、SET 前重新探测、立即回读、失败隐藏、Fn+Space 外部状态变化、句柄释放及 UI 档位排序；不调用真实 EnergyDrv。集成版键盘背光已在 Yoga Pro 16 IPH11 上测试通过。参考项目提供 ThinkBook/IdeaPad（小新）EnergyDrv 支持，因此预期同协议设备兼容；这些机型的本功能仍待实机验证，不增加型号白名单或以文档宣称替代 Probe/Get。

2026-10-21：主程序 Release 编译通过（0 警告、0 错误），完整 dev 发布至 `publish/5.3-keyboard-dev/`，含更新/保活助手及 PRI；最终单行排序版发布文件与无硬件逻辑检查 **1381 项通过**。随后按用户要求启动 dev，用户确认 Yoga Pro 16 IPH11 键盘背光测试完毕；自动检查本身未调用真实背光驱动。

## 音频与声纹安全

Elevoc 使用自建 Direct RPC：RPCRT4、`ncalrpc:ElevocRpcEndpoint`、NDR32，启动执行只读 ABI 自检。不加载 ElevocUAPO.dll，不依赖版本、SHA256 或 RVA。Core Audio 使用完整 x64 PROPVARIANT（24 字节，指针偏移 8），内置阵列按 JackSubType 的 MIC_ARRAY/PROCESSING_MIC_ARRAY 判断，不靠设备名猜测。

麦克风模式为 `environmental/private/shared/special/voiceid`。关闭统一写入 `environmental`，不区分耳机/内置；`off` 仅保留旧状态恢复兼容。设置后先等待 150 ms，再通过有限回读确认；扬声器同样不因一次迟延回读就立即报错。回读只查询、不重发设置命令；最终不匹配提示请求/实际模式和设备。耳机不提供多人通话；仅听机主只在 private/voiceid 下显示，人声定位只在 shared/special 下显示，且均要求内置麦克风及对应能力。未注册声纹时仅听机主禁用。

声纹注册顺序：保存原模式 → private → 48 kHz/20 秒录音 → mono FLOAT32 WAV → SetSpeakerID(file) → registered → voiceid → 有效 data[192] → 最终复核。不修改 timestamp，不依赖 debug PCM 或声纹磁盘格式；private 返回 metadata-only 不视为声纹丢失。

录制/注册期间禁用麦克风及扬声器降噪，所有音频操作共用原来的串行队列。实测录制期间并发操作扬声器可令 Elevoc 状态异常，已撤回独立 downlink RPC 通道；不要重新引入。用户确认重启本机 `ElevocService` 即可恢复此异常：先停止录制并退出应用，再重启服务、重新启动程序，不自动替用户重启服务。

录制按钮复用为停止录制，朗读示例只在录制期间显示且位于状态下方；倒计时保留最新进度，其他面板刷新不重置。停止丢弃本次录音，不提交声纹；提交阶段按钮禁用。删除前先关闭仅听机主并通过回读验证，再删除数据。设备变化取消录制，不向新端点恢复旧模式。

WinMM 清理按 Stop → Reset → Unprepare → Close → 释放内存；任一步失败保留驱动可能仍引用的内存，不强行释放。正常结束检查缓冲区长度和 Reset 结果。注册失败尽力恢复原模式，完成/失败/取消后清理临时文件。

## UI 与辅助功能

- 大窗口为可滚动纵向分组，默认宽度 492 DIP；四周 Padding 8 DIP，组标题 14 DIP SemiBold，卡片最小高 48 DIP、字体 12.5 DIP。同排按钮 Stretch、允许换行，不固定高度截断文字。
- 大窗口“独显控制”按“结束独显占用进程、重启独显设备、保持独显唤醒”单行三列排列；小窗口仅前两个操作，单行两列。
- 小窗口提供性能、充电、独显控制的结束进程/重启设备和独立退出；不提供保活卡片，无型号、杂项、自启、开盖开机。退出最小高 28 DIP，宽度与功能组总宽度对齐。
- 分组只用独立灰色分割线，按钮无静态填充，灰色/强调色边框表示状态。原生指针各状态共享同一个 Outline 画刷，修改 Color 即时刷新，不替换 Style。等待回读保留原选中项，给请求项加待确认边框，说明确认后才切换。
- 复用按钮、网格、说明容器及最新回调，操作期间保留状态行，不临时禁用普通按钮导致焦点跳转；通过忙碌标志阻止重复执行。真实能力/录制限制仍禁用。开盖开机只有操作后追加重启提示。
- 两窗口使用原生 MicaBackdrop、透明根背景，由系统处理获焦/失焦过渡和纯色回退；大窗口只获焦时强制强调色细边框，失焦恢复系统默认，小窗口保持原有边框行为。
- 大窗口内容延伸至标题栏、拖动区到顶，保留主题适配的系统控制按钮，按 Inset/DPI 避让。标题获得初始焦点但不画焦点框，随后 Tab 从首个可用按钮开始；开窗请求前台，后台更新不抢焦点。
- RepositionThemeTransition 移动组/分割线，ContentThemeTransition 只用于实际变化的说明，BrushTransition 100 ms 提供悬停高光，不缩放。遵循系统动画开关，主题次要文字不叠加 Opacity。
- 滚动条首次提示约 1 秒，滚动及触屏惯性期间显示，结束后约 180 ms 隐藏，原生淡入/淡出；悬停/拖动保持可见。普通模式放在右侧 8 DIP 留白内；系统始终显示滚动条时保留原生宽度并额外避让，不覆盖分割线。
- 讲述人名称包含当前功能分组，例如“性能选项中的性能模式”；选项状态及完成/错误通知可读，录制倒计时不逐秒播报。支持 Tab/Shift+Tab/空格、Esc、触屏及高对比度。

桌面上直接订阅 AccessibilitySettings.HighContrastChanged 曾抛出 `0x80070490`。使用常驻窗口的 WM_SETTINGCHANGE/WM_THEMECHANGED 更新对比度及滚动条偏好，保留已验证的 UISettings.ColorValuesChanged；不要重新引入不兼容的事件订阅。框架主题资源不通过 Application.Resources 索引直接读取。

## 配置与隐私

- `%LOCALAPPDATA%\YogaITS\settings.json`：`GpuWorkingMode` 显卡策略、`GamutApplyBrightness` 色域亮度偏好及默认关闭的 `KeepDGpuAwake` 保活配置。保活暂停不会清除开启配置。
- `%LOCALAPPDATA%\YogaITS\ColorCalibration\Factory\`：验证后的工厂 ICC 源文件。
- 程序不创建 `Logs` 目录或写入日志文件；错误仍通过界面提示，原生 TCON 诊断回调采用无输出适配，不改变探针链路行为。
- 在线恢复弹确认框，默认取消；仅确认后向 Lenovo 发送序列号。下载使用 `Accept: */*`，下载与暂存副本均校验后替换缓存。不能给 ZIP 强制使用 JSON-only Accept。
- 录音只由明确用户操作触发；不将声纹向量或完整序列号写入常规日志。提交前检查端点身份与取消状态，删除声纹需二次确认。

## 自动检查与只读诊断

```powershell
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release -- --icon-executable .\dist\YogaITS.Tray.exe --updater-executable .\dist\YogaITS.Updater.exe --gpu-restart-executable .\dist\YogaITS.dGPURestart.exe
dotnet run --project .\Tests\YogaITS.ReadOnlyProbe -c Release
```

PolicyChecks 直接复用原探针 SupportsMode 验证目标能力位，并覆盖 ACM 操作能力/设置与有效模式独立性/策略位组合、未知模式/后端选择矩阵、ITS/供电能力、显卡策略、HPD 能力、HDR/ACM 实际状态与偏好、电池策略、结构大小/偏移、ICC 身份与亮度、声纹数据、串行读写/关闭、启动参数、DPI/工作区及录制文案等。5.2 新增 OptimizationChecks 验证刷新合并/写优先、在途超时与过期写入取消、按功能版本合并、充电驱动互斥、进程身份保护、下载上限、ZIP 路径安全、完整更新文件清单及成功暂存保留期。逻辑检查不操作真实硬件、注册表、麦克风或网络；不带图标/助手参数时检查数量不同。

5.4 增加显卡 WMI/NVAPI 模拟矩阵、偏好与真实状态分离、外接屏唯一偏好例外、有限补查/重试/取消、状态文字缓冲，以及独显重启助手身份校验与窗口恢复检查。当前基础检查 **1632 项通过**，包含完整发布物的检查 **1640 项通过**；另运行 `Tests/ReleaseVersionChecks.ps1` 验证版本命名。真实驱动、显示器插拔和设备重启依赖实机验收。

2026-10-13 TCON 集成后，本机只读诊断确认选择 `Icc`，7 个有效工厂模式及 HDR 实际状态读取正常；未执行 TCON/ICC 色域写入、HDR 切换、音频设置、录音或在线下载。非管理员 GPU 探测仍为拒绝访问，不能据此判定能力不存在。

ReadOnlyProbe 并行读取四组能力与状态，不调用 setter、不录音、不下载。故意不调用 GPU InitializeAsync，避免保存的 Auto 策略触发写入；建议管理员运行，拒绝访问不能据此判定能力不存在。

可选网络诊断：

```powershell
dotnet run --project .\Tests\YogaITS.ReadOnlyProbe -c Release -- --check-calibration-http
```

该选项发送本机序列号，比较 Lenovo 元数据接口的 Accept 请求头，不下载 ZIP、不修改缓存。只有明确同意发送序列号时才能执行。自动检查与只读探测不能替代写入及 UI 实机验收。

## 实机验收记录

以下是用户确认的完善测试记录；Yoga Pro 16 IPH11 已完成本轮 5.2 全功能回归，其他机型保留既有版本的验证记录。除基准机型外，重点验证与 Yoga Pro 16 IPH11 不同的实现路径，而非仅测试表中一项功能。

| 已完善测试的机型 | 重点验证路径 |
| --- | --- |
| Yoga Pro 16 IPH11 | 基准机型，完整功能及交互验证；5.2 全功能回归、5.3 键盘背光与 5.4 显卡工作模式更新均已实机验证通过。 |
| 小新 Pro 16 IAH7 | 旧版 ITS 服务的性能选项切换。 |
| 小新 Pro 16 IAH10 | 不同屏幕配置下的 ICC 色域切换。 |
| ThinkBook 14 G6+ AHP | TCON 硬件色域控制，P3/sRGB 切换。 |
| ThinkBook 14 G6+ IMH | TCON 硬件色域控制，P3/sRGB 切换。 |
| ThinkBook 14 G7+ IAH | TCON 硬件色域控制，P3/sRGB 切换。 |

新增专项验证：

| 机型 | 已验证范围 |
| --- | --- |
| 小新 Pro 14 IRH8 | 5.4 显卡工作模式控制；换回普通版电脑管家后仍然有效。 |

**Yoga Pro 16 IPH11 的实机验收清单如下，用户已确认全部通过，且 5.2 优化后的所有功能均已重新验证成功。** 这些结论来自用户实机验收，不是自动化测试操作真实硬件的记录。其他机型的本轮回归仍需确认，不承诺全系列兼容。

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

2026-10-19：5.2 优化提交 `959e4a8` 的主程序与只读探针编译通过，完整发布检查为 **1111 项通过**；另运行仅使用自建父/子进程的进程树测试及版本命名检查。用户已确认 5.2 优化后的所有功能在 Yoga Pro 16 IPH11 上完成实机回归并全部通过。后续代码更改或其他机型仍须由对应硬件用户验收。

2026-10-19：提交 `77c8984` 新增息屏暂停、亮屏按条件恢复独显 helper；主程序编译通过，包含两个助手的完整 dev 发布检查 **1267 项通过**。用户先确认该 dev 版息屏/亮屏测试成功，随后进一步确认 5.2 优化后的所有功能在 Yoga Pro 16 IPH11 上均已重新验证成功。

2026-10-21：用户确认 5.3 键盘背光在 Yoga Pro 16 IPH11 上实机测试通过，最终 UI 为单行“关闭、自动、低亮度、高亮度”。ThinkBook、小新仅作为相同 EnergyDrv 协议的预期兼容对象，尚未收到本功能实机验收结果。

2026-10-23：最终 dev 版 `5.3-20261023094009-07f7e66a` 在 Yoga Pro 16 IPH11 上获用户确认验证成功；其显卡工作模式实现作为 5.4 合入 main，包含 NVAPI 显示占用保护、独立偏好协调及有限回读后的状态显示。小新 Pro 14 IRH8 已专项确认可由 YogaITS 控制显卡工作模式：原机普通版管家没有选项，安装 AI 智能场景版后虽出现选项，但调节无效且不同步；随后换回普通版管家，YogaITS 仍然有效。该机型当前验收范围为显卡控制路径，管家失败原因尚未确定。

## 许可证、来源核查与分发（6.0）

YogaITS 原创部分采用标准 `BSD-3-Clause`，正文为根目录 `LICENSE`，版权声明为 `wfsg00098 and YogaITS contributors`。允许商用、修改、闭源及源码/二进制再发布；保留版权、条件及免责条款，禁止未经允许以权利人或贡献者名义背书。`NOTICE` 补充独立项目、非厂商官方产品和第三方范围说明，不给标准 BSD 另加条件，也不承诺绝对排除法律规定不能免除的责任。

2026-10-26 核查范围为当前源码、直接与传递 NuGet 依赖，以及普通版/玻璃版实际发布文件。`ThirdParty/Licenses/components.json` 固定组件版本、适用构建、来源、原始许可/NOTICE 路径及 SHA-256；原件按字节保留，相同文档去重保存。不要把包内许可链接或上游仓库的 MIT 标记直接当作二进制分发许可。

- **玻璃 UI**：LiquidGlassWinUI 1.0.3（含包内原样使用的 `CustomEffectRuntimeNative.dll`）和 LiquidGlassTabBars 参考动效为 MIT。保留各自许可及版本，材质本地修改见 `ThirdParty/LiquidGlassWinUI/UPSTREAM`。不分发 Apple 平台玻璃 API。
- **微软依赖**：Windows App SDK / WinUI 成品包按其各自微软条款分发；WinUI 2.2.1 的许可与 SDK 总包并不相同，两份都保留。.NET 库为 MIT，WebView2 该版 SDK 为 BSD 三条款式许可；随包保留其第三方声明。Windows SDK .NET 投影另保留 SDK 原始许可及 NOTICE，C#/WinRT、C++/WinRT 保留 MIT。未分发完整 .NET、Edge/WebView2 浏览器运行时或 OEM 驱动。SDK 的 AI/ML/Widgets 禁用包不是已交付功能，不能仅因存在于 restore/deps 元数据就计为分发的二进制。
- **Win2D 1.3.2**：NuGet 明确指向 `eula_win2d_10012014.htm`，没有内嵌许可；上游 [#912](https://github.com/microsoft/Win2D/issues/912)、[#997](https://github.com/microsoft/Win2D/issues/997) 也记录了二进制许可及失效链接问题。已从原微软网页存档取回完整 HTML，按原始字节保存为 `Microsoft-Win2D-binary-EULA.html`，来源请求与哈希见清单。它允许随具有主要功能的 Windows 程序分发目标代码，要求保留声明及传递适用条款，并限制背书、平台和再许可。源代码 MIT 文本单独保留；不声称这会替代 NuGet 二进制条款。存档是原网页的历史证据，并非微软对本项目的单独授权或新的许可确认。
- **LLT 来源边界**：逐项对照固定 master `d0c57bfc` 中 ITSModeFeature/Driver、BatteryFeature、BatteryNightChargeFeature、FlipToStart 三种 Feature、GPUController、PhyscialGPUExtensions、Compatibility、DgpuAwakeManager，以及 dev `9baf5a01` 的保活管理。YogaITS 不链接/分发 LLT、NVAPIWrapper 或其类实现。相同项主要为设备路径、IOCTL、寄存器/注册表字段、模式编码、ABI 与兼容性事实；异步队列、状态机、独立助手、回读和 UI 为本项目实现。发现 ITS 中一段与 LLT 相同的通用整数转换辅助函数，已移除该实现，改为按 .NET 注册表 DWORD/QWORD 文档处理真实 Int32/Int64；不再保留复制的转换分支。此处理不把 LLT 改为 BSD，后续复制或改编其受保护代码须另行遵守 GPLv3，不能凭“没有引用 DLL”或改名排除 GPL。
- **键盘背光参考**：SaltwaterC 项目为 MIT；gonwan 仓库未找到明确源码许可。仅使用接口/协议事实和本机回读验证，未搬入后者的 C 实现；公开可读不等于允许复制。保持底层能力检测和 YogaITS 自有后端，不重现 OEM 业务系统。
- **NVIDIA/OEM**：手写互操作调用系统已装驱动，未打包 `nvapi64.dll`、NVAPI 静态库、联想/音频厂商 DLL 或固件。校色文件在用户机器按需取得，不随源码或程序包分发，也不由项目 BSD 许可授权。

这是所列版本与代码范围的工程来源核查记录，不把短片段比较视为法律认定，也不将第三方条款转授为 BSD。新增外部源码、改编代码、包或静态链接内容时，应先更新记录并保留上游声明；来源不清或 GPL 派生边界未解决时，不得将它直接标为项目原创 BSD。

正式构建在全部 publish 完成后执行 `scripts/Copy-DistributionNotices.ps1`，两版均附带根 `LICENSE`/`NOTICE`、第三方汇总、分构建清单和相应原件。脚本按实际发布资产核对 `.deps.json`，发现未登记的组件版本或许可文本哈希变化即终止构建；不在 CI 联网临时抓取许可。四个 YogaITS EXE 的版本信息含本项目版权，第三方项目不套用该字段；`PublishedAssemblyChecks` 检查发布后的归属、版权、构建类型和每份原件哈希，完整 ZIP 提取/更新验证继续执行。Actions 复用同一构建入口；裸 `dotnet publish` 不是完整的发布流程。

## 提交与维护

使用简明祈使句提交标题，例如 `Fix tray recovery after Explorer restart`。提交说明应区分 UI 变化、硬件写入与只读检查；PR 提供行为变化、相关问题、构建/硬件验证记录和 UI 截图，不提交生成产物或敏感日志。保留可回滚的独立提交，涉及驱动、声纹或校色写入时明确验证范围。
