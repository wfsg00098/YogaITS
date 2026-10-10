# YogaITS 集成版

面向支持 Lenovo 驱动与服务的 Windows x64 笔记本的控制中心。基于 WinUI 3，四个最终 probe 的后端已整合为独立服务。窗口共用原有按钮、圆角、系统主题色边框与 Acrylic 背景。

## 使用

- **右键托盘图标**：原有快捷菜单，保留性能、充电、独显休眠和退出，移除杂项。
- **左键托盘图标**：完整控制中心，包含原有功能、杂项及四组新增功能。再次点击激活已有窗口。
- 完整窗口采用纵向单列：性能、充电、独显休眠、新增功能、杂项。可调整大小，内容过多时可滚动；关闭窗口后应用继续驻留托盘。
- 无法确认能力的功能自动隐藏；受临时条件限制的选项禁用并提示原因。窗口每次获得焦点时刷新一次，设置完成及相关系统事件也会更新状态，无手动刷新按钮。

原版与集成版共用单实例标识、开机启动任务名称。测试集成版前，请先退出原版托盘应用。

边框强制显示系统强调色，不受 Windows 对应显示开关影响，系统强调色变化后自动更新。标题栏及其按钮保持系统默认行为，不单独设置颜色，不修改系统全局设置。

## 已集成功能

| 功能 | 行为 |
| --- | --- |
| 显卡工作模式 | 集显、智能、双显；电源/显示事件驱动智能策略；外接独显显示器保护；最多五次临时重试 |
| 屏幕色域 | HDR / ACM 开关；按有效工厂 ICC 显示模式；在线恢复；可选 ICC 建议亮度；STANDARD/WCS 回读与 DWM 刷新 |
| 唤醒和睡眠 | 自动唤醒、自动睡眠、离开后的快/中/慢、人员辅助判定 |
| 播放和暂停 | SmartSense 人走暂停、人回播放；实际播放器兼容性由服务决定 |
| 音频降噪 | 麦克风关闭/单人/多人、人声定位、仅听机主、扬声器降噪 |
| 声纹 | 状态读取、删除确认、48 kHz/20 秒注册、取消录制、设备变化取消、完整注册验证 |

不依赖联想电脑管家、SmartEngine 或厂商 RPC 客户端 DLL。对应硬件驱动、SmartSense 和 Elevoc 服务仍是功能前提。HPD 距离、YogaMode 等不属于最终集成说明要求的范围。

HDR / ACM 使用屏幕色域区域内的选项卡片，按内屏能力显示，选中表示开启，再次点击关闭。HDR 开启时不能开启 ACM；只有 HDR、ACM 均关闭且系统回到 SDR 后才能切换工厂色域。关闭高级颜色不会自动应用旧 ICC。

## 构建与启动

要求 Windows x64、.NET 10 SDK 和可还原 NuGet 的网络。运行需 .NET 10 Desktop Runtime、与项目引用兼容的 Windows App Runtime，以及管理员权限。

在此目录打开 PowerShell：

```powershell
.\build-clean.ps1
.\dist\YogaITS.Tray.exe
```

脚本清理本项目的 bin/、obj/ 和 dist/，发布 framework-dependent Release，并验证 PRI 资源。保留 dist/ 的全部文件，不单独复制 EXE。

开发编译：

```powershell
dotnet build .\YogaITS.Tray\YogaITS.Tray.csproj -c Debug
```

## 验证

```powershell
dotnet run --project .\Tests\YogaITS.PolicyChecks -c Release
dotnet run --project .\Tests\YogaITS.ReadOnlyProbe -c Release
```

第一项为无硬件操作的逻辑检查；第二项只读取四组功能的能力与状态，不切模式、不录音、不下载。GPU 读取可能需要管理员权限。

可选网络诊断：为 ReadOnlyProbe 添加 `-- --check-calibration-http`，会发送本机序列号至 Lenovo 元数据接口，对比三种 Accept 请求头；不下载 ZIP，也不修改缓存。仅在同意发送序列号时运行。

能力规则、检查记录与实机验收清单见 [INTEGRATION.md](INTEGRATION.md)。编译及只读检测通过不代表所有写入操作或窗口交互已完成实机验收。

## 代码与配置

- YogaITS.Tray/Services/：原有接口、缓存、GPU 策略及系统事件。
- Services/Hpd/、Services/Elevoc/、Services/Color/：probe 后端与业务服务。
- UI/TrayMenuWindow.cs：共用的快捷菜单/完整窗口及原有功能。
- UI/FeatureCard.cs、UI/FeaturePanels.cs：卡片交互与新增功能界面。
- %LOCALAPPDATA%\YogaITS\settings.json：显卡逻辑模式与色域亮度偏好。
- %LOCALAPPDATA%\YogaITS\ColorCalibration\Factory\：验证后的工厂 ICC，只作为源文件。
- 注册结束、失败或取消后尝试清理临时录音。不保存声纹向量或完整序列号到常规日志。

