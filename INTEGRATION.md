# 集成规则与验收记录

## 范围与结构

独立目录从 YogaITS-main 基线开始，用 Git 分阶段管理。四组功能按各自最终集成说明实现；沿用管理员进程，不新增常驻 helper 或厂商客户端 DLL 依赖。

业务服务继承 SerializedFeatureService，同一功能的读、写、关闭串行执行；不同功能的首次检测并行执行。所有写入由用户操作或已选择的 GPU 智能策略触发。正常运行无周期轮询。

右键保留原有快捷菜单，并按最新需求在末尾显示杂项：开机自启与退出同组两列，均为 56 DIP 最小高度的同款边线卡片，中间无分割线；不显示开盖开机。左键使用共用控制组件构建纵向单列窗口：性能、充电、独显休眠、新增功能、开盖开机、杂项；不因失去焦点关闭，支持滚动和缩放。两种窗口共用缓存和设置回调，大窗口的开盖开机独立分组及 48 DIP 杂项卡片保持不变。

控制中心每次从非激活变为激活时并行刷新各组能力和状态，刷新期间不叠加批次；没有手动刷新按钮。HDR/ACM 复用选项卡片，点击切换、选中表示开启。大窗口内容延伸至标题栏，以固定页面头部作为 SetTitleBar 拖动区；头部布局边界从窗口 y=0 开始，文字间距放在内部 Padding，支持靠近顶部拖动。保留系统三个控制按钮，图标按 ActualTheme 使用浅色黑/深色白，失焦使用可读灰色，背景透明，悬停及按下使用低透明度对比色；主题变化时更新，关闭窗口时取消订阅。根据 LeftInset / RightInset 和 RasterizationScale 预留按钮区域。型号在主标题下方左对齐，无胶囊装饰；小窗口不显示型号。只有边框强制显示系统强调色，无视系统对应显示开关，不修改 Windows 全局设置。实现依据 [自定义标题栏接口](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar)。

