# WitchSpring3Resolution

《魔女之泉3 Re:Fine》（WitchSpring3 Re:Fine）PC 版自适应分辨率 Mod，基于 BepInEx 5，支持原生全屏、超宽屏横向扩展和等比 UI 缩放。

## 功能

- **原生全屏**：全屏模式按游戏窗口所在显示器的桌面分辨率输出，使用无边框全屏。16:10、16:9 和超宽屏使用完整视口，默认不添加适配黑边。
- **超宽屏画面扩展**：3440×1440 扩展左右视野，保持角色、场景和 UI 的比例；标题场景可显示原构图之外的背景及其边界。
- **UI 等比缩放**：按屏幕高度缩放，保留边缘 HUD 锚点和居中弹窗。相同高度下，按钮、文字和图标大小一致。
- **标题菜单分组**：“新的开始”“时间读取”“继续旅程”“Quit”保持在同一列，选择光标与菜单项同步。
- **关键 UI 安全间距**：对已识别的返回、确认、攻击、野外菜单及 HP/MP 状态栏按 12px 目标边距调整；状态栏作为整体移动。
- **启动自动适配与运行时切换**：启动及进入游戏场景时应用分辨率策略；检测宽度、高度和全屏模式变化，尺寸稳定 200ms 后更新布局和输入坐标。
- **独立插件**：无需修改游戏程序集或资源，与其他 BepInEx 插件分别加载。

## 环境要求

- Windows 版 WitchSpring3 Re:Fine。
- BepInEx **5.x x64**；已验证环境为 BepInEx 5.4.23.6、Unity 2019.2.18f1、Mono。
- 此插件不面向 BepInEx 6 或 IL2CPP 版本。

## 安装

