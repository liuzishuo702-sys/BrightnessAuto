# 环境光自动亮度 — Bug 清单

> 代码基线：`Project1/BrightnessAuto`，`Package.appxmanifest` 版本 `3.1.23.0`
> 审查范围：`Services/AutoBrightnessEngine.cs`、`Services/BrightnessController.cs`、`Services/CameraLuma.cs`、`Services/EngineParams.cs`、`Controls/TrayIcon.cs`、`App.xaml.cs`、`MainWindow.xaml.cs`、`BrightnessAuto.csproj`、`Package.appxmanifest`
> 审查方式：静态走查（未运行程序验证）
> 修复进度：见下方「修复进度」表

---

## 修复进度

### 第二轮（本次）

| 编号 | 状态 | 改动摘要 |
|---|---|---|
| **21** | ✅ 已修 | 引擎新增**同步** `Dispose()`（取消 + 超时等待 + 强制释放）；工作循环提前到 `StartAsync` 启动并等 `_startTrigger`；`Closed` 改调 `Dispose()` |
| **22** | ✅ 已修 | 引擎新增 `StartFailed` 事件；开机自动启动失败时界面复位，不再卡在"停止" |
| **23** | ✅ 已修 | 隐藏到托盘时锁屏提醒只写日志，不再尝试弹看不见的对话框 |
| 20 后续 | ✅ 已加 | 「测光摄像头」下拉选择器（见第 20 条末） |

验证方式：**完整 `dotnet build` 通过（0 error）** —— 清空 `obj/` 后权限问题消失，
不再需要离线 `csc` 绕行；XAML 编译器（含新控件 `CameraPickerCombo`）也验证通过。
已打包 **3.1.27.0** 并安装。

### 第一轮

| 编号 | 状态 | 改动摘要 |
|---|---|---|
| 1 | ✅ 已修 | `_brightnessController` 改为惰性创建，并在 `Closed`/停止/启动引擎时 `Dispose()`（释放系统亮度覆盖）<br>⚠️ 本轮发现只堵住一半，见第 21 条 |
| 2 | ✅ 已修 | 删除 3 处 `LightBulb` 强杀（`App.xaml.cs` 的启动逻辑保留） |
| 3 | ✅ 已修 | 人脸微调改为「连续 3 次同方向 + 每步 3% + 累计上限 ±15%」 |
| 5 | ✅ 已修 | 亮度映射改用 EMA 后的 `smoothLuma`；强光锁定解除仍看原始采样 |
| 6 | ✅ 已修 | 人脸检测返回 `null` 时立即作废 `noFaceConfirmed` / `lockWarningSent` |
| 7 | ✅ 已修 | `GetIdleSeconds()` 改用 `GetTickCount64` 补位，处理 32 位回绕 |
| 8 | ✅ 已改 | 预览帧改为「下一帧替换时释放旧帧」，失败时留 `Debug.WriteLine` |
| 9 | ✅ 已修 | 源组初始化拆成「独占→共享」整体重试，`_hasExclusiveControl` 只在成功后赋值 |
| 10 / 15 | ✅ 已修 | `ActivateStartStop` 用 `try/finally` 恢复按钮；`OnEngineStopped` 统一走 `SetRunningVisual(false)` |
| 11 | ✅ 已修 | 托盘图标按 alpha 生成 AND 掩码（`SetBitmapBits`），透明区不再发黑 |
| 12 | ✅ 已修 | 共享图标显式标记 `_ownsIcon = false`，不再靠句柄值判断 |
| 13 | ✅ 已修 | `Closed` 里解绑 `AppWindow.Closing`，杜绝关闭流程重入 |
| 16 | ✅ 已修 | 引擎收尾先摘掉 `_camera.Diag` 再 `DisposeAsync` |
| 17 | ✅ 已修 | 删掉 `lastSeenPerson` / `lastLuma` / `lightSurging` 死变量 |
| 18 | ✅ 已修 | `IdleLockSeconds` 纳入读写设置，默认 120→60（与界面「提前 10 秒提醒」一致） |
| 19 | ✅ 已修 | 久坐分钟数改为 `EngineParams.SedentaryReminderMinutes`，从设置读写 |
| 4 | ⬜ 待决策 | 环境光正反馈需改采样区域，必须实机调参 |
| 14 | ⬜ 部分 | 已用「引擎启动即释放手动覆盖」缓解；彻底方案需全局单实例（待定） |