显卡刷新独立于窗口焦点：常驻隐藏消息窗口监听 AC/DC、电源状态、显示拓扑事件；设备节点变化也走 750 ms 显示防抖，覆盖外接屏/扩展坞插拔但分辨率未改变的情况。睡眠恢复同时刷新电源与屏幕状态。所有模式均读取实际 GPU、外接独显路径及 NVIDIA 存在性，只有逻辑 Auto 应用切换策略。GPU 完成读取或重试后直接更新 NVIDIA 存在性缓存并刷新界面，事件处理完成后还刷新两种菜单的核心状态，不因大窗口的整批刷新、失焦、最小化或关闭而跳过；无常态轮询。事件依据 [电源通知](https://learn.microsoft.com/en-us/windows/win32/power/power-management-events)及[设备变化通知](https://learn.microsoft.com/en-us/windows/win32/devio/wm-devicechange)。

大小窗口均采用 `Window.SystemBackdrop = new MicaBackdrop()`，不再叠加 DWM Acrylic。窗口激活时根背景透明以露出 Mica，失活时通过 ThemeResource 还原纯色基础背景。分组无背景或外围框，仅底部灰色分割线；按钮复用 WinUI 默认模板，无填充，灰色/强调色边线分别表示未选中/选中，悬停及按下不增加背景填充。小窗口移除 WS_EX_NOACTIVATE，第一次显示才激活；能力重建只调整尺寸，不重复抢焦点。保留 TOOLWINDOW、外部点击关闭及原有无滚动菜单。参考 [Mica 材质](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)及[系统背景接口](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)。

大窗口所有分组标题共用 SettingsSectionTitleStyle（14 DIP、SemiBold、主题主文本色、22 DIP 最小高度），标题与选项间隔 8 DIP。大窗口所有选项及退出按钮统一使用 48 DIP 最小高度，文字统一为 12.5 DIP（原为 10.5/11 DIP），长标签仍允许换行并自然增高，不设置裁切用的固定高度；退出按钮复用无填充边线卡片，并与组内选项对齐。小窗口保留原字号、紧凑标题与退出布局。

大小窗口在用户通过托盘打开时共用 ShowInForeground：最小化时先恢复，再 Activate 并调用 SetForegroundWindow 请求前台焦点；再次点击已存在的大窗口也走此路径，不重设尺寸或位置。后台刷新不调用前台激活。遵循 [Windows 前台激活规则](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)，不通过模拟输入或绑定线程绕过系统限制；实机焦点行为仍需确认。

扬声器降噪排在麦克风降噪之前，设备状态分别标明“当前扬声器设备”和“当前麦克风设备”。性能、充电、唤醒/睡眠、播放/暂停及两类降噪按当前选项显示说明；独显操作保留数据丢失与显示闪烁风险说明。显卡工作模式只保留原有智能模式说明。人员辅助判定标为“人员辅助判定（面部识别数据）”，无服务实现或能力缺失提示。开盖开机单独分组，说明重启后生效；开机自启与退出位于杂项组同一行两列。说明只在大窗口显示，不增加快捷菜单高度。

色彩管理按钮使用“管家选项（对应颜色配置）[亮度]”：Default→出厂默认（D65）、Native→鲜艳（Native）、sRGB→网页办公（sRGB-D65）、DisplayP3→摄影/数码影院（P3-D65）、DCIP3→原生数码影院（DCI-P3）、AdobeRGB→摄影印刷（Adobe-RGB-D65）、REC709→SDTV视频（REC709）。只修改显示文字，底层 profile ID、工厂亮度和 HDR/ACM 写入保护不变。

HDR/ACM 卡片选中状态、颜色提示和色域写入判断使用 `activeColorMode`，不把 `userEnabled` 偏好位当成实际启用状态。实际 HDR 或 WCG 时，颜色状态统一显示“当前颜色配置：不适用。HDR或者ACM已开启。”；实际 SDR 时恢复当前配置及校色文件提示，即使 HDR 启用偏好仍为真，也允许通过其余安全校验后切换色域。状态不可读或未知非 SDR 模式仍阻止写入。电源、屏幕事件独立于窗口焦点触发色彩刷新，750 ms 防抖后读取并延迟复核一次；后续事件取消旧延迟，退出时停止，无常态轮询。电源通知不依赖 GPU 模式能力。

大小窗口均将夜间优化作为第四个互斥充电选项。选择夜间优化先开启夜间标志，再设置常规模式；选择另外三个选项先关闭夜间标志，再设置目标模式。两窗口共享串行写入入口，任一步失败停止后续写入，最后回读两项状态并一次更新缓存；夜间开启时只选中夜间优化，不同时选中常规模式。大窗口只显示当前选项的一条说明。

大窗口页面、分组及选项按钮的 Padding 统一为 8 DIP，普通分组间距也为 8 DIP。型号头部去掉额外最小高度留白及底部外边距，型号下方至首个分组标题由分组顶部 8 DIP Padding 控制。头部拖动表面仍延伸至窗口顶边，按钮避让宽度按系统 Inset 单独计算。滚动视口向右延伸 8 DIP，内容使用右侧 8 DIP Padding 保持左右对称；原生竖向滚动条横向缩放并右对齐，展开后也限制在已有的右侧 8 DIP 留白内，不再额外占用内容宽度或覆盖分割线。保留原生悬停、拖动、滚轮及键盘滚动行为，仅修改大窗口。杂项组末尾不再添加分割线或额外退出行。

功能卡片没有状态文字时折叠状态行，底部没有可见操作时折叠 Footer，避免唤醒/睡眠及播放/暂停说明下方出现空白行。错误提示仍可显示；声纹注册时取消按钮及其 Footer 同步显示，录制结束后同步折叠。

## 能力与状态源

声纹朗读示例仅在录制期间显示，位于录制状态提示下方。录制卡片按钮复用为“停止录制”，不再单独显示取消按钮；录制操作保持卡片可刷新、可点击，其他声纹操作仍禁用。停止后丢弃本次内容、删除临时录音并尽力恢复原模式，不向 Elevoc 提交声纹；进入提交/验证阶段后按钮禁用。WinMM 清理统一按 Stop → Reset → Unprepare → Close → 释放内存执行，任一安全释放步骤失败都保留驱动可能仍引用的内存，不忽略失败后强行释放。正常结束也检查 Reset 结果和缓冲区长度，预取消直接退出，无录音副作用。依据 [waveInReset](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveinreset)、[waveInUnprepareHeader](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveinunprepareheader)及 [waveInClose](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveinclose)。

小窗口的独显操作标签固定为“结束独显\n占用进程”两行，大窗口保持“结束独显占用进程”。大窗口仅获焦时强制强调色边框，忽略系统对应开关；失焦时写入 `DWMWA_COLOR_DEFAULT` 恢复系统默认边框，强调色变化时仍遵循当前激活状态，不修改系统全局设置。小窗口边框行为不变。依据 [DWM 边框颜色与激活状态说明](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)。

| 功能 | 显示/启用规则 |
| --- | --- |
| GPU 工作模式 | IsSupportIGPUMode.Data > 0；实际状态来自 GetIGPUModeStatus，逻辑模式由本应用保存 |
| 集显选择 | GetIGPUMHDMIStatus 必须为 0；未知状态也禁用，写入前再次读取 |
| GPU 智能模式 | 电源通知注册成功；AC 目标双显，DC 且独显显示路径未占用时目标集显；电源未知时不切换 |
| 色域 | 内屏、HWID、动态 WCS DeviceID、机器身份及 LCD 亮度通道可读取；Factory 缺失为可恢复状态 |
| 色域写入 | 实际 activeColorMode 为 SDR、Advanced Color 可读取；ICC 的 CxF 必须匹配本机序列号、面板及 Machine Type |
| HDR / ACM 开关 | 对应 supported 或 userEnabled 为真时显示；选中表示实际 HDR/WCG；点击反转 userEnabled 偏好，HDR 不以策略限制阻止偏好请求，ACM 开启仍检查策略及实际 HDR |
| 唤醒/睡眠 | GetAll rc=0 且 unAwareAbility 非零 |
| 人员辅助判定 | 唤醒/睡眠支持、sensor_type != 4、capability & 0x40 非零 |
| 播放/暂停 | GetAll rc=0，且 sensor_type 为 3/4 或 unAwareAbility 非零 |
| 麦克风/扬声器降噪 | 当前 Capture/Render endpoint 声明 uplink/downlink，且对应 GetMode 返回 OK |
| 多人通话 | 麦克风降噪可用且当前为内置阵列麦克风；耳机及未知类型只显示关闭降噪和单人通话，写入前重新检测并拒绝 shared |
| 人声定位 | 内置阵列麦克风、声明 special，且当前模式属于 shared/special；按钮可切换 shared↔special |
| 声纹/仅听机主 | 内置阵列麦克风、声明 speaker_extraction、IsVoiceidSetted 返回 OK；仅听机主按钮仅在 private/voiceid 显示，未注册时灰色禁用；按钮切换 private↔voiceid |

内置麦克风采用保守阵列类型识别：读取 [PKEY_AudioEndpoint_JackSubType](https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-jacksubtype)，按 Windows SDK 的 MIC_ARRAY / PROCESSING_MIC_ARRAY 分类，并叠加当前 endpoint 的 RPC 能力。未知、普通麦克风或耳机类型不显示方向性子选项，不靠设备名称判断；设备切换仅刷新，不自动写模式。

Core Audio 属性读取使用完整的 x64 PROPVARIANT（24 字节，类型标记偏移 0、指针偏移 8），避免原先仅声明字符串成员时产生的 16 字节缓冲区不足和原生内存越界。策略检查覆盖大小及偏移；已用只读诊断在本机验证内置麦克风和扬声器端点、插孔类型及 Elevoc 模式正常读取。结构依据 [Windows PROPVARIANT 定义](https://learn.microsoft.com/en-us/windows/win32/api/propidlbase/ns-propidlbase-propvariant)。

原有功能保留 Lenovo ITS/EnergyDrv/FlipToStart 的能力检测。NVIDIA 存在性在后台刷新时重新读取，平台切至集显后独显休眠区域可动态隐藏。

## 写入约束

- GPU：直接对枚举获得的 WMI 实例 InvokeMethod；不读取 ReturnValue。设置后等待并回读。Auto 的 DC 过渡最多重试 5 次、间隔 5 秒；AC/拓扑事件及手动选择取消旧重试。
- 色域：Factory 源不修改；保留 managed ICC 注册刷新、字节验证、association、STANDARD 回读、WCS、可选亮度和最后的 C1 DWM 刷新。模式来自当前 STANDARD，不自动恢复旧色域。
- HDR / ACM：通过 DisplayConfigSetDeviceInfo 的 SET_HDR_STATE / SET_WCG_STATE 设置当前内屏 target 的启用偏好；串行写入并有限回读确认偏好，随后重新读取实际模式，不要求实际模式与偏好一致。HDR 请求不修改 Windows 的电池策略，也不在电源事件中自动写偏好；系统拒绝请求时保留错误提示。实际暂时关闭而偏好仍启用时，点击会关闭保存的偏好，卡片仍按回读的实际状态显示。结构与标志依据 [Windows SDK 定义](https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/wingdi.h)，[Windows 电池 HDR 策略](https://support.microsoft.com/en-gb/windows/hardware/display-graphics/hdr-settings-in-windows)可暂时关闭 HDR 并在接回电源后恢复。关闭后只刷新能力，不应用 ICC。
- 在线恢复：只在用户点击时下载；完整序列号仅用于 Lenovo 请求及本机校验。下载和 staged copy 验证通过后替换缓存。
- 麦克风降噪：2026-10-11 根据管家实测纠正关闭映射，内置麦克风及耳机均发送并回读 `environmental`，不再使用 `off` 执行关闭。客户端保留 `off` 仅用于旧状态恢复兼容。设置成功后等待 150 ms 再回读；不匹配时显示请求模式、实际模式和当前设备。不自动重写模式，耳机插拔仍只刷新。
- HPD：保留 ContextCreate/ContextClose 生命周期；设置后检查 rc 并回读；不暴露距离或 YogaMode。
- 音频：设备变化只刷新能力和状态，不重写偏好模式。Voice ID 删除二次确认。注册按 private → 48 kHz/20 秒 FLOAT32 WAV → SetSpeakerID(file) → registered → voiceid → data[192] → 最终复核。
- 注册失败尽力恢复原模式；设备变化后不向新端点恢复模式。删除声纹前，若当前为 voiceid，先切回 private 并通过回读验证；切换失败或设备变化则停止删除。数据需 192 维有限数值，FLOAT32，L2 接近 1。私有模式的 metadata-only 返回不视为声纹丢失。timestamp 交由服务管理。

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

2026-10-11 HDR 实际状态修正：309 项 PolicyChecks 通过，覆盖 SDR/HDR/WCG/未知模式、启用偏好和策略位组合、电池暂时关闭及接电恢复状态；Release 发布与 PRI 检查通过。检查未执行硬件写入，电源切换和 HDR 点击行为仍需以下实机验收。

- [ ] 退出原版后启动 dist/YogaITS.Tray.exe，确认左右键行为、大窗口布局、主题、不同 DPI、窄窗口及关闭后驻留。
- [ ] 大小窗口获得焦点时显示 Mica，Alt+Tab 失焦后显示纯色；浅深主题及关闭透明效果/高对比度下回退正常；小窗口打开后外部点击仍关闭，能力刷新不抢焦点。
- [ ] 大窗口头部拖动、双击最大化、系统三个控制按钮及不同 DPI 下的按钮避让正常；滚动内容时头部保持固定，型号仅在大窗口主标题下方显示。
- [ ] 大窗口与右键菜单同时打开，确认原有设置状态同步；Explorer 重启后托盘恢复。
- [ ] 管理员运行验证 GPU 双显/集显互切、AC/DC 智能切换、外接独显屏保护、拔屏重评估和重启后 Auto 恢复。
- [ ] 验证 HPD 各开关和快/中/慢回读；缺少 SmartSense 服务时隐藏。
- [ ] 在色域卡片切换 HDR/ACM，确认回读状态与禁用原因；关闭后反复切换色域；验证亮度开关、内屏拓扑变化、在线恢复成功与身份不匹配阻断。
- [ ] Windows 开启“使用电池时关闭 HDR”：拔电后卡片按实际 SDR 取消选中并允许色域切换；接电后实际 HDR 恢复则再次阻止色域写入，失焦/最小化仍更新。电池下点击 HDR 只修改启用偏好，不覆盖电池策略、不因实际仍 SDR 而误报偏好设置失败。
- [ ] 验证麦克风/扬声器各模式、耳机插拔后能力变化；设备切换只刷新、不覆盖服务 preferred mode。
- [ ] 内置麦克风选择多人通话后插入耳机：多人通话及人声定位隐藏，仅保留关闭降噪、单人通话；拔出后重新显示内置选项，不自动写入模式。插拔后点击旧按钮也需在服务层重新检测并拒绝不支持的 shared 请求。
- [ ] 验证 20 秒声纹注册、取消、录制途中设备变化、重新注册及删除；private 下读取不误报声纹丢失。
- [ ] 缺少相关驱动/服务的机器隐藏相应功能，恢复服务后切回窗口重新检测；验证获得焦点仅触发一次刷新、不叠加批次。

这些验收包含实际硬件写入、录音及桌面交互；自动检查没有代为执行。