1. 从 [Releases](https://github.com/xujinglie/WitchSpring3Resolution/releases/latest) 下载 `WitchSpring3Resolution-v1.0.1.zip`。
2. 关闭游戏。如未安装 BepInEx，请先从 [BepInEx 官方发布页](https://github.com/BepInEx/BepInEx/releases) 安装 5.x x64 版本到游戏目录。
3. 将 ZIP 中的 `BepInEx` 文件夹合并到游戏根目录，即 `WitchSpring3 ReFine.exe` 所在目录。
4. 启动游戏，插件自动生效。

安装后的主要文件：

```text
WitchSpring3 ReFine.exe
BepInEx/
  plugins/WitchSpring3Resolution/WitchSpring3Resolution.dll
  config/com.dsh.witchspring3.resolution.cfg
```

已使用本 Mod 并自定义配置时，更新可只替换 DLL，以保留原配置。

## 全屏与窗口模式

**全屏推荐使用默认配置。** 桌面为 3440×1440 时，全屏输出 3440×1440；桌面为 2560×1600 时，全屏输出 2560×1600。全屏保持原生桌面尺寸，不通过非等比拉伸填满屏幕。

游戏设置菜单中的原有分辨率选项在窗口模式下直接生效；在全屏模式下仅记录下次切回窗口时使用的尺寸，当前全屏继续使用原生桌面分辨率。

窗口模式支持以下目标尺寸：

| 分辨率 | 推荐策略 | UI 缩放系数 | 画面显示 |
|---|---|---:|---|
| 3440×1440 | 固定高度、等比缩放 | 2.4 | 扩展左右视野，无适配黑边 |
| 2560×1440 | 固定高度、等比缩放 | 2.4 | 完整 16:9 画面 |
| 1920×1080 | 固定高度、等比缩放 | 1.8 | 完整 16:9 画面 |
| 1366×768 | 固定高度、等比缩放 | 1.28 | 完整画面，不为微小比例差添加黑边 |

缩放系数以游戏现有的 600 逻辑单位画布高度为基准。边缘控件随边缘锚点移动，居中控件随屏幕中心移动。布局优先保证关键 UI 完整、文字可读及元素等比；固定宽度和固定高度均可行时，选择与原 UI 代码一致的固定高度。

原版窗口不可拖动改大小的设定保留；游戏设置、启动参数或其他方式实际改变窗口尺寸后，插件会响应变化。切换显示器后可重新选择全屏以读取该显示器的桌面尺寸。

## 配置

配置文件：`BepInEx/config/com.dsh.witchspring3.resolution.cfg`。修改配置后重启游戏。

```ini
[General]
Enabled = true
NativeAtStartup = true
StartupWidth = 0
StartupHeight = 0
ExpandWorld = true
```

| 配置项 | 含义 |
|---|---|
| `Enabled` | 是否启用分辨率 Mod |
| `NativeAtStartup` | 启动时自动应用桌面尺寸；默认开启 |
| `StartupWidth` / `StartupHeight` | 窗口模式的指定启动宽高，需成对填写；`0` 为自动 |
| `ExpandWorld` | `true` 使用完整视口并扩展画面；`false` 使用带黑边的 16:9 构图模式 |

默认无黑边模式使用 `ExpandWorld = true`。全屏始终使用桌面原生尺寸；窗口启动尺寸优先级为：成对的命令行宽高 > 配置宽高 > 桌面尺寸。

例如，以 3440×1440 窗口启动：

```powershell
& '.\WitchSpring3 ReFine.exe' -screen-width 3440 -screen-height 1440 -screen-fullscreen 0
```

## 诊断快捷键

- **F8**：窗口模式依次测试四档目标尺寸；全屏模式保持原生全屏并采样。
- **F9**：保持当前尺寸与模式，导出布局检查、性能采样和截图。
- 每次采样约 12 秒，采样期间不接受下一次诊断请求。

诊断输出在游戏目录的 `ModDev/resolution-reports/`，包含 PNG、UI 边界及字号 CSV、按钮交集候选 CSV、相机参数 CSV 和汇总 TXT。诊断不会修改存档。

### 鼠标边界检查

仓库中的 `tools/Test-CursorBounds.ps1` 可读取 Windows 实际鼠标限制矩形和游戏客户端边界。先启动游戏，再在源码目录执行：

```powershell
pwsh -NoProfile -File .\tools\Test-CursorBounds.ps1 -GameDirectory 'F:\SteamLibrary\steamapps\common\WitchSpring3'
```

添加 `-Sweep` 可依次将鼠标移动到窗口四角及四边中点附近，核对实际位置是否与目标相差不超过 1px；完成后恢复原鼠标位置。测试只在游戏位于前台时执行，不发送点击。运行期间请暂时不要移动鼠标；切换到其他窗口会中止扫描。

结果保存为游戏目录下的 `ModDev/resolution-reports/cursor-bounds.json`，可用 `-Name` 指定报告名。`client` 和 `clip` 使用屏幕坐标，`samples` 使用游戏客户端坐标。该检查只验证系统鼠标可达范围，游戏内 UI 点击与角色移动响应仍需另行检查。

## 已验证范围

- 四档窗口输出：3440×1440、2560×1440、1920×1080、1366×768。
- 2560×1600 原生无边框全屏：输出尺寸与桌面尺寸匹配，实际客户端截图上下适配黑边均为 0px。
- 地图 HP/MP 状态栏、Text/TMP 字号及相机比例已做几何检查；标题菜单四项已完成四档窗口布局检查。

3440×1440 物理全屏尚未在对应显示器上验收。返回、确认、攻击按钮及所有战斗、背包、对话和语言组合尚未完成完整回归；诊断报告中未出现的控件不视为通过。标题场景原有背景边界、剧情遮罩与淡入淡出效果保留。

运行时切换会重建布局和显示缓冲区，短时帧时间取决于场景、分辨率与驱动；未承诺所有硬件的 FPS 波动范围或长期内存增长上限。

## 从源码编译

需要 PowerShell 7、.NET SDK，以及已安装游戏和 BepInEx 的本地目录。编译只使用本地程序集，无需 NuGet；仓库不包含游戏 DLL 或 BepInEx 二进制。

```powershell
pwsh -NoProfile -File .\build.ps1 -GameDirectory 'F:\SteamLibrary\steamapps\common\WitchSpring3'
```

输出：`dist/WitchSpring3Resolution.dll`。游戏关闭后，将其复制到插件安装目录即可。

生成与发布版相同结构的安装 ZIP：

```powershell
pwsh -NoProfile -File .\package.ps1 -GameDirectory 'F:\SteamLibrary\steamapps\common\WitchSpring3'
```

输出：`dist/WitchSpring3Resolution-v1.0.1.zip` 及 SHA256 校验文件。

## 卸载

关闭游戏，删除 `BepInEx/plugins/WitchSpring3Resolution/`。配置文件可按需删除。
