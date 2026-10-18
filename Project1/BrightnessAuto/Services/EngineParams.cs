namespace BrightnessAuto.Services;

public enum PowerPolicy
{
    FollowPowerSource,
    AlwaysPlugged,
    AlwaysBattery
}

/// <summary>一套自动亮度调节参数.</summary>
public sealed class BrightnessProfile
{
    public int IntervalSec { get; set; } = 3;
    public int MinBrightness { get; set; } = 10;
    public int MaxBrightness { get; set; } = 100;
    public int Deadband { get; set; } = 3;
    public int FadeSec { get; set; } = 2;
    public int SmoothPct { get; set; } = 35;
    public int BrightSnap { get; set; } = 101;

    public BrightnessProfile(bool batteryDefaults = false)
    {
        if (batteryDefaults)
        {
            MaxBrightness = 70;
            IntervalSec = 5;
            SmoothPct = 30;
        }
    }

    public void Reset(bool batteryDefaults = false)
    {
        var defaults = new BrightnessProfile(batteryDefaults);
        IntervalSec = defaults.IntervalSec;
        MinBrightness = defaults.MinBrightness;
        MaxBrightness = defaults.MaxBrightness;
        Deadband = defaults.Deadband;
        FadeSec = defaults.FadeSec;
        SmoothPct = defaults.SmoothPct;
        BrightSnap = defaults.BrightSnap;
    }
}

/// <summary>
/// 自动调节参数. 界面写入, 引擎工作循环读取(每轮取快照).
/// 对应 C++ 版本的 Params 结构 + 全局 g_params.
/// </summary>
public sealed class EngineParams
{
    public BrightnessProfile PluggedProfile { get; } = new();
    public BrightnessProfile BatteryProfile { get; } = new(batteryDefaults: true);

    /// <summary>选择跟随系统电源，或固定使用某一套配置.</summary>
    public PowerPolicy PowerPolicy { get; set; } = PowerPolicy.FollowPowerSource;

    /// <summary>当前正在被界面编辑的配置.</summary>
    public bool EditingBatteryProfile { get; set; }

    private BrightnessProfile EditingProfile => EditingBatteryProfile ? BatteryProfile : PluggedProfile;

    /// <summary>检测间隔(秒).</summary>
    public int IntervalSec { get => EditingProfile.IntervalSec; set => EditingProfile.IntervalSec = value; }

    /// <summary>最低亮度(%).</summary>
    public int MinBrightness { get => EditingProfile.MinBrightness; set => EditingProfile.MinBrightness = value; }

    /// <summary>最高亮度(%).</summary>
    public int MaxBrightness { get => EditingProfile.MaxBrightness; set => EditingProfile.MaxBrightness = value; }

    /// <summary>变化死区(%): 目标与当前差小于此值时不调整.</summary>
    public int Deadband { get => EditingProfile.Deadband; set => EditingProfile.Deadband = value; }

    /// <summary>过渡时间(秒): WmiSetBrightness 的淡入时长.</summary>
    public int FadeSec { get => EditingProfile.FadeSec; set => EditingProfile.FadeSec = value; }

    /// <summary>平滑系数(1-100), 实际 EMA 权重 = 值/100.</summary>
    public int SmoothPct { get => EditingProfile.SmoothPct; set => EditingProfile.SmoothPct = value; }

    /// <summary>强光跳满阈值(0-255): 环境光≥此值直接到最高亮度. 0 或 >255 表示关闭.</summary>
    public int BrightSnap { get => EditingProfile.BrightSnap; set => EditingProfile.BrightSnap = value; }

    /// <summary>根据当前电源状态返回引擎应使用的配置.</summary>
    public BrightnessProfile GetProfile(bool onBattery) => PowerPolicy switch
    {
        PowerPolicy.AlwaysPlugged => PluggedProfile,
        PowerPolicy.AlwaysBattery => BatteryProfile,
        _ => onBattery ? BatteryProfile : PluggedProfile
    };

    /// <summary>启用无人自动锁屏.</summary>
    public bool IdleLockEnabled { get; set; } = true;

    /// <summary>
    /// 启用"无人时降低亮度"(省电/护眼). 与锁屏分开, 因为很多人只想降亮不想被锁.
    /// </summary>
    public bool DimWhenAwayEnabled { get; set; } = true;

    /// <summary>无人多久后自动锁屏(秒).</summary>
    public int IdleLockSeconds { get; set; } = 60;

    /// <summary>连续有人多久提醒一次久坐(分钟).</summary>
    public int SedentaryReminderMinutes { get; set; } = 45;

    /// <summary>
    /// 指定用于测光的摄像头(设备 Id, 空 = 自动按"内置优先"选择).
    /// 允许用户在手机虚拟摄像头与内置摄像头之间手动切换; 下次启动引擎时生效.
    /// </summary>
    public string CameraDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 启用"人脸位置微调"(抬头调亮 / 低头调暗). 默认<b>关闭</b>:
    /// 该功能与主亮度映射共用同一个输出, 容易互相打架, 且"抬头≠环境变亮"的假设
    /// 对多数人并不成立. 想要的话可在设置里打开.
    /// </summary>
    public bool FaceTiltAdjustEnabled { get; set; } = false;
}