---

## 严重度总览

| 编号 | 问题 | 严重度 | 影响面 | 修复量 |
|---|---|---|---|---|
| 1 | `BrightnessOverride` 从不释放，退出后可能锁死系统亮度 | 🔴 高 | 功能 / 系统 | 小 |
| 2 | 停止引擎 / 退出程序会 Kill 掉 LightBulb | 🔴 高 | 误伤他人软件 | 小 |
| 3 | 人脸位置调亮度：每 3 轮 ±5%，无死区无上限累计 | 🔴 高 | 核心功能 | 中 |
| 4 | 环境光正反馈：全画面平均亮度（脸自己就是光源） | 🔴 高 | 核心功能 | 中 |
| 5 | `smoothLuma` / EMA 算完不用，平滑滑块是假控件 | 🔴 高 | 参数失效 | 小 |
| 6 | `noFaceConfirmed` 只会被置 true，不会因取帧失败复位 | 🟠 中 | 自动锁屏 | 小 |
| 7 | `GetIdleSeconds()` 的 `TickCount` 回绕 | 🟠 中 | 自动锁屏 | 小 |
| 8 | 摄像头预览 `SetBitmapAsync` 后立刻 `Dispose` 源位图 | 🟠 中 | 预览画面 | 小 |
| 9 | `_hasExclusiveControl` 在回退路径上状态错误 | 🟠 中 | 摄像头打开 | 小 |
| 10 | 引擎启动失败时"开始"按钮有一段假死 | 🟠 中 | 交互 | 小 |
| 11 | 托盘图标 alpha 不生效（可能发黑/整块不透明） | 🟠 中 | 观感 | 小 |
| 12 | `LoadIcon` 共享句柄被当成自有图标 | 🟡 低 | GDI 泄漏 | 小 |
| 13 | 构造期/关闭期的 `_allowWindowClose` 读写竞争 | 🟡 低 | 关闭流程 | 小 |
| 14 | 两个 `BrightnessOverride` 实例互相顶替 | 🟡 低 | 手动亮度 | 中 |
| 15 | 引擎失败提前 return，`StartStopButton` 未恢复 | 🟡 低 | 交互 | 小 |
| 16 | `_camera.Diag` 回调在窗口关闭后仍 `TryEnqueue` | 🟡 低 | 稳定性 | 小 |
| 17 | 死变量 `lastSeenPerson` / 冗余 `faceY` 读取 | ⚪ 清理 | — | 小 |
| 18 | `IdleLockSeconds` 既无 UI 也不读设置，硬编码 120 | ⚪ 清理 | 可配置性 | 小 |
| 19 | 久坐提醒 45 分钟硬编码 | ⚪ 清理 | 可配置性 | 小 |
| **20** | **摄像头选源取"第一个可用"，选中手机虚拟摄像头** | **🔴 高** | **核心功能** | **小** |
| **21** | **退出时 `StopAsync()` 未 await，覆盖/摄像头不释放** | **🔴 高** | **系统亮度被锁死** | **小** |
| **22** | **开机自动启动失败时按钮卡在"停止"** | **🟠 中** | **交互** | **小** |
| **23** | **隐藏到托盘时锁屏提醒弹窗可能抛异常** | **🟡 低** | **稳定性** | **小** |

---

## 🔴 高优先级

### 21. 退出时 `StopAsync()` 未 await，亮度覆盖与摄像头不释放（第二轮发现）

