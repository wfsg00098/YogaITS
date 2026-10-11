# 集成规则与验收记录

## 范围与结构

独立目录从 YogaITS-main 基线开始，用 Git 分阶段管理。四组功能按各自最终集成说明实现；沿用管理员进程，不新增常驻 helper 或厂商客户端 DLL 依赖。

业务服务继承 SerializedFeatureService，同一功能的读、写、关闭串行执行；不同功能的首次检测并行执行。所有写入由用户操作或已选择的 GPU 智能策略触发。正常运行无周期轮询。

右键保留原有快捷菜单并移除杂项。左键使用共用控制组件构建纵向单列窗口：性能、充电、独显休眠、新增功能、杂项；不因失去焦点关闭，支持滚动和缩放。两种窗口共用缓存和设置回调。

控制中心每次从非激活变为激活时并行刷新各组能力和状态，刷新期间不叠加批次；没有手动刷新按钮。HDR/ACM 复用选项卡片，点击切换、选中表示开启。大窗口内容延伸至标题栏，以固定页面头部作为 SetTitleBar 拖动区；头部布局边界从窗口 y=0 开始，文字间距放在内部 Padding，支持靠近顶部拖动。保留系统三个控制按钮，图标按 ActualTheme 使用浅色黑/深色白，失焦使用可读灰色，背景透明，悬停及按下使用低透明度对比色；主题变化时更新，关闭窗口时取消订阅。根据 LeftInset / RightInset 和 RasterizationScale 预留按钮区域。型号在主标题下方左对齐，无胶囊装饰；小窗口不显示型号。只有边框强制显示系统强调色，无视系统对应显示开关，不修改 Windows 全局设置。实现依据 [自定义标题栏接口](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar)。

大小窗口均采用 `Window.SystemBackdrop = new MicaBackdrop()`，不再叠加 DWM Acrylic。窗口激活时根背景透明以露出 Mica，失活时通过 ThemeResource 还原纯色基础背景。分组无背景或外围框，仅底部灰色分割线；按钮复用 WinUI 默认模板，无填充，灰色/强调色边线分别表示未选中/选中，悬停及按下不增加背景填充。小窗口移除 WS_EX_NOACTIVATE，第一次显示才激活；能力重建只调整尺寸，不重复抢焦点。保留 TOOLWINDOW、外部点击关闭及原有无滚动菜单。参考 [Mica 材质](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)及[系统背景接口](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)。

大窗口所有分组标题共用 SettingsSectionTitleStyle（14 DIP、SemiBold、主题主文本色、22 DIP 最小高度），标题与选项间隔 8 DIP。大窗口所有选项及退出按钮统一使用 48 DIP 最小高度，文字统一为 12.5 DIP（原为 10.5/11 DIP），长标签仍允许换行并自然增高，不设置裁切用的固定高度；退出按钮复用无填充边线卡片，并与组内选项对齐。小窗口保留原字号、紧凑标题与退出布局。

大小窗口在用户通过托盘打开时共用 ShowInForeground：最小化时先恢复，再 Activate 并调用 SetForegroundWindow 请求前台焦点；再次点击已存在的大窗口也走此路径，不重设尺寸或位置。后台刷新不调用前台激活。遵循 [Windows 前台激活规则](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)，不通过模拟输入或绑定线程绕过系统限制；实机焦点行为仍需确认。

扬声器降噪排在麦克风降噪之前，设备状态分别标明“当前扬声器设备”和“当前麦克风设备”。性能、充电、独显休眠、唤醒/睡眠、播放/暂停及两类降噪均有与色域相同的说明文字样式；独显操作说明包含进程数据丢失与显示闪烁风险。杂项与退出之间没有分割线。说明只在大窗口显示，不增加快捷菜单高度。

大窗口页面、分组及选项按钮的 Padding 统一为 8 DIP，普通分组间距也为 8 DIP。型号头部去掉额外最小高度留白及底部外边距，型号下方至首个分组标题由分组顶部 8 DIP Padding 控制。头部拖动表面仍延伸至窗口顶边，按钮避让宽度按系统 Inset 单独计算。滚动视口向右延伸 8 DIP，内容使用右侧 8 DIP Padding 保持左右对称；原生竖向滚动条横向缩放并右对齐，展开后也限制在已有的右侧 8 DIP 留白内，不再额外占用内容宽度或覆盖分割线。保留原生悬停、拖动、滚轮及键盘滚动行为，仅修改大窗口。杂项与退出保留较小的独立 footer Spacing（4 DIP），杂项底部不叠加内边距。

