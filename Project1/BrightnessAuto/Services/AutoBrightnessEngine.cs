using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.System.Power;

namespace BrightnessAuto.Services;

/// <summary>引擎状态更新: 平滑后的环境光(0-255) 与 已应用亮度(0-100).</summary>
public readonly record struct StatusUpdate(int Luma, int Brightness);

/// <summary>
/// 自动亮度调节引擎. 对应 C++ 版本的 WorkerProc 工作线程:
/// 循环读取环境光 -> EMA 平滑 -> 映射为目标亮度 -> 死区判断 -> 设置亮度.
/// 用 async Task + CancellationToken 替代 CreateThread/volatile g_running.
/// 通过 Log / Status 事件回传界面(调用方负责 marshal 到 UI 线程).
/// </summary>
public sealed class AutoBrightnessEngine
{
    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();
    
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
    
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>系统启动至今的毫秒数(含睡眠时间, 与 <c>LASTINPUTINFO.dwTime</c> 同一时间基准).</summary>
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    /// <summary>
    /// 获取距离最后一次鼠标/键盘输入的秒数.
    ///
    /// <c>LASTINPUTINFO.dwTime</c> 来自 <c>GetTickCount</c>(系统启动至今的毫秒数, 32 位),
    /// 因此必须用<b>同一基准</b>的时钟去减: 这里用 <c>GetTickCount64</c>,
    /// 只取低 32 位做无符号减法即可天然处理 49.7 天回绕。
    ///
    /// 踩过的坑(务必别再犯): 曾改用 <c>QueryUnbiasedInterruptTime</c> 想剔除睡眠时间,
    /// 但它返回的是 <b>100 纳秒</b>单位, 与毫秒的 dwTime 直接相减会得到荒谬结果
    /// (实测算出 "4172787 秒无输入" ≈ 48 天), 导致人明明在操作也被判定成无人,
    /// 反复降亮度甚至自动锁屏。
    /// </summary>
    private static uint GetIdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii))
            return 0;

        uint nowLow = (uint)GetTickCount64();          // 毫秒, 低 32 位
        uint elapsedMs = unchecked(nowLow - lii.dwTime); // 无符号减法处理低 32 位回绕
        return elapsedMs / 1000;
    }

    private static bool IsOnBattery()
    {
        try
        {
            // BatteryStatus 与主页显示使用同一套系统状态，
            // 只有真正放电时才切换到电池策略；充电中仍按插电处理.
            return PowerManager.BatteryStatus == BatteryStatus.Discharging;
        }
        catch
        {
            // 无法读取供电状态时使用插电配置，保证自动调节仍可工作.
            return false;
        }
    }

    // 调节算法常量(对应 C++ 版本的硬编码参数).
    private const double GammaExponent = 0.6;       // 伽马曲线, 暗处更敏感
    private const int StableSamplesRequired = 3;    // 目标连续稳定次数才应用
    private const int TargetJitterThreshold = 1;    // 目标变化超过此值重置稳定计数
    private const int MaxSingleStep = 12;           // 单次亮度最大跳变(%)
    private const double DefaultEma = 0.35;         // 平滑系数异常时的回退值
    private const int DefaultBrightness = 50;       // 读取当前亮度失败时的回退值
    private const int FaceOffsetSamplesRequired = 3; // 人脸偏移连续同方向几次才微调
    private const int FaceOffsetStep = 3;            // 每次微调的幅度(%)
    private const int MaxFaceOffset = 15;            // 人脸微调累计上限(%), 防止顶到极限
    private const int BrightenMargin = 4;            // 调亮比调暗多要求的差值(%), 抑制屏幕自照明正反馈.
                                                     // 取值偏小以免压制正常调亮; 配合采样取区域中位数已能压住大部分反馈.

    private readonly EngineParams _params;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CameraLuma? _camera;   // 运行时的摄像头实例, 供预览帧获取

    public event Action<string>? Log;
    public event Action<StatusUpdate>? Status;
    /// <summary>最新一次状态更新，供托盘菜单显示「环境光 | 亮度」。</summary>
    public StatusUpdate LatestStatus { get; set; } = new(-1, -1);
    /// <summary>无人锁屏前的倒计时提醒, 参数为剩余秒数.</summary>
    public event Action<int>? IdleLockWarning;
    /// <summary>久坐提醒, 参数为本次连续计入的分钟数.</summary>
    public event Action<int>? SedentaryReminder;
    public event Action? Stopped;
    /// <summary>启动失败(连不上亮度接口 / 摄像头打不开), 界面据此复位"运行中"状态.</summary>
    public event Action? StartFailed;

    private readonly TaskCompletionSource _startTrigger =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsRunning => _loop is { IsCompleted: false }
        && _cts?.IsCancellationRequested != true;

    /// <summary>
    /// 预览帧就绪(引擎线程触发, 参数为紧密排列的 BGRA8 字节 + 宽高).
    /// 帧的抓取必须在引擎线程完成(MediaFrameReader 是单元线程对象),
    /// 界面只接收纯字节, 不再跨线程碰 WinRT 对象。
    /// </summary>
    public event Action<byte[], int, int>? PreviewFrameReady;

    private volatile bool _previewEnabled;

    /// <summary>开启/关闭预览帧推送(界面进入/离开摄像头页时切换).</summary>
    public void SetPreviewEnabled(bool enabled) => _previewEnabled = enabled;

    public AutoBrightnessEngine(EngineParams p)
    {
        _params = p;
    }

    /// <summary>亮度映射: 环境光(0-255) -> 亮度百分比. 对应 MapLumaToBrightness.</summary>
    public static int MapLumaToBrightness(double luma, int minB, int maxB, int brightSnap)
    {
        if (luma < 0) return -1;
        if (brightSnap > 0 && brightSnap <= 255 && luma >= brightSnap) return maxB;
        double norm = Math.Clamp(luma / 255.0, 0.0, 1.0);
        double curved = Math.Pow(norm, GammaExponent);   // 伽马曲线, 暗处更敏感
        int b = minB + (int)((maxB - minB) * curved + 0.5);
        return Math.Clamp(b, minB, maxB);
    }

    /// <summary>启动工作循环. 已在运行时忽略.</summary>
    public async Task<bool> StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_loop is { IsCompleted: false })
            {
                if (_cts?.IsCancellationRequested != true) return false;
                await _loop;   // 上一轮正在收尾, 等它退出
            }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            // 在这里就把工作循环跑起来, 但它会先等这次触发.
            // 这样即使从未真正开工, Dispose/StopAsync 也能通过取消令牌把它收掉,
            // 不会出现"任务挂在启动触发上、超时也退不出去"的情况.
            _loop = Task.Run(() => RunAsync(_cts.Token));
            _startTrigger.TrySetResult();
            return true;
        }
        finally { _lifecycleGate.Release(); }
    }

    /// <summary>请求停止. 等待循环退出(不设超时, 供正常的停止流程使用).</summary>
    public async Task StopAsync()
    {
        Task? loop;
        await _lifecycleGate.WaitAsync();
        try
        {
            _cts?.Cancel();
            loop = _loop;
        }
        finally { _lifecycleGate.Release(); }
        if (loop is not null)
            await loop;
    }

    /// <summary>
    /// 同步关闭: 取消并等待循环收尾, 超时后仍强制释放持有的资源.
    /// 专供窗口 Closed 使用 —— 那里 <c>await StopAsync()</c> 没法等待(fire-and-forget),
    /// 进程可能在清理跑完前就退出, 导致系统亮度覆盖不被 StopOverride(亮度被锁死)、
    /// 摄像头句柄不释放. 这里用同步等待 + 超时兜底, 保证一定执行到资源释放.
    /// </summary>
    public void Dispose()
    {
        Task? loop = null;
        try
        {
            _lifecycleGate.Wait(TimeSpan.FromSeconds(1));
            try
            {
                _cts?.Cancel();
                loop = _loop;
            }
            finally { _lifecycleGate.Release(); }
        }
        catch { /* 拿不到锁也不能阻塞退出 */ }

        if (loop is not null)
        {
            try { loop.Wait(TimeSpan.FromSeconds(3)); }
            catch { /* 超时或异常: 继续走下面的强制释放 */ }
        }

        try { _camera?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1)); } catch { }
        _camera = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        BrightnessController? brightness = null;
        try
        {
            // 循环在 StartAsync 里就启动了, 但真正干活要等这次触发.
            // 没触发直接取消时, 这里会抛 OperationCanceledException 被下面吞掉,
            // 不会走后面"启动失败"的分支去打扰界面.
            await _startTrigger.Task.WaitAsync(token);

            brightness = new BrightnessController();
            if (!brightness.Connect())
            {
                Log?.Invoke("[错误] 无法连接 WMI (该笔记本可能不支持软件调节亮度)");
                StartFailed?.Invoke();
                return;
            }

            Log?.Invoke("正在打开摄像头...");
            _camera = new CameraLuma();
            // 用户在设置里指定的摄像头(空 = 自动按内置优先选择).
            _camera.PreferredId = _params.CameraDeviceId;
            // Diag 直接写日志, 不再套一层 [相机](OpenAsync 自己已带清晰前缀).
            _camera.Diag = m => Log?.Invoke(m);
            if (!await _camera.OpenAsync(token))
            {
                Log?.Invoke("[错误] 无法打开摄像头 — 看上方诊断日志排查权限/占用");
                StartFailed?.Invoke();
                return;
            }
            Log?.Invoke("摄像头就绪, 开始自动调节.");

            bool onBattery = IsOnBattery();
            BrightnessProfile profile = _params.GetProfile(onBattery);
            int minB = Math.Clamp(profile.MinBrightness, 0, 100);
            int maxB = Math.Clamp(profile.MaxBrightness, 0, 100);
            if (minB > maxB) (minB, maxB) = (maxB, minB);

            int applied = brightness.GetCurrentBrightness();
            if (applied < 0) applied = DefaultBrightness;
            applied = Math.Clamp(applied, 0, 100);
            double smoothLuma = -1.0;
            int pendingTarget = -1;
            int pendingSamples = 0;
            DateTime lastSeenFace = DateTime.Now;
            int faceCheckCounter = 0;
            bool isDimming = false;
            // 人脸位置微调: 累计偏移量 / 当前方向 / 已连续同方向次数.
            int faceOffset = 0;
            int faceOffsetDir = 0;
            int faceOffsetSamples = 0;
            DateTime? sedentarySince = null;   // 久坐计时起点(按真实时间计, 不按循环次数)
            bool noFaceConfirmed = false;
            bool lockWarningSent = false;
            DateTime lastHoldLog = DateTime.MinValue;   // "维持"日志限流用
            // 本会话是否真正检测到过人脸 / 是否已经警告过"始终没人脸".
            // 用于防止摄像头没对准时把"检测不到脸"误当成"人走了".
            bool faceSeenThisSession = false;
            bool warnedNoFaceEver = false;
            BrightnessProfile? loggedProfile = null;

            while (!token.IsCancellationRequested)
            {
                // 每轮重新读取供电状态和当前策略，拔插电源后无需重启引擎.
                onBattery = IsOnBattery();
                profile = _params.GetProfile(onBattery);
                if (!ReferenceEquals(loggedProfile, profile))
                {
                    Log?.Invoke(ReferenceEquals(profile, _params.BatteryProfile)
                        ? "当前使用电池策略."
                        : "当前使用插电策略.");
                    loggedProfile = profile;
                    pendingTarget = -1;
                    pendingSamples = 0;
                }

                // 取本轮参数快照.
                minB = Math.Clamp(profile.MinBrightness, 0, 100);
                maxB = Math.Clamp(profile.MaxBrightness, 0, 100);
                if (minB > maxB) (minB, maxB) = (maxB, minB);
                int deadband = Math.Clamp(profile.Deadband, 0, 20);
                int brightSnap = profile.BrightSnap;
                double ema = profile.SmoothPct / 100.0;
                if (ema is <= 0.0 or > 1.0) ema = DefaultEma;
                brightness.FadeSeconds = Math.Clamp(profile.FadeSec, 0, 10);

                double luma = await _camera.ReadLumaAsync(token);
                if (luma < 0)
                {
                    Log?.Invoke("[警告] 摄像头取帧失败, 跳过本次");
                }
                else
                {
                    smoothLuma = smoothLuma < 0 ? luma : ema * luma + (1.0 - ema) * smoothLuma;
                    // 强光锁定(≥ brightSnap 直接跳最高)只看当前原始采样:
                    // 环境突然变暗时立即解除, 不让 EMA 尾巴把亮度卡在最高值.
                    double snapBasis = smoothLuma < 0 ? luma : Math.Min(luma, smoothLuma);
                    int snapForSample = brightSnap > 0 && snapBasis < brightSnap ? 0 : brightSnap;
                    // 亮度控制用 EMA 平滑后的值, 这样界面的「平滑系数」才真正生效;
                    // 强光跳满的解除条件仍由原始采样兜底(见上).
                    int target = MapLumaToBrightness(smoothLuma, minB, maxB, snapForSample);

                    // 摄像头自动曝光可能在静止环境下慢慢漂移；要求目标连续稳定三次，
                    // 并限制单次变化幅度，避免亮度突然跳到一个新值。
                    if (pendingTarget < 0 || Math.Abs(target - pendingTarget) > TargetJitterThreshold)
                    {
                        pendingTarget = target;
                        pendingSamples = 1;
                    }
                    else
                    {
                        pendingSamples++;
                    }

                    if (pendingSamples < StableSamplesRequired)
                    {
                        var snapshot = new StatusUpdate((int)(smoothLuma + 0.5), applied);
                        LatestStatus = snapshot;   // 托盘菜单读它
                        Status?.Invoke(snapshot);
                    }
                    else
                    {
                        int requested = target;
                        if (Math.Abs(requested - applied) > MaxSingleStep)
                            requested = applied + Math.Sign(requested - applied) * MaxSingleStep;

                        // 迟滞(hysteresis): 对于"调亮"要求更大的差值才动作.
                        // 屏幕自身是画面里最强的光源, 调亮会让测得的环境光偏高,
                        // 于是又想更亮 —— 单向棘轮效应, 夜里会一路顶到最高亮度.
                        // 让向上调整比向下更难触发, 就能把这个正反馈压住:
                        // 每轮最多 12% 的跳变下, 稳定后向上需要的余量大于屏幕带来的增量.
                        int delta = requested - applied;
                        bool needsApply = delta >= 0
                            ? delta >= deadband + BrightenMargin
                            : -delta >= deadband;

                        if (!isDimming && needsApply)
                        {
                            if (brightness.SetBrightness(requested))
                            {
                                Log?.Invoke($"环境光 {smoothLuma:F0}  →  目标 {requested}%  (计算值 {target}%, 从 {applied}% 过渡)");
                                applied = requested;
                            }
                            else Log?.Invoke("[警告] 设置亮度失败");
                        }
                        else if (!isDimming)
                        {
                            // "维持"是常态, 每轮都打会把真正的警告淹没.
                            // 限流: 距上一条"维持"日志 30 秒以上才再打.
                            if ((DateTime.Now - lastHoldLog).TotalSeconds >= 30)
                            {
                                lastHoldLog = DateTime.Now;
                                Log?.Invoke($"环境光 {smoothLuma:F0}  →  维持 {applied}%");
                            }
                        }
                        var snapshot = new StatusUpdate((int)(smoothLuma + 0.5), applied);
                        LatestStatus = snapshot;   // 托盘菜单读它
                        Status?.Invoke(snapshot);
                    }
                }

                // 无人检测: 每 3 次亮度检测做一次人脸检测, 省 CPU.
                if (_params.IdleLockEnabled && ++faceCheckCounter % 3 == 0)
                {
                    var faceResult = await _camera.DetectFaceAsync(token);
                    bool hasFace = faceResult?.HasFace == true;
                    float? faceY = faceResult?.FaceY;
                    uint inputIdle = GetIdleSeconds();
                    if (hasFace || inputIdle < 10)
                    {
                        if (hasFace) faceSeenThisSession = true;   // 人脸检测确实有效过才允许后续"无人"判定
                        noFaceConfirmed = false;
                        lockWarningSent = false;
                        lastSeenFace = DateTime.Now;
                        if (isDimming)
                        {
                            isDimming = false;
                            int restoreTarget = MapLumaToBrightness(smoothLuma < 0 ? 128 : smoothLuma, minB, maxB, brightSnap);
                            // 亮度恢复动画: 分3步渐变恢复, 不刺眼
                            int steps = 3;
                            for (int s = 1; s <= steps; s++)
                            {
                                int midB = applied + (restoreTarget - applied) * s / steps;
                                brightness.SetBrightness(midB);
                                applied = midB;
                                await Task.Delay(150, token);
                            }
                            Log?.Invoke($"检测到人, 亮度渐变恢复 {restoreTarget}%");
                        }
                        // 人脸位置微调: 抬头看屏幕略调亮, 低头打字略调暗.
                        // 默认关闭 —— 它与主亮度映射共用同一个输出, 容易互相打架;
                        // 需要时在设置里打开. 打开后也必须"连续同方向"才生效,
                        // 且累计偏移不超过 MaxFaceOffset, 给主映射留出主导权.
                        if (_params.FaceTiltAdjustEnabled && faceY.HasValue && !isDimming)
                        {
                            int yDir = faceY.Value < 0.3f ? 1 : (faceY.Value > 0.7f ? -1 : 0);
                            if (yDir == 0)
                            {
                                faceOffset = 0;
                                faceOffsetDir = 0;
                                faceOffsetSamples = 0;
                            }
                            else if (yDir == faceOffsetDir)
                            {
                                faceOffsetSamples++;
                            }
                            else
                            {
                                faceOffsetDir = yDir;
                                faceOffsetSamples = 1;
                            }

                            if (faceOffsetDir != 0 && faceOffsetSamples >= FaceOffsetSamplesRequired
                                && Math.Abs(faceOffset) < MaxFaceOffset)
                            {
                                faceOffset = Math.Clamp(faceOffset + faceOffsetDir * FaceOffsetStep,
                                    -MaxFaceOffset, MaxFaceOffset);
                                int newB = Math.Clamp(applied + faceOffsetDir * FaceOffsetStep, minB, maxB);
                                if (Math.Abs(newB - applied) >= deadband)
                                {
                                    brightness.SetBrightness(newB);
                                    applied = newB;
                                }
                                faceOffsetSamples = 0;   // 下次再攒够稳定次数才继续微调
                            }
                        }
                        // 久坐提醒: 按真实时间累计(原来按循环次数计, 默认参数下 45 次只有约 7 分钟),
                        // 满设定分钟数提醒一次并重新计时; 人离开时下面的分支会清零.
                        sedentarySince ??= DateTime.Now;
                        double seatedMinutes = (DateTime.Now - sedentarySince.Value).TotalMinutes;
                        if (seatedMinutes >= Math.Max(1, _params.SedentaryReminderMinutes))
                        {
                            int seated = (int)Math.Round(seatedMinutes);
                            Log?.Invoke($"你已经连续坐了 {seated} 分钟, 站起来活动一下吧");
                            SedentaryReminder?.Invoke(seated);
                            sedentarySince = DateTime.Now;
                        }
                    }
                    else if (faceResult != null && !hasFace)
                    {
                        // 安全闸: 本会话从未检测到过人脸时, "检测不到脸"大概率是摄像头没对准
                        // (外接 USB 摄像头朝墙、被挡住、画面过暗), 而不是人真的走了。
                        // 这种情况下绝不能据此降亮度/锁屏 —— 否则人坐在电脑前也会被反复降亮、锁屏。
                        if (!faceSeenThisSession)
                        {
                            noFaceConfirmed = false;
                            if (!warnedNoFaceEver)
                            {
                                warnedNoFaceEver = true;
                                Log?.Invoke("[警告] 摄像头始终没有检测到人脸, 已暂停\"无人降亮/自动锁屏\"。"
                                          + "请检查摄像头是否对准你, 或在「摄像头」页换一个测光摄像头。");
                            }
                        }
                        else
                        {
                            noFaceConfirmed = true;
                            // 人不在, 久坐计时清零(回来重新开始计).
                            sedentarySince = null;
                            faceOffset = 0;
                            faceOffsetDir = 0;
                            faceOffsetSamples = 0;
                            int faceIdleSec = (int)(DateTime.Now - lastSeenFace).TotalSeconds;
                            // 10秒无人脸+无输入 → 开始降亮度
                            if (faceIdleSec >= 10 && applied > 10)
                            {
                                isDimming = true;
                                int dimmed = Math.Max(10, applied - 20);
                                brightness.SetBrightness(dimmed);
                                applied = dimmed;
                                Log?.Invoke($"无人 {faceIdleSec}秒, 亮度降至 {dimmed}%");
                            }
                        }
                    }
                    else
                    {
                        // 人脸检测不可用(取帧失败/检测异常): 状态未知.
                        // 保守处理 — 不锁屏、不降亮度, 并把"无人"判定作废,
                        // 避免拿着过期状态对一个已经回来的人倒计时.
                        noFaceConfirmed = false;
                        lockWarningSent = false;
                    }
                }

                // 接近锁屏阈值时缩短等待, 确保能在息屏前约 10 秒发出提醒.
                // 发出提醒后等待剩余时间, 最后重新检测人脸和输入, 避免误锁屏.
                if (_params.IdleLockEnabled && noFaceConfirmed && !lockWarningSent)
                {
                    int lockAfterSeconds = Math.Max(1, _params.IdleLockSeconds);
                    int warningLead = Math.Min(10, Math.Max(0, lockAfterSeconds - 1));
                    int faceIdleSec = (int)(DateTime.Now - lastSeenFace).TotalSeconds;
                    int remaining = lockAfterSeconds - faceIdleSec;
                    if (warningLead > 0 && remaining > 0 && remaining <= warningLead
                        && GetIdleSeconds() >= 10)
                    {
                        lockWarningSent = true;
                        IdleLockWarning?.Invoke(remaining);
                        Log?.Invoke($"即将锁屏, 还剩 {remaining} 秒.");

                        // 倒计时期间每秒复查输入状态: 用户中途动了鼠标/键盘就立刻作废,
                        // 而不是干等 remaining 秒后才在最终复查里拦下 —— 那样
                        // "提醒中动了鼠标还要白等 10 秒且界面看着像要锁" 的体验很糟.
                        bool aborted = false;
                        for (int s = 0; s < remaining; s++)
                        {
                            await Task.Delay(1000, token);
                            if (token.IsCancellationRequested) break;
                            if (GetIdleSeconds() < 10)
                            {
                                aborted = true;
                                noFaceConfirmed = false;
                                lockWarningSent = false;
                                Log?.Invoke("检测到操作, 已取消自动锁屏.");
                                break;
                            }
                        }
                        if (aborted || token.IsCancellationRequested) continue;

                        var finalFaceResult = await _camera.DetectFaceAsync(token);
                        bool finalHasFace = finalFaceResult?.HasFace == true;
                        uint finalInputIdle = GetIdleSeconds();
                        int finalFaceIdleSec = (int)(DateTime.Now - lastSeenFace).TotalSeconds;
                        if (finalFaceResult is null)
                        {
                            noFaceConfirmed = false;
                            lockWarningSent = false;
                        }
                        else if (finalHasFace || finalInputIdle < 10)
                        {
                            noFaceConfirmed = false;
                            lockWarningSent = false;
                            lastSeenFace = DateTime.Now;
                            isDimming = false;
                        }
                        else if (finalFaceIdleSec >= lockAfterSeconds)
                        {
                            Log?.Invoke($"已 {finalFaceIdleSec} 秒无人脸且 {finalInputIdle} 秒无输入, 自动锁屏.");
                            LockWorkStation();
                            lastSeenFace = DateTime.Now;
                            isDimming = false;
                            lockWarningSent = false;
                        }
                        else
                        {
                            lockWarningSent = false;
                        }
                    }
                }

                // 分片等待, 便于及时响应取消.
                int intervalSec = Math.Clamp(profile.IntervalSec, 1, 30);
                if (_params.IdleLockEnabled && noFaceConfirmed && !lockWarningSent)
                {
                    int lockAfterSeconds = Math.Max(1, _params.IdleLockSeconds);
                    int warningAt = Math.Max(0, lockAfterSeconds - 10);
                    int faceIdleSec = (int)(DateTime.Now - lastSeenFace).TotalSeconds;
                    int secondsUntilWarning = warningAt - faceIdleSec;
                    if (secondsUntilWarning > 0)
                        intervalSec = Math.Min(intervalSec, Math.Max(1, secondsUntilWarning));
                }
                // 分片等待(每片 100ms), 便于及时响应取消; 顺带在等待期间推送预览帧.
                int waitTicks = intervalSec * 10;
                for (int t = 0; t < waitTicks; ++t)
                {
                    await Task.Delay(100, token);

                    // 每 300ms 推一帧预览. 关键: 抓帧必须在这个线程(引擎线程)上做,
                    // MediaFrameReader 是单元线程对象, 交给 UI 线程调用会 RPC_E_WRONG_THREAD 崩溃.
                    // 只把紧密排列的 BGRA 字节丢给界面, 两边不共享 WinRT 对象.
                    if (_previewEnabled && t % 3 == 2)
                    {
                        try
                        {
                            if (_camera != null
                                && _camera.TryReadPreviewBgra(out var px, out var pw, out var ph))
                            {
                                PreviewFrameReady?.Invoke(px, pw, ph);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { /* 预览失败不影响自动调节主流程 */ }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"[错误] {ex.Message}");
        }
        finally
        {
            // 先摘掉日志回调: 关窗口后仍往 DispatcherQueue 里投递会丢日志/乱序.
            if (_camera != null)
            {
                _camera.Diag = null;
                await _camera.DisposeAsync();
            }
            _camera = null;
            brightness?.Dispose();
            Log?.Invoke("已停止.");
            Stopped?.Invoke();
        }
    }
}