**位置**：[MainWindow.xaml.cs:150](<Project1/BrightnessAuto/MainWindow.xaml.cs#L150>)（修复前）

```csharp
Closed += (_, _) =>
{
    ...
    _ = _engine.StopAsync();      // ← 没 await: fire-and-forget
    ...
};
```

**问题**：`Closed` 是同步处理器，`_ = _engine.StopAsync()` 只是**发起**了停止。进程可能在引擎 `RunAsync` 的 `finally` 跑到之前就退出：

```csharp
finally
{
    brightness?.Dispose();   // ← 这里才 StopOverride(), 可能永远没执行
    ...
}
```

**后果**（正是第 1 条想解决但没堵住的那半边）：
- `BrightnessOverride.StopOverride()` 不执行 → **系统亮度被锁在某固定值，任务栏/设置里的亮度滑块失效，直到重启**；
- 摄像头不被释放 → 其他软件打不开摄像头。

**为什么第 1 条没堵住**：第 1 条修的是**界面自己**那个手动 controller；引擎内部的 controller 只归 `RunAsync.finally` 管，而那个 finally 依赖 StopAsync 被真正等待。

**修复**（已改）：
- 引擎新增 **同步** `Dispose()`：取消 + 等待收尾（任务 3s / 摄像头 1s 超时）后**无条件强制释放**；
- 工作循环改为在 `StartAsync` 里就启动、先 await 一个 `_startTrigger`，这样即使从未开工也能被取消收掉（否则会挂在触发等待上，超时也退不出去）；
- `Closed` 里改调 `_engine.Dispose()`。

---

### 20. 摄像头选源取"第一个可用"，选中了手机虚拟摄像头（实机发现）

**位置**：[CameraLuma.cs:58-72](<Project1/BrightnessAuto/Services/CameraLuma.cs#L58-L72>)（修复前）

**实机证据**（用户 2026-10-17 截图，日志原文）：

```
可用视频源组: 2
选用: OnePlus 13 (Windows 虚拟摄像头)
MediaCapture 独占模式就绪
格式: NV12 1280x720
帧读取器已启动
摄像头就绪, 开始自动调节.
```

**问题**：原逻辑是遍历 `MediaFrameSourceGroup.FindAllAsync()`，**取第一个有彩色源的组**。系统里同时存在内置摄像头和"OnePlus 13 虚拟摄像头"（手机当网络摄像头）时，取到哪个完全看枚举顺序，实测取到了手机。

**后果**：
- 测量对象变成"手机摄像头正对着的东西"——手机放兜里/朝墙/朝天花板，亮度就完全乱套；
- 手机虚拟摄像头走网络串流，持续占用手机电量和带宽；
- 用户看到的"自动亮度不准"里，这一条比算法问题更直接。

**修复**（已改）：新增 `CameraPriority` / `DevicePriority` 排序，优先级
`0 内置前摄 > 1 内置 > 2 普通/外接 > 3 名字含"虚拟/virtual"`，
并把所有候选连同优先级打印到日志，方便排查。
源组路径（`OpenAsync`）和设备路径（`OpenDefaultCaptureAsync`）使用同一套优先级。

**教训**：凡是"枚举设备取第一个"的地方都要显式排序 —— 用户的机器上装了什么虚拟设备是不可控的。

**后续（用户要求）**：已加「测光摄像头」下拉选择器（摄像头页），
列出所有可用摄像头（标注内置/外接·虚拟），默认「自动（优先内置摄像头）」，
也可手动指定手机虚拟摄像头。选择持久化到 `EngineParams.CameraDeviceId`，
`CameraLuma.PreferredId` 匹配到时无条件置顶（即使它是虚拟设备），
未匹配到则回退自动策略并打警告日志。切换需重新点「开始」生效。

---

## 🟠 中优先级

### 22. 开机自动启动失败时，按钮卡在"停止"（第二轮发现）

**位置**：[MainWindow.xaml.cs:982](<Project1/BrightnessAuto/MainWindow.xaml.cs#L982>) + `ActivateStartStop`（修复前）

**问题**：`ApplyLaunchPreferences()` 里 `if (_autoAdjustOnLaunch) ActivateStartStop();` —— 这条路径与普通点击不同：它**没有配对的 `OnEngineStopped` 数据流**。引擎若在后台循环里提前 `return`（WMI 连不上 / 摄像头打不开），界面就永远显示"停止"：

- 用户点了也没反应（`IsRunning` 为 false，实际会去启动，但视觉状态是错的）；
- 状态点还亮着绿色"运行中"。

**修复**（已改）：
- 引擎新增 `StartFailed` 事件，在"连不上亮度接口""摄像头打不开"两处提前返回前触发；
- `MainWindow` 订阅该事件，复位 `SetRunningVisual(false)` 并恢复按钮；
- 配合第 21 条把工作循环提前到 `StartAsync` 里启动，避免"挂在启动触发上收不回来"。

---

## 🟡 低优先级

### 23. 隐藏到托盘时，锁屏提醒弹窗可能抛异常（第二轮发现）

**位置**：[MainWindow.xaml.cs:1911](<Project1/BrightnessAuto/MainWindow.xaml.cs#L1911>)（修复前）

**问题**：久坐提醒检查了 `_windowHiddenInTray`（藏托盘时只写日志），**锁屏倒计时却没有**。窗口隐藏后 `ContentDialog.ShowAsync()` 可能因为没有可用 `XamlRoot` 而抛异常；即使不抛，用户也看不到，只会在 10 秒后莫名锁屏。

**修复**（已改）：与该路径保持一致 —— 藏托盘时只写日志「无人操作, 约 N 秒后自动锁屏」，到点照常锁屏。

---

## 🔴 高优先级（第一轮）

### 1. `BrightnessOverride` 从不释放，退出程序后可能锁死系统亮度

**位置**：[MainWindow.xaml.cs:38](<Project1/BrightnessAuto/MainWindow.xaml.cs:38>)、[:112](<Project1/BrightnessAuto/MainWindow.xaml.cs#L112>)、[BrightnessController.cs:36-48](<Project1/BrightnessAuto/Services/BrightnessController.cs#L36-L48>)

```csharp
// MainWindow.xaml.cs
private readonly BrightnessController _brightnessController;   // L38
_brightnessController = new BrightnessController();            // L112

// BrightnessController.Connect()
if (laptop.IsInvalid)
{
    var systemBrightness = BrightnessOverride.GetDefaultForSystem();
    if (systemBrightness.IsSupported)
        _systemBrightness = systemBrightness;                  // L40-42
}
```

**问题**：`Connect()` 内部会拿到一个"系统级亮度覆盖"对象，而 `_brightnessController` 在 `Closed` 事件里**从未 `Dispose()`**（只停了引擎、关了托盘）。只有引擎内部那个 controller 会在 `finally` 里 `StopOverride()`。

**后果**：引擎一停，应用级 override 重新生效，系统亮度被锁在某值上，任务栏/设置里的亮度滑块推不动；退出程序后也可能一直锁着，直到重启或恢复系统亮度设置。

**修复方向**：
- `MainWindow.Closed` 里 `_brightnessController?.Dispose()`；
- 或者更彻底：仅在"手动调亮度"时惰性创建 controller，用完即 `Dispose()`，不要常驻。

---

### 2. 停止引擎 / 退出程序会 Kill 掉 LightBulb

**位置**：[MainWindow.xaml.cs:149-155](<Project1/BrightnessAuto/MainWindow.xaml.cs#L149-L155>)、[:1661-1667](<Project1/BrightnessAuto/MainWindow.xaml.cs#L1661-L1667>)、[App.xaml.cs:26-35](<Project1/BrightnessAuto/App.xaml.cs#L26-L35>)

```csharp
// L1661-1667：点"停止"时
foreach (var p in Process.GetProcessesByName("LightBulb"))
    p.Kill();
```

**问题**：LightBulb 是独立的第三方色温软件，跟"停止自动调亮度"没有任何因果关系。三处 Kill 全部无条件执行，用户没启动过它也会被杀，而且是强杀（无保存机会）。

**修复方向**：删掉这三处 Kill。若确实想让"本程序启动的" LightBulb 随程序退出，需要先记录自己启动的 PID，且只 Kill 自己拉起来的那个。

---

### 3. 人脸位置调亮度：每 3 轮 ±5%，无死区、无上限累计

**位置**：[AutoBrightnessEngine.cs:289-301](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L289-L301>)

```csharp
if (faceY.HasValue && !isDimming)
{
    int yAdjust = faceY.Value < 0.3f ? 5 : (faceY.Value > 0.7f ? -5 : 0);
    if (yAdjust != 0)
    {
        int newB = Math.Clamp(applied + yAdjust, minB, maxB);
        if (Math.Abs(newB - applied) >= deadband)
        {
            brightness.SetBrightness(newB);
            applied = newB;
        }
    }
}
```

**问题**：
- `faceY` 只是**当前帧**的位置，没有稳定判定（旁边主调节路径要求连续 3 次稳定，这里没有）；
- 没有累计上限：只要你的头稍微偏上（`faceY < 0.3`），每 1 个检测周期就 +5%，一路爬到 `MaxBrightness`（100%）；偏下就一路跌到 `MinBrightness`；
- 默认 `Deadband = 3`，而 `yAdjust = 5` 永远大于死区，等于这个"死区"在这里完全不起作用。

**后果**：坐姿稍偏就把亮度顶到极限，用户看到的是"亮度自己发疯"。

**修复方向**：
- 加稳定判定（同一方向连续 N 次）+ 总偏移量上限（例如 ±15%）；
- 与主亮度映射合并成一次决策，而不是在主路径之外再叠加一层；
- 或者干脆先做成开关，默认关闭。

---

### 4. 环境光正反馈：测的是整机画面平均亮度

**位置**：[CameraLuma.cs:424-465](<Project1/BrightnessAuto/Services/CameraLuma.cs#L424-L465>)、[AutoBrightnessEngine.cs:207-219](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L207-L219>)

```csharp
private static double AverageGray(SoftwareBitmap gray)
{
    // 对整幅画面按步长采样求平均
    for (int y = 0; y < height; y += step)
        for (int x = 0; x < width; x += step)
            sum += pixels[row + x];
    return (double)sum / count;
}
```

**问题**：平均值把**人脸和身体**一起算进去了，而脸是被屏幕照亮的。于是形成正反馈回路：

```
屏幕变亮 → 脸被照亮 → 摄像头平均亮度↑ → 目标亮度↑ → 屏幕更亮 → ...
```

**后果**：
- 夜里环境暗、只有屏幕照明时，亮度会自己爬到 `MaxBrightness`；
- 环境很亮时也很难降下来；
- "自动调节"表现得像"只会往上加"。

**修复方向**：
- 只取画面外围/边缘区域（避开中央人脸），或把人脸框区域排除后再平均；
- 用画面下 1/3 或左右两侧条带做"环境段"采样；
- 给映射加迟滞（上升和下降用不同阈值），打断正反馈。

---

### 5. `smoothLuma` / EMA 算完不用，平滑滑块是假控件

**位置**：[AutoBrightnessEngine.cs:214-219](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L214-L219>)

```csharp
smoothLuma = smoothLuma < 0 ? luma : ema * luma + (1.0 - ema) * smoothLuma;   // L214 算 EMA
int snapForSample = brightSnap > 0 && luma < brightSnap ? 0 : brightSnap;     // 用的是 luma
int target = MapLumaToBrightness(luma, minB, maxB, snapForSample);            // L219 却用原始 luma
```

**问题**：EMA 平滑值只用于界面显示，实际映射目标是**原始采样值**。注释说"控制使用当前采样，避免 EMA 滞留"，但结果是 `SmoothPct`（平滑系数）滑块对输出几乎无影响。

**后果**：用户调"平滑"没反应，是个假控件。要么让它参与控制，要么在 UI 上说明清楚。

**修复方向**：把 `MapLumaToBrightness` 的输入改成 `smoothLuma`（保留 `brightSnap` 用原始 `luma` 判断的逻辑），这样平滑滑块才真正生效。

---

## 🟠 中优先级

### 6. `noFaceConfirmed` 只会被置 true，不会因取帧失败复位

**位置**：[AutoBrightnessEngine.cs:314-330](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L314-L330>)、[:354-358](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L354-L358>)

```csharp
else if (faceResult != null && !hasFace)
{
    noFaceConfirmed = true;      // L316：置 true 之后，只有"检测到人脸"才会清
    ...
}
```

**问题**：如果某几轮 `DetectFaceAsync` 返回 `null`（摄像头取帧失败、检测异常），`noFaceConfirmed` 会保持之前的值。锁屏判定会基于**过期状态**推理，可能对着一个已经回来的人倒计时。

**修复方向**：`faceResult == null` 时同时把 `noFaceConfirmed = false` 并重置 `lastSeenFace`（保守策略：检测不可用时不锁屏）。

---

### 7. `GetIdleSeconds()` 的 `TickCount` 回绕

**位置**：[AutoBrightnessEngine.cs:34-39](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L34-L39>)

```csharp
return (uint)(Environment.TickCount - lii.dwTime) / 1000;
```

**问题**：`Environment.TickCount` 是 32 位有符号、约 24.9 天回绕。`LASTINPUTINFO.dwTime` 是 32 位无符号 tick。连续运行超过 24.9 天后这个差值会算出巨大的值，导致一次莫名其妙的自动锁屏。

**修复方向**：改用 `GetTickCount64` 并给 `dwTime` 补高位（用 `(now64 & ~0xFFFFFFFF) | dwTime`，并处理跨 49.7 天边界），或直接用 `GetLastInputInfo` 配合 `GetTickCount64` 推导。

---

### 8. 摄像头预览：`SetBitmapAsync` 后立刻 `Dispose` 源位图

**位置**：[MainWindow.xaml.cs:1245-1257](<Project1/BrightnessAuto/MainWindow.xaml.cs#L1245-L1257>)

```csharp
var frame = _engine.GetPreviewFrame();
if (frame != null && _cameraPreviewSource != null)
{
    try { await _cameraPreviewSource.SetBitmapAsync(frame); }
    catch { }
    finally { frame.Dispose(); }        // L1255
}
```

**问题**：`SetBitmapAsync` 语义是"复制位图到 source 内部格式"，理论上调用方可以在 await 之后释放；但这里 `frame` 同时也被 `Image` 的渲染管线引用，且 300ms 一次的频率下这属于**未经实测验证的临界用法**。实践中更容易踩到的是 `E_ILLEGAL_METHOD_CALL`（格式不被接受）。同时 `catch { }` 把失败静默吞掉，用户只看到预览不动。

**修复方向**：把帧的生命周期交给 source（下一帧替换时再释放旧帧），失败时至少写一次日志而不是静默吞。这条建议**运行一次再定级**：如果预览正常，降级为"代码整洁性"问题；如果预览闪黑/不动，就是它。

---

### 9. `_hasExclusiveControl` 在回退路径上状态错误

**位置**：[CameraLuma.cs:85-97](<Project1/BrightnessAuto/Services/CameraLuma.cs#L85-L97>)

```csharp
if (!await InitCaptureAsync(chosenGroup, exclusive: true)
    && !await InitCaptureAsync(chosenGroup, exclusive: false))
{
    if (!await OpenDefaultCaptureAsync())     // 回退路径
        return false;
}
else
{
    _source = _capture!.FrameSources[chosenInfo.Id];
    if (!await StartReaderAsync(_source))
        return false;                          // 独占已成功、只是读取器失败 → 不重试共享模式
}
```

**问题**：
- 独占模式初始化成功、但 `StartReaderAsync` 失败时，不会再尝试共享只读模式；
- 回退到 `OpenDefaultCaptureAsync()` 后 `_hasExclusiveControl` 的取值依赖 `DisposeCaptureOnlyAsync()` 的副作用，容易被后续 `StartReaderAsync` 里的 `SetFormatAsync` 判断误用。

**修复方向**：把"初始化 + 启动读取器"作为一个整体重试单元，两种共享模式各试一次；`_hasExclusiveControl` 只在真正 `InitializeAsync` 成功的那条分支里赋值。

---

### 10. 引擎启动失败时"开始"按钮有一段假死

**位置**：[MainWindow.xaml.cs:1669-1675](<Project1/BrightnessAuto/MainWindow.xaml.cs#L1669-L1675>)

```csharp
StartStopButton.IsEnabled = false;
if (await _engine.StartAsync())
    SetRunningVisual(true);
StartStopButton.IsEnabled = true;      // 只有 StartAsync 返回后才恢复
```

**问题**：`StartAsync()` 只负责把循环任务跑起来、立即返回 true；真正失败（WMI 连不上、摄像头打不开）是在后台循环里 `return`。这段时间里按钮一直是禁用的，用户点不动。虽然 `OnEngineStopped` 之后会把按钮恢复，但中间有几秒窗口期（等摄像头超时）。

**修复方向**：用 `try/finally` 保证按钮恢复；或者让 `StartAsync` 等到"相机/亮度都就绪"再返回，把失败反馈提前。

---

### 11. 托盘图标 alpha 不生效（可能发黑/整块不透明）

**位置**：[TrayIcon.cs:99-116](<Project1/BrightnessAuto/Controls/TrayIcon.cs#L99-L116>)

```csharp
nint colorBitmap = CreateBitmap(size, size, 1, 32, pixels);
nint maskBitmap = CreateBitmap(size, size, 1, 1, null);   // 全 0 掩码
...
return CreateIconIndirect(ref info);
```

**问题**：用 GDI `CreateBitmap` 造的 32bpp DIB 不是"带 alpha 的 DIB 段"，`CreateIconIndirect` 在这个组合下不会按预期应用 per-pixel alpha。经典做法是用 `CreateDIBSection(BI_RGB, 32bpp)` 再交给 `CreateIconIndirect`。

**后果**：深色任务栏上图标可能整块发黑、或边缘出现黑边。

**修复方向**：换成 `CreateDIBSection` 创建颜色位图；掩码位图单独用 1bpp 全 0。

---

## 🟡 低优先级

### 12. `LoadIcon` 共享句柄被当成自有图标

**位置**：[TrayIcon.cs:56-59](<Project1/BrightnessAuto/Controls/TrayIcon.cs#L56-L59>)、[:221-225](<Project1/BrightnessAuto/Controls/TrayIcon.cs#L221-L225>)

```csharp
_icon = CreateBrightnessIcon();
_ownsIcon = _icon != 0 && _icon != 1;
if (!_ownsIcon)
    _icon = LoadIcon(0, (nint)32512);   // IDI_APPLICATION — 系统共享图标
```

**问题**：`LoadIcon` 返回的是系统共享句柄，按 MSDN 不应 `DestroyIcon`。当前 `_ownsIcon` 在 `_icon==1` 时为 false 恰好躲过，但这是"靠返回值巧合"而不是靠正确判断。

**修复方向**：显式用 `LoadIconWithScaleDown`/`LoadImage` 或直接标记 `_ownsIcon = false` 的分支，不依赖句柄值判断。

---

### 13. 构造期/关闭期的 `_allowWindowClose` 读写竞争

**位置**：[MainWindow.xaml.cs:143-158](<Project1/BrightnessAuto/MainWindow.xaml.cs#L143-L158>)、[:217-273](<Project1/BrightnessAuto/MainWindow.xaml.cs#L217-L273>)

**问题**：`OnAppWindowClosing` 是 `async void`，先设 `args.Cancel = true` 再 `await` 对话框；期间 `_closePromptShowing` 做重入保护，但 `_allowWindowClose` 的读写跨 await，且 `Closed` 里的清理（停引擎、Dispose 托盘）与正在等待的关闭对话框可能交错。

**修复方向**：把关闭流程抽成一个状态机（`Idle / Prompting / Closing / Hidden`），所有分支只改状态，不在 await 前后裸读写布尔。

---

### 14. 两个 `BrightnessOverride` 实例互相顶替

**位置**：[BrightnessController.cs:36-48](<Project1/BrightnessAuto/Services/BrightnessController.cs#L36-L48>)

**问题**：应用级 controller（手动亮度滑块用）和引擎内部 controller 都会 `GetDefaultForSystem()`。系统亮度覆盖全局通常只有一个生效，两者会互相顶替——引擎停止后应用级那个接管，反过来也一样。

**修复方向**：全局只保留一个 controller 实例，由 MainWindow 持有并注入给引擎；引擎停止时只"暂停覆盖"而不销毁。

---

### 15. 引擎失败提前 return，`StartStopButton` 未恢复

见第 10 条，两者是同一处代码的两种表现（一个是超时窗口期，一个是彻底失败时的状态残留）。

---

### 16. `_camera.Diag` 回调在窗口关闭后仍 `TryEnqueue`

**位置**：[AutoBrightnessEngine.cs:151](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L151>)、[MainWindow.xaml.cs:1705](<Project1/BrightnessAuto/MainWindow.xaml.cs#L1705>)

**问题**：`Closed` 里 `_ = _engine.StopAsync()` 是 fire-and-forget，引擎 finally 阶段仍会触发 `Log` → `DispatcherQueue.TryEnqueue`。`TryEnqueue` 对已关闭队列返回 false，不会崩，但日志会丢、顺序会乱。

**修复方向**：`Closed` 里改为 `await`（可放到 `async void` 处理器），或在事件订阅处统一解绑。

---

### 17. 死变量 / 冗余读取

**位置**：[AutoBrightnessEngine.cs:174](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L174>)（`lastSeenPerson` 只写不读）、[:265](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L265>)（`faceY` 取出后仅一处使用）、[CameraLuma.cs:340-368](<Project1/BrightnessAuto/Services/CameraLuma.cs#L340-L368>)（`bitmap` / `gray` 的释放路径靠 `ReferenceEquals` 兜底，异常路径下 `bitmap` 未保证释放）。

---

### 18. `IdleLockSeconds` 既无 UI 也不读设置

**位置**：[EngineParams.cs:94](<Project1/BrightnessAuto/Services/EngineParams.cs#L94>)

```csharp
public int IdleLockSeconds { get; set; } = 120;
```

界面里只有 `IdleLockToggle`（开关），没有秒数输入；`LoadPreferences()` 也没读这个键。用户改不了，代码里却按"可配置"设计。

---

### 19. 久坐提醒 45 分钟硬编码

**位置**：[AutoBrightnessEngine.cs:63](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L63>)、[:306](<Project1/BrightnessAuto/Services/AutoBrightnessEngine.cs#L306>)

`SedentaryReminderMinutes = 45` 写死。要么放进设置，要么在 UI 里说明默认值。

---

## 附：顺手建议

- **测试包堆积**：`Project1/BrightnessAuto/AppPackages/` 下有 `3.1.19.0` ~ `3.1.23.0` 五个 Test 包，每个都带 4 份 `Microsoft.WindowsAppRuntime.1.7.msix`（约 91 MB/包），累计十几 GB，建议只留最新一个。
- **`startupTask Enabled="false"`**：清单里是禁用，代码走 `RequestEnableAsync` 动态开启，语义是对的——注意首次安装不会自启是预期行为，别当成 bug 排查。
- **`App.xaml.cs` 自动拉起 LightBulb**：路径 `C:\Program Files (x86)\LightBulb\LightBulb.exe` 硬编码，且是在 `OnLaunched` 里同步 `Process.Start`。这一段和第 2 条的 Kill 是一对，建议整体重新设计成"可选联动"。

---

## 建议修复顺序

1. **第 2 条**（删 Kill）— 一行删除级改动，风险最低，先止血；
2. **第 1 条**（Dispose override）— 影响系统行为，必须修；
3. **第 5 条**（平滑滑块失效）— 改动一行，立刻让参数生效；
4. **第 3、4 条**（人脸偏移 + 正反馈）— 核心算法，需要改完实测调参；
5. **第 6、7、8、9 条**— 稳定性问题，逐个补；
6. 其余低优先级与清理项，可以合并到一次版本里。
