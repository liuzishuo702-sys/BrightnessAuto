# 环境光自动亮度

用笔记本摄像头测量环境光，自动调节屏幕亮度。

没有环境光传感器的笔记本也能用——把摄像头当「眼睛」，亮了调亮、暗了调暗。

## 功能

- **摄像头测光**：打开摄像头抓帧，计算平均亮度（0–255）
- **自动调亮度**：经 EMA 平滑 + 伽马曲线映射后，通过 WMI 设置屏幕亮度
- **可调参数**：检测间隔、亮度上下限、死区、过渡时间、平滑系数、强光跳满阈值
- **现代界面**：WinUI 3 卡片布局、Acrylic 磨砂、浅色/深色主题、悬停光影
- **权限友好**：首次启动引导摄像头授权，失败时有诊断日志

## 系统要求

| 项目 | 要求 |
|------|------|
| 系统 | Windows 10 1809+ / Windows 11（64 位） |
| 硬件 | 带摄像头的笔记本，且支持软件调节亮度（内置屏） |
| 运行时 | [.NET 9](https://dotnet.microsoft.com/download) + [Windows App Runtime](https://aka.ms/windowsappsdk) |

> 外接显示器 / 台式机一般没有 `WmiMonitorBrightness`，无法用本软件调亮度。

## 快速使用

### 安装包（推荐）

1. 下载 Release 中的安装包
2. **右键 `安装.bat` → 以管理员身份运行**
3. 脚本会：信任自签名证书 → 安装 MSIX
4. 开始菜单搜索「**环境光自动亮度**」启动
5. 主页点「**开始**」，允许摄像头权限即可

### 手动安装

1. 双击 `证书.cer` → 安装到「**本地计算机** → **受信任的根证书颁发机构**」
2. 双击 `.msix` → 安装

## 仓库结构

```
.
├── BrightnessAuto/              # WinUI 3 应用
│   ├── Services/                # 测光 / 亮度 / 调节引擎
│   ├── Controls/                # 双环仪表等自定义控件
│   ├── Assets/                  # 图标与启动图
│   ├── Packaging/证书.cer       # 安装用公钥证书
│   ├── App.xaml(.cs)
│   ├── MainWindow.xaml(.cs)
│   ├── Package.appxmanifest
│   └── BrightnessAuto.csproj
├── Project1.sln
└── README.md
```

## 调节算法（简要）

1. 摄像头取帧 → 计算平均亮度 `luma`（0–255）
2. EMA 平滑：`smooth = α·luma + (1-α)·smooth`（`α = 平滑系数/100`）
3. 映射到目标亮度：
   - 若 `luma ≥ 强光跳满阈值` → 直接最高亮度
   - 否则：`norm = luma/255`，`curved = norm^0.6`（暗处更敏感）
   - `brightness = min + (max-min) · curved`
4. 死区判断：目标与当前差小于死区则不跳变
5. 通过 `WmiSetBrightness` 设置，带硬件淡入过渡

默认参数：间隔 3s · 亮度 10%–100% · 死区 3% · 过渡 2s · 平滑 35% · 强光阈值 101

## 从源码构建

```powershell
# 需要 .NET 9 SDK + Windows 10/11 SDK
cd BrightnessAuto
dotnet build -c Release -p:Platform=x64
```

打 MSIX 安装包（需本机已导入签名证书）：

```powershell
cd BrightnessAuto
dotnet build -c Release -p:Platform=x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:UapAppxPackageBuildMode=SideloadOnly `
  -p:AppxBundle=Never `
  -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=<你的证书指纹>
```

产物在 `BrightnessAuto\AppPackages\`。

> 签名证书需事先导入当前用户证书存储。仓库只包含公钥 `Packaging\证书.cer`，**不要提交带私钥的 `.pfx`**。

也可用 Visual Studio 打开 `Project1.sln` 调试运行。

## 技术栈

| 层 | 技术 |
|----|------|
| UI | WinUI 3 + Windows App SDK 1.7 |
| 运行时 | .NET 9 |
| 摄像头 | `MediaCapture` + `MediaFrameReader` |
| 亮度 | `System.Management` → WMI (`WmiMonitorBrightness*`) |
| 打包 | MSIX（自签名 sideload） |

## 权限与隐私

- 需要 **摄像头** 权限（只用于计算亮度，不录像、不上云）
- 需要 **WMI** 访问以调节内置屏亮度
- 主题偏好等本地设置写在应用本地存储，无网络请求

## 常见问题

**Q: 提示无法连接 WMI？**  
A: 当前显示器可能不支持软件调亮度（常见于外接显示器、部分台式机）。

**Q: 摄像头打不开？**  
A: 设置 → 隐私和安全性 → 摄像头 → 允许本应用访问。确认摄像头未被其他程序独占。

**Q: 重装同版本报错 `0x80073CFB`？**  
A: 同版本号不同内容不能覆盖，先卸载再装：

```powershell
Get-AppxPackage -Name BrightnessAuto.Dev | Remove-AppxPackage
```

**Q: 缺少 Windows App Runtime？**  
A: 按提示安装，或从 https://aka.ms/windowsappsdk 下载 x64 Runtime。

## 许可证

本项目以学习与个人使用为目的分享。若你二次分发安装包，请自行处理签名证书与隐私声明。

---

**显示名称**：环境光自动亮度 · **包标识**：`BrightnessAuto.Dev` · **版本**：2.0.0.0