功能卡片没有状态文字时折叠状态行，底部没有可见操作时折叠 Footer，避免唤醒/睡眠及播放/暂停说明下方出现空白行。错误提示仍可显示；声纹注册时取消按钮及其 Footer 同步显示，录制结束后同步折叠。

## 能力与状态源

| 功能 | 显示/启用规则 |
| --- | --- |
| GPU 工作模式 | IsSupportIGPUMode.Data > 0；实际状态来自 GetIGPUModeStatus，逻辑模式由本应用保存 |
| 集显选择 | GetIGPUMHDMIStatus 必须为 0；未知状态也禁用，写入前再次读取 |
| GPU 智能模式 | 电源通知注册成功；AC 目标双显，DC 且独显显示路径未占用时目标集显；电源未知时不切换 |
| 色域 | 内屏、HWID、动态 WCS DeviceID、机器身份及 LCD 亮度通道可读取；Factory 缺失为可恢复状态 |
| 色域写入 | HDR/ACM 关闭、activeColorMode 为 SDR、Advanced Color 可读取；ICC 的 CxF 必须匹配本机序列号、面板及 Machine Type |
| HDR / ACM 开关 | 对应 supported 或 userEnabled 为真时显示；策略限制时禁止开启；HDR 开启时禁止开启 ACM，仍允许关闭已开启项 |
| 唤醒/睡眠 | GetAll rc=0 且 unAwareAbility 非零 |
| 人员辅助判定 | 唤醒/睡眠支持、sensor_type != 4、capability & 0x40 非零 |
| 播放/暂停 | GetAll rc=0，且 sensor_type 为 3/4 或 unAwareAbility 非零 |
| 麦克风/扬声器降噪 | 当前 Capture/Render endpoint 声明 uplink/downlink，且对应 GetMode 返回 OK |
| 人声定位 | 当前麦克风可用且声明 special；仅使用 shared/special 用户模式 |
| 声纹/仅听机主 | 当前麦克风可用、声明 speaker_extraction、IsVoiceidSetted 返回 OK；未注册时禁用仅听机主 |

原有功能保留 Lenovo ITS/EnergyDrv/FlipToStart 的能力检测。NVIDIA 存在性在后台刷新时重新读取，平台切至集显后独显休眠区域可动态隐藏。

## 写入约束

- GPU：直接对枚举获得的 WMI 实例 InvokeMethod；不读取 ReturnValue。设置后等待并回读。Auto 的 DC 过渡最多重试 5 次、间隔 5 秒；AC/拓扑事件及手动选择取消旧重试。
- 色域：Factory 源不修改；保留 managed ICC 注册刷新、字节验证、association、STANDARD 回读、WCS、可选亮度和最后的 C1 DWM 刷新。模式来自当前 STANDARD，不自动恢复旧色域。
- HDR / ACM：通过 DisplayConfigSetDeviceInfo 的 SET_HDR_STATE / SET_WCG_STATE 设置当前内屏 target；串行写入并有限回读确认。结构与标志依据 [Windows SDK 定义](https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/wingdi.h)，关闭后只刷新能力，不应用 ICC。
- 在线恢复：只在用户点击时下载；完整序列号仅用于 Lenovo 请求及本机校验。下载和 staged copy 验证通过后替换缓存。
- 麦克风降噪：2026-10-11 根据管家实测纠正关闭映射，内置麦克风及耳机均发送并回读 `environmental`，不再使用 `off` 执行关闭。客户端保留 `off` 仅用于旧状态恢复兼容。设置成功后等待 150 ms 再回读；不匹配时显示请求模式、实际模式和当前设备。不自动重写模式，耳机插拔仍只刷新。
- HPD：保留 ContextCreate/ContextClose 生命周期；设置后检查 rc 并回读；不暴露距离或 YogaMode。
- 音频：设备变化只刷新能力和状态，不重写偏好模式。Voice ID 删除二次确认。注册按 private → 48 kHz/20 秒 FLOAT32 WAV → SetSpeakerID(file) → registered → voiceid → data[192] → 最终复核。
- 注册失败尽力恢复原模式；设备变化后不向新端点恢复模式。数据需 192 维有限数值，FLOAT32，L2 接近 1。私有模式的 metadata-only 返回不视为声纹丢失。timestamp 交由服务管理。

