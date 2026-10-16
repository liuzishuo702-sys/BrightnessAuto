using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.System;

namespace BrightnessAuto.Services;

/// <summary>
/// 摄像头环境光测量. 用 MediaCapture + MediaFrameReader 抓取视频帧,
/// 计算平均亮度(0-255). 替代 C++ 版本的 Media Foundation (IMFSourceReader).
/// 优先请求 Gray8 格式(像素值即亮度), 否则回退到 Bgra8 并按 Rec.601 加权.
/// </summary>
public sealed class CameraLuma : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private MediaFrameSource? _source;
    private bool _hasExclusiveControl;
    private FaceDetector? _faceDetector;

    /// <summary>人脸检测失败只报一次, 避免每轮刷屏.</summary>
    private bool _faceDiagReported;

    /// <summary>诊断日志回调(可选). 用于定位取帧失败原因.</summary>
    public Action<string>? Diag { get; set; }

    /// <summary>
    /// 用户指定的摄像头(设备 Id 或显示名). 为空 / 未匹配到时按内置优先的自动策略选择.
    /// 由 EngineParams.CameraDeviceId 传入, 让用户能在手机虚拟摄像头与内置摄像头之间手动切换.
    /// </summary>
    public string? PreferredId { get; set; }

    /// <summary>枚举当前可用于取帧的摄像头(不需要先打开), 供设置界面列出可选项.</summary>
    public static async Task<List<(string Id, string Name, bool IsBuiltIn)>> ListCamerasAsync()
    {
        var result = new List<(string, string, bool)>();
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            foreach (var group in groups)
            {
                var info = group.SourceInfos.FirstOrDefault(
                    si => si.MediaStreamType == MediaStreamType.VideoRecord &&
                          si.SourceKind == MediaFrameSourceKind.Color)
                    ?? group.SourceInfos.FirstOrDefault(
                        si => si.SourceKind == MediaFrameSourceKind.Color);
                if (info == null) continue;

                string id = info.DeviceInformation?.Id ?? group.Id;
                string name = info.DeviceInformation?.Name ?? group.DisplayName;
                result.Add((id, name, IsBuiltIn(info)));
            }
        }
        catch
        {
            // 枚举失败时返回空列表, 界面回落为"自动".
        }
        return result
            .GroupBy(x => x.Item2)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>打开指定摄像头并启动取帧. 失败返回 false.</summary>
    public async Task<bool> OpenAsync(CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            // 1) 先查/要 webcam 能力. MSIX 包身份下 MediaCapture 会因隐私拒绝抛 UnauthorizedAccess;
            //    用户在系统设置里开了总开关, 但本应用条目仍可能是"拒绝".
            if (!await EnsureWebcamAccessAsync())
            {
                await DisposeAsync();
                return false;
            }

            // 2) 枚举视频采集设备.
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            token.ThrowIfCancellationRequested();
            Diag?.Invoke($"可用视频源组: {groups.Count}");
            MediaFrameSourceGroup? chosenGroup = null;
            MediaFrameSourceInfo? chosenInfo = null;

            // 收集所有带彩色源的可选项, 并打印出来备查.
            var candidates = new List<(MediaFrameSourceGroup Group, MediaFrameSourceInfo Info)>();
            foreach (var group in groups)
            {
                var info = group.SourceInfos.FirstOrDefault(
                    si => si.MediaStreamType == MediaStreamType.VideoRecord &&
                          si.SourceKind == MediaFrameSourceKind.Color)
                    ?? group.SourceInfos.FirstOrDefault(
                        si => si.SourceKind == MediaFrameSourceKind.Color);
                if (info == null) continue;
                candidates.Add((group, info));
                Diag?.Invoke($"  候选: {group.DisplayName}  内置={IsBuiltIn(info)}  优先级={CameraPriority(info)}");
            }

            // 排序规则:
            //   1) 用户手动指定的摄像头(PreferredId) 无条件置顶, 即使它是虚拟摄像头;
            //   2) 其余按优先级: 内置前摄 > 内置 > 普通 > 名字含"虚拟/virtual".
            // 默认自动策略把虚拟摄像头排最后, 避免拿手机朝向当环境光.
            bool hasPreference = !string.IsNullOrWhiteSpace(PreferredId);
            if (hasPreference)
            {
                var hit = candidates.FirstOrDefault(c => MatchesPreference(c, PreferredId!));
                if (hit.Group != null)
                {
                    candidates.Remove(hit);
                    candidates.Insert(0, hit);
                    Diag?.Invoke($"已按用户设置优先选用: {hit.Group.DisplayName}");
                }
                else
                {
                    Diag?.Invoke($"[警告] 设置里指定的摄像头已不存在({PreferredId}), 回退到自动选择");
                }
            }

            var best = hasPreference && candidates.Count > 0
                ? candidates[0]
                : candidates
                    .OrderBy(c => CameraPriority(c.Info))
                    .ThenBy(c => c.Group.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

            if (best.Group != null)
            {
                chosenGroup = best.Group;
                chosenInfo = best.Info;
                bool builtIn = IsBuiltIn(chosenInfo);
                Diag?.Invoke($"选用: {chosenGroup.DisplayName}" + (builtIn ? " (内置摄像头)" : " (非内置)"));
            }

            if (chosenGroup == null || chosenInfo == null)
            {
                // 有时 FrameSourceGroup 为空但设备枚举仍有摄像头 → 走默认初始化回退.
                Diag?.Invoke("[警告] MediaFrameSourceGroup 未找到彩色源, 尝试默认摄像头初始化");
                if (await OpenDefaultCaptureAsync())
                    return true;
                Diag?.Invoke("[警告] 未找到可用摄像头设备 (可在设置→蓝牙和设备→相机 检查是否被占用/禁用)");
                return false;
            }

            // 3) 初始化: 先独占, 失败(占用/部分驱动)再共享只读.
            //    "初始化成功但帧读取器起不来"也要继续降级, 所以把两步合成一个整体重试.
            bool ready = false;
            foreach (bool exclusive in new[] { true, false })
            {
                if (!await InitCaptureAsync(chosenGroup, exclusive))
                    continue;
                if (_capture!.FrameSources.TryGetValue(chosenInfo.Id, out var source)
                    && await StartReaderAsync(source))
                {
                    _source = source;
                    _hasExclusiveControl = exclusive;
                    ready = true;
                    break;
                }
                // 这个模式不可用: 清干净再试下一种, 避免残留的 capture/source 影响后续.
                await DisposeCaptureOnlyAsync();
            }

            if (!ready)
            {
                Diag?.Invoke("[警告] 指定源组不可用, 尝试默认摄像头");
                if (!await OpenDefaultCaptureAsync())
                    return false;
            }

            // 预热: 丢弃前几帧(等自动曝光稳定).
            await WarmupAsync();
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Diag?.Invoke("[错误] 摄像头访问被拒绝 (UnauthorizedAccess)");
            Diag?.Invoke("  请打开: 设置 → 隐私和安全性 → 摄像头");
            Diag?.Invoke("  1) 「摄像头访问」与「让桌面应用访问摄像头」都打开");
            Diag?.Invoke("  2) 下方列表找到「环境光自动亮度」/ BrightnessAuto 设为「允许」");
            Diag?.Invoke("  若列表没有本应用: 先点下方「打开摄像头设置」, 或重装后再点开始触发授权");
            Diag?.Invoke($"  详情: {ex.Message}");
            _ = OpenCameraPrivacySettingsAsync();
            await DisposeAsync();
            return false;
        }
        catch (Exception ex)
        {
            Diag?.Invoke($"[错误] 打开摄像头异常: {ex.GetType().Name}: {ex.Message}");
            await DisposeAsync();
            return false;
        }
    }

    /// <summary>候选是否匹配用户在设置里指定的摄像头(设备 Id 优先, 其次显示名).</summary>
    private static bool MatchesPreference((MediaFrameSourceGroup Group, MediaFrameSourceInfo Info) candidate, string preferred)
        => string.Equals(candidate.Info.DeviceInformation?.Id, preferred, StringComparison.OrdinalIgnoreCase)
        || string.Equals(candidate.Group.DisplayName, preferred, StringComparison.OrdinalIgnoreCase)
        || string.Equals(candidate.Info.DeviceInformation?.Name, preferred, StringComparison.OrdinalIgnoreCase);

    /// <summary>摄像头是否内置(笔记本自带). 枚举不到位置信息时保守视为内部.</summary>
    private static bool IsBuiltIn(MediaFrameSourceInfo info)
    {
        var panel = info.DeviceInformation?.EnclosureLocation?.Panel;
        return panel is null or Panel.Unknown or Panel.Front or Panel.Back;
    }

    /// <summary>
    /// 摄像头选择优先级(数字越小越优先):
    /// 0 内置前摄(正对用户的笔记本摄像头) / 1 其他内置 / 2 名字不像虚拟设备 / 3 其余(手机投屏等虚拟摄像头).
    /// 虚拟摄像头排最后, 避免拿手机朝向当环境光.
    /// </summary>
    private static int CameraPriority(MediaFrameSourceInfo info)
    {
        var panel = info.DeviceInformation?.EnclosureLocation?.Panel;
        if (panel == Panel.Front) return 0;
        if (panel == Panel.Back) return 1;

        string name = info.DeviceInformation?.Name ?? string.Empty;
        if (panel is null or Panel.Unknown)
            return NamePriority(name);   // 2 普通 / 3 虚拟
        return 2; // 外接摄像头: 比内置靠后, 但优先于虚拟设备
    }

    /// <summary>设备名里带"虚拟/virtual"的排在最后(手机投屏、OBS 虚拟摄像头等).</summary>
    private static int NamePriority(string name)
        => name.Contains("虚拟", StringComparison.OrdinalIgnoreCase)
           || name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ? 3 : 2;

    /// <summary>按 DeviceInformation 面板信息选择摄像头的优先级(与源组路径一致).</summary>
    private static int DevicePriority(DeviceInformation device)
    {
        var panel = device.EnclosureLocation?.Panel;
        if (panel == Panel.Front) return 0;
        if (panel == Panel.Back) return 1;
        if (panel is null or Panel.Unknown) return NamePriority(device.Name ?? string.Empty);
        return 2;
    }

    /// <summary>检查并请求 webcam 能力; 拒绝时给出可操作提示.</summary>
    private async Task<bool> EnsureWebcamAccessAsync()
    {
        try
        {
            var cap = AppCapability.Create("webcam");
            var status = cap.CheckAccess();
            Diag?.Invoke($"webcam 能力状态: {status}");

            if (status == AppCapabilityAccessStatus.Allowed)
                return true;

            if (status is AppCapabilityAccessStatus.UserPromptRequired
                       or AppCapabilityAccessStatus.DeniedByUser)
            {
                // DeniedByUser 时 RequestAccessAsync 通常仍返回 Denied, 但部分系统会再弹一次.
                Diag?.Invoke("正在请求摄像头权限...");
                status = await cap.RequestAccessAsync();
                Diag?.Invoke($"请求结果: {status}");
            }

            if (status == AppCapabilityAccessStatus.Allowed)
                return true;

            switch (status)
            {
                case AppCapabilityAccessStatus.DeniedByUser:
                    Diag?.Invoke("[错误] 摄像头权限被用户拒绝");
                    Diag?.Invoke("  设置 → 隐私和安全性 → 摄像头 → 找到本应用改为「允许」");
                    Diag?.Invoke("  或: 设置里关掉再打开本应用开关, 然后重新点「开始」");
                    break;
                case AppCapabilityAccessStatus.DeniedBySystem:
                    Diag?.Invoke("[错误] 摄像头权限被系统策略拒绝 (组策略/家长控制/企业配置)");
                    break;
                case AppCapabilityAccessStatus.NotDeclaredByApp:
                    Diag?.Invoke("[错误] 应用清单未声明 webcam 能力 (安装包异常, 请重装正式包)");
                    break;
                default:
                    Diag?.Invoke($"[错误] 摄像头权限不可用: {status}");
                    break;
            }

            _ = OpenCameraPrivacySettingsAsync();
            return false;
        }
        catch (Exception ex)
        {
            // 老系统/无 AppCapability 时不拦, 交给后续 MediaCapture 再报.
            Diag?.Invoke($"webcam 能力检查跳过: {ex.Message}");
            return true;
        }
    }

    private async Task<bool> InitCaptureAsync(MediaFrameSourceGroup group, bool exclusive)
    {
        try
        {
            await DisposeCaptureOnlyAsync();
            _capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = exclusive
                    ? MediaCaptureSharingMode.ExclusiveControl
                    : MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video
            };
            await _capture.InitializeAsync(settings);
            _hasExclusiveControl = exclusive;
            Diag?.Invoke(exclusive ? "MediaCapture 独占模式就绪" : "MediaCapture 共享只读模式就绪");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            throw; // 交给外层统一处理
        }
        catch (Exception ex)
        {
            Diag?.Invoke($"初始化失败({(exclusive ? "独占" : "共享")}): {ex.Message}");
            await DisposeCaptureOnlyAsync();
            return false;
        }
    }

    /// <summary>不指定 SourceGroup, 让系统选默认视频设备.</summary>
    private async Task<bool> OpenDefaultCaptureAsync()
    {
        try
        {
            // 先确认系统里至少有一个视频采集设备.
            string selector = Windows.Media.Devices.MediaDevice.GetVideoCaptureSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            Diag?.Invoke($"默认视频设备数: {devices.Count}");
            if (devices.Count == 0)
            {
                Diag?.Invoke("[警告] 系统未枚举到任何摄像头");
                return false;
            }
            foreach (var d in devices.Take(6))
                Diag?.Invoke($"  - {d.Name} (面板={d.EnclosureLocation?.Panel}, 优先级={DevicePriority(d)})");

            // 和源组路径用同一套优先级: 内置摄像头优先, 虚拟摄像头垫底.
            var ordered = devices
                .OrderBy(DevicePriority)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await DisposeCaptureOnlyAsync();
            _capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            };
            // 按优先级选设备, 避免多设备时选到手机虚拟摄像头.
            if (!string.IsNullOrEmpty(ordered[0].Id))
                settings.VideoDeviceId = ordered[0].Id;
            Diag?.Invoke($"默认路径选用: {ordered[0].Name}");

            await _capture.InitializeAsync(settings);
            _hasExclusiveControl = false;

            _source = _capture.FrameSources.Values.FirstOrDefault(
                s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                ?? _capture.FrameSources.Values.FirstOrDefault();
            if (_source == null)
            {
                Diag?.Invoke("[警告] 默认 MediaCapture 无可用 FrameSource");
                return false;
            }

            Diag?.Invoke($"默认源: {_source.Info.SourceKind} / {_source.Info.DeviceInformation?.Name}");
            return await StartReaderAsync(_source);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Diag?.Invoke($"默认摄像头初始化失败: {ex.Message}");
            await DisposeCaptureOnlyAsync();
            return false;
        }
    }

    private async Task<bool> StartReaderAsync(MediaFrameSource source)
    {
        try
        {
            // 优先选低分辨率格式(足够测亮度, 更省资源).
            var format = source.SupportedFormats
                .Where(f => f.VideoFormat.Width > 0 && f.VideoFormat.Height > 0)
                .OrderBy(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                .FirstOrDefault();
            if (format != null && _hasExclusiveControl)
            {
                await source.SetFormatAsync(format);
                Diag?.Invoke($"格式: {format.Subtype} {format.VideoFormat.Width}x{format.VideoFormat.Height}");
            }

            // 请求 Bgra8(广泛支持); 在读取时统一转换为 Gray8 计算亮度.
            _reader = await _capture!.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            var status = await _reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                await _reader.StopAsync();
                _reader.Dispose();
                _reader = await _capture.CreateFrameReaderAsync(source);
                status = await _reader.StartAsync();
                if (status != MediaFrameReaderStartStatus.Success)
                {
                    Diag?.Invoke($"[警告] 帧读取器启动失败: {status}");
                    return false;
                }
            }
            Diag?.Invoke("帧读取器已启动");
            return true;
        }
        catch (Exception ex)
        {
            Diag?.Invoke($"启动帧读取器异常: {ex.Message}");
            return false;
        }
    }

    private async Task WarmupAsync()
    {
        if (_reader == null) return;
        for (int i = 0; i < 15; ++i)
        {
            using var f = _reader.TryAcquireLatestFrame();
            await Task.Delay(60);
        }
    }

    /// <summary>打开系统摄像头隐私设置页, 方便用户改本应用开关.</summary>
    public static async Task OpenCameraPrivacySettingsAsync()
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));
        }
        catch { /* 忽略 */ }
    }

    /// <summary>获取最新一帧的 Bgra8 副本(用于 UI 预览). 无帧时返回 null.</summary>
    public SoftwareBitmap? GetLatestPreviewFrame()
    {
        if (_reader == null) return null;
        using var frameRef = _reader.TryAcquireLatestFrame();
        var bitmap = frameRef?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap == null) return null;
        // 转 Bgra8 Premultiplied 供 SoftwareBitmapSource 显示, 复制一份避免 frameRef 释放后失效.
        return SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>读取一次平均亮度(0-255). 失败返回 -1.</summary>
    public async Task<double> ReadLumaAsync(CancellationToken token = default)
    {
        if (_reader == null) return -1.0;
        // 抓最新帧: TryAcquireLatestFrame 常返回 null(帧间空档), 多试几次.
        for (int attempt = 0; attempt < 8; ++attempt)
        {
            token.ThrowIfCancellationRequested();
            using var frameRef = _reader.TryAcquireLatestFrame();
            var bitmap = frameRef?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap == null)
            {
                await Task.Delay(40, token);
                continue;
            }

            SoftwareBitmap gray = bitmap.BitmapPixelFormat == BitmapPixelFormat.Gray8
                ? bitmap
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
            try
            {
                double luma = AverageGray(gray);
                if (luma >= 0) return luma;
            }
            finally
            {
                if (!ReferenceEquals(gray, bitmap))
                    gray.Dispose();
                bitmap.Dispose();
            }
        }
        return -1.0;
    }

    /// <summary>人脸检测结果: 是否有人脸 + 人脸垂直位置(0=顶部, 1=底部).</summary>
    public readonly record struct FaceResult(bool HasFace, float? FaceY);

    /// <summary>检测当前帧中的人脸. 失败返回 null.</summary>
    public async Task<FaceResult?> DetectFaceAsync(CancellationToken token = default)
    {
        if (_reader == null) return null;
        try
        {
            _faceDetector ??= await FaceDetector.CreateAsync();
            for (int attempt = 0; attempt < 3; ++attempt)
            {
                token.ThrowIfCancellationRequested();
                using var frameRef = _reader.TryAcquireLatestFrame();
                var bitmap = frameRef?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap == null)
                {
                    await Task.Delay(40, token);
                    continue;
                }
                // FaceDetector 只接受 Gray8 / Nv12: 直接把 Bgra8 原帧丢进去会抛 E_INVALIDARG,
                // 异常被下面的 catch 吞掉后 "无人检测/自动锁屏" 就整条链路静默失效. 先转格式再检测.
                SoftwareBitmap detect = bitmap.BitmapPixelFormat == BitmapPixelFormat.Gray8
                    ? bitmap
                    : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
                try
                {
                    var faces = await _faceDetector.DetectFacesAsync(detect);
                    if (faces.Count == 0) return new FaceResult(false, null);
                    var face = faces.OrderByDescending(f => f.FaceBox.Width * f.FaceBox.Height).First();
                    float y = (float)face.FaceBox.Y / detect.PixelHeight;
                    return new FaceResult(true, y);
                }
                finally
                {
                    if (!ReferenceEquals(detect, bitmap)) detect.Dispose();
                    bitmap.Dispose();
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            // 静默失败会让无人检测/自动锁屏悄悄失效, 至少报一次.
            if (!_faceDiagReported)
            {
                _faceDiagReported = true;
                Diag?.Invoke($"[警告] 人脸检测不可用({ex.GetType().Name}): {ex.Message}");
            }
            return null;
        }
    }

    /// <summary>
    /// 估算环境光亮度(0-255): 把画面分成 左带 / 右带 / 下带 三块互不重叠的区域,
    /// 各求平均后取<b>中位数</b>。
    ///
    /// 为什么不用全画面平均: 屏幕照亮的是画面中央的人脸/胸口, 全画面平均会把
    /// "屏幕有多亮"算进环境光, 形成正反馈 —— 屏幕变亮 → 人脸变亮 → 测得环境光更亮
    /// → 屏幕更亮, 夜里会一路顶到最高亮度。
    ///
    /// 取三块的中位数而不是最大值/平均值: 只要中央(脸)所在的那块被抬高, 它就落在
    /// 中位数之外被丢弃, 而剩下两块反映的是画面边缘的真实环境照度。
    /// (侧窗强光/单侧台灯这类真实单向光源仍会被保留, 因为那会同时抬高相邻区域。)
    /// </summary>
    private static double AverageGray(SoftwareBitmap gray)
    {
        int width = gray.PixelWidth, height = gray.PixelHeight;
        if (width == 0 || height == 0) return -1.0;

        int stride;
        int startIndex;
        using (var buffer = gray.LockBuffer(BitmapBufferAccessMode.Read))
        {
            var plane = buffer.GetPlaneDescription(0);
            stride = plane.Stride;
            startIndex = plane.StartIndex;
        }

        int bufSize = startIndex + stride * height;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(bufSize);
        try
        {
            gray.CopyToBuffer(pixels.AsBuffer(0, bufSize));

            // 采样步长: 目标约 5 万点以内, 兼顾精度与开销.
            long total = (long)width * height;
            int step = total > 100_000 ? (int)(total / 50_000) : 1;
            if (step < 1) step = 1;

            int bandW = Math.Max(1, width / 4);        // 左右带各占 1/4 宽
            int bottomTop = height * 3 / 4;           // 下带从 3/4 高处开始

            // 区域 1: 左带(全高)  区域 2: 右带(全高)  区域 3: 下带(全宽)
            double[] sums = new double[3];
            long[] counts = new long[3];

            for (int y = 0; y < height; y += step)
            {
                int row = startIndex + y * stride;
                bool inBottom = y >= bottomTop;

                for (int x = 0; x < width; x += step)
                {
                    byte px = pixels[row + x];
                    if (x < bandW) { sums[0] += px; counts[0]++; }              // 左
                    else if (x >= width - bandW) { sums[1] += px; counts[1]++; } // 右
                    if (inBottom) { sums[2] += px; counts[2]++; }               // 下
                }
            }

            double[] means = new double[3];
            int valid = 0;
            for (int i = 0; i < 3; i++)
            {
                if (counts[i] > 0) means[valid++] = sums[i] / counts[i];
            }
            if (valid == 0) return -1.0;
            if (valid == 1) return means[0];

            Array.Sort(means, 0, valid);
            // 中位数: 偶数个取中间两个的均值
            int mid = valid / 2;
            return valid % 2 == 1 ? means[mid] : (means[mid - 1] + means[mid]) / 2.0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private async Task DisposeCaptureOnlyAsync()
    {
        try
        {
            if (_reader != null)
            {
                try { await _reader.StopAsync(); } catch { }
                _reader.Dispose();
                _reader = null;
            }
        }
        catch { }

        _source = null;
        _hasExclusiveControl = false;
        try { _capture?.Dispose(); } catch { }
        _capture = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeCaptureOnlyAsync();
    }
}
