# 集成规则与验收记录

## 范围与结构

独立目录从 YogaITS-main 基线开始，用 Git 分阶段管理。四组功能按各自最终集成说明实现；沿用管理员进程，不新增常驻 helper 或厂商客户端 DLL 依赖。

业务服务继承 SerializedFeatureService，同一功能的读、写、关闭串行执行；不同功能的首次检测并行执行。所有写入由用户操作或已选择的 GPU 智能策略触发。正常运行无周期轮询。

右键保留原有快捷菜单并移除杂项。左键使用共用的原有控制组件和新增功能卡片构建完整窗口；不因失去焦点关闭，支持滚动、缩放及窄窗口单列。两种窗口共用缓存和设置回调。

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

## 待实机验收

- [ ] 退出原版后启动 dist/YogaITS.Tray.exe，确认左右键行为、大窗口布局、主题、不同 DPI、窄窗口及关闭后驻留。
- [ ] 大窗口与右键菜单同时打开，确认原有设置状态同步；Explorer 重启后托盘恢复。
- [ ] 管理员运行验证 GPU 双显/集显互切、AC/DC 智能切换、外接独显屏保护、拔屏重评估和重启后 Auto 恢复。
- [ ] 验证 HPD 各开关和快/中/慢回读；缺少 SmartSense 服务时隐藏。
- [ ] 在色域卡片切换 HDR/ACM，确认回读状态与禁用原因；关闭后反复切换色域；验证亮度开关、内屏拓扑变化、在线恢复成功与身份不匹配阻断。
- [ ] 验证麦克风/扬声器各模式、耳机插拔后能力变化；设备切换只刷新、不覆盖服务 preferred mode。
- [ ] 验证 20 秒声纹注册、取消、录制途中设备变化、重新注册及删除；private 下读取不误报声纹丢失。
- [ ] 缺少相关驱动/服务的机器隐藏相应功能，恢复服务后手动刷新重新检测。

这些验收包含实际硬件写入、录音及桌面交互；自动检查没有代为执行。