## 已执行验证（2026-10-10）

- Windows .NET SDK 10.0.401 下各功能阶段编译通过。
- Release clean publish 通过，PRI 已验证；发布目录 39 个文件，约 39.47 MB。
- PolicyChecks：92 项通过，覆盖 GPU 电源/外接路径矩阵、HPD 能力组合、HDR/ACM 阻断与支持位、24 字节设置结构及 target 寻址、ICC 身份/亮度、声纹校验、读写串行化及关闭行为。未操作硬件。
- ReadOnlyProbe（非管理员）：HPD 三组能力为 true；音频 uplink/downlink/special/voiceId 为 true、registered 为 false；色域检测可用、7 个有效模式、当前 blocked=true。
- 同次 GPU 只读探测为不可用，具体错误为“拒绝访问”；管理员路径尚未实机验收，不能据此判定硬件不支持。
- 色域禁用原因已确认：Advanced Color raw=0x73、mode=2，HDR=true、ACM=false；工厂文件和七个模式验证正常。
- 经明确授权，只读请求 Lenovo 校色元数据接口：Accept 为 application/json、*/*、缺省时均 HTTP 200，未复现元数据阶段 406。下载共用客户端改为 Accept: */*，避免 ZIP 被 JSON-only 请求头限制；ZIP 下载与完整在线恢复未复测，不能认定 406 已解决。
- 纵向单列、焦点刷新、HDR/ACM 选项卡片和标题栏主题配色调整后，Debug 编译为 0 警告 / 0 错误，92 项逻辑检查再次通过，Release 发布及 PRI 文件检查通过；桌面交互和配色仍需实机确认。
- 大小窗口 Mica 与共享主题卡片调整后，Debug 编译为 0 警告 / 0 错误，92 项逻辑检查再次通过；尚未进行桌面焦点、透明效果及高对比度验收。原 dist 首次被运行进程占用，用户退出后重新发布至 dist。

## 待实机验收

- [ ] 退出原版后启动 dist/YogaITS.Tray.exe，确认左右键行为、大窗口布局、主题、不同 DPI、窄窗口及关闭后驻留。
- [ ] 大小窗口获得焦点时显示 Mica，Alt+Tab 失焦后显示纯色；浅深主题及关闭透明效果/高对比度下回退正常；小窗口打开后外部点击仍关闭，能力刷新不抢焦点。
- [ ] 大窗口头部拖动、双击最大化、系统三个控制按钮及不同 DPI 下的按钮避让正常；滚动内容时头部保持固定，型号仅在大窗口主标题下方显示。
- [ ] 大窗口与右键菜单同时打开，确认原有设置状态同步；Explorer 重启后托盘恢复。
- [ ] 管理员运行验证 GPU 双显/集显互切、AC/DC 智能切换、外接独显屏保护、拔屏重评估和重启后 Auto 恢复。
- [ ] 验证 HPD 各开关和快/中/慢回读；缺少 SmartSense 服务时隐藏。
- [ ] 在色域卡片切换 HDR/ACM，确认回读状态与禁用原因；关闭后反复切换色域；验证亮度开关、内屏拓扑变化、在线恢复成功与身份不匹配阻断。
- [ ] 验证麦克风/扬声器各模式、耳机插拔后能力变化；设备切换只刷新、不覆盖服务 preferred mode。
- [ ] 验证 20 秒声纹注册、取消、录制途中设备变化、重新注册及删除；private 下读取不误报声纹丢失。
- [ ] 缺少相关驱动/服务的机器隐藏相应功能，恢复服务后切回窗口重新检测；验证获得焦点仅触发一次刷新、不叠加批次。

这些验收包含实际硬件写入、录音及桌面交互；自动检查没有代为执行。
