# Atlas for SAI2

把 Atlas（三维色彩空间取色器）做成 PaintTool SAI Ver.2 里的一块取色面板：在面板里转立体取色，点一下就把颜色送进 SAI2 的**前景色**；SAI2 里换了颜色，面板也跟着变。

不改 SAI2 本体、不替换 DLL、不注入进程——面板是独立程序，通过 SAI2 自己的颜色流程写前景色。

![面板预览](docs/panel-preview.png)

## 便携版（推荐给普通用户）

从 [Releases](https://github.com/Dandelion011/atlas/releases) 下载 `Atlas-for-SAI2-v*-win-x64.zip`，解压后**双击 `Atlas.Sai2.exe`** 就能用：

- 自带 .NET 运行时，不需要安装 .NET、不需要管理员权限；
- 页面资源（index.html / embed.css / embed.js）已打进 exe，第一次运行会解包到 `%LOCALAPPDATA%\Atlas.Sai2\web`；
- 不写注册表、不改 SAI2 目录；只有 WebView2 缓存和窗口宽度记在 `%LOCALAPPDATA%\Atlas.Sai2`；
- 仍然需要系统里有 **WebView2 Runtime**（Win11 自带，Win10 装过 Edge 一般也有）。

只想用便携版的话看完这一节就够了；下面是源码构建 / 装进 SAI2 目录的玩法。

## 安装（装进 SAI2 目录）

要求：
- Windows 10 / 11 x64
- **.NET 10 Desktop Runtime**（用便携版则不需要）
- **WebView2 Runtime**（Win11 自带；Win10 一般随 Edge 安装）
- 目标 SAI2：`PaintTool SAI Ver.2 (64bit) Alpha.2026.10.19`（PivInk 汉化版，exe sha256 `FF227172…A15F`）。其它版本可以打开面板、读取当前色，但**写色会拒绝**并给出提示。

在仓库里先构建，再安装到 SAI2 程序目录：

```powershell
.\plugin\build.ps1
.\plugin\install\install.ps1 -Sai2Path 'F:\[PivInk]SAI Ver.2'
```

安装只会在 SAI2 程序目录下新建 `Atlas\` 子目录并注册一个开始菜单快捷方式，不写注册表、不改 SAI2 文件。

卸载：

```powershell
& 'F:\[PivInk]SAI Ver.2\Atlas\uninstall.ps1'
```

## 使用

1. 先打开 SAI2。
2. 打开 **Atlas for SAI2**（开始菜单，或安装目录里的 `Atlas.Sai2.exe`）。
3. 面板自动贴到 SAI2 客户区右侧，跟随 SAI2 移动 / 缩放 / 最小化；`Ctrl+Alt+C` 显示或隐藏。
4. 在面板里取色：
   - **中键拖拽**旋转色体、**滚轮**缩放、**左键**在色体/切面上取色、**右键上下拖**改竖直坐标（V / S / L）；
   - 顶部切 `HSV / HVS / OKLCH`，底部 H/S/V（OKLCH 时 H/C/L）滑条与 HEX 输入同步；
   - 点 **应用到 SAI2** 写入前景色；
   - 勾 **实时同步** 后，拖拽取色会以 ≤30Hz 连续写进 SAI2，松手补一次落定值；
   - **读取当前色** 把 SAI2 当前前景色读回面板（SAI2 里用吸管/色轮改色时也会自动跟随）。
5. **贴靠 / 浮动**：贴靠时面板是 SAI2 的附属窗口（不出现在任务栏、随 SAI2 最小化）；浮动时是独立窗口，可拖标题条移动。
6. **紧凑 / 完整**：标题条「完整」把面板拉到 SAI2 客户区同宽（三栏，和原版界面一样）；「紧凑」回到 380 宽单列。两档都能用到全部功能。

## 功能

插件面板 = 原版 Atlas 的全部功能，一个不少：三维色体（HSV / HVS / OKLCH）、V / L 两张读数盘、色板与收藏、历史色带、H/S/V 滑条、L / Y 目标值、等感知亮度 / 等灰度切面、RGB / HSV / CMYK / OKLCH 四套数值、HEX、对比度（白底/黑底）、渐变编辑器（含死区、导出 CSS/SVG）、色卡（导入导出 CSS/JSON/GPL/ASE）、保存 3D 视图 PNG、吸色（EyeDropper）、复制 HEX / 链接、操作说明。

窄面板时这些模块按原页面自己的响应式规则纵向排列，滚动即可；点「完整」就是原版三栏排布。

## 它怎么工作

面板本体是 WPF + WebView2，页面就是 Atlas 的 `index.html`（`?embed=1` 时由宿主注入 `embed.css` / `embed.js`，只在页面外面加一条 SAI2 操作条，不隐藏、不移动原页面任何模块；普通打开 `index.html` 完全不受影响）。宿主用 `https://atlas.app` 虚拟主机加载，剪贴板 / EyeDropper / localStorage 都按正常网页工作。页面导出的文件由 `embed.js` 拦截后交给宿主保存（原生保存对话框）。

颜色桥写在 `plugin/host/Sai2Bridge.cs` + `Sai2Memory.cs`：

- **读**：读 SAI2 进程 `.data` 里打包的 `0xAARRGGBB` 前景色；内存不可读时降级为读颜色面板里 R/G/B 数值控件。
- **写**：把颜色换算成 SAI2 自己的 VHSV（`V,S,H` 三个 float），写进 SAI2 的颜色状态，再在 SAI2 里起一个远程线程调用它**自己的**「应用前景色」例程——所以色轮标记、SV 方块、R/G/B 字段、色板、笔刷预览都会像用户手动点选一样刷新。
- 偏移量按 exe SHA-256 白名单校验，版本不符直接拒绝写内存；失败时把 HEX 复制到剪贴板兜底。

细节（RVA、失败模式、压测数据）见 [docs/BRIDGE.md](docs/BRIDGE.md)。

## 已知限制

- 只验证了 `Alpha.2026.10.19 64bit` 这一个 SAI2 版本；换版本后写入会被版本守卫拒绝（面板仍可读色、复制 HEX）。
- 如果 SAI2 以管理员权限运行、面板不是，Windows 会拦跨权限的内存访问，此时只能复制 HEX 到剪贴板。
- 颜色空间坐标沿用 Atlas 的模型；写进 SAI2 的是 8bit sRGB。
- 面板宽度由宿主控制（紧凑 380 / 完整 = SAI2 客户区宽），暂不支持拖边框自由缩放。

## 开发

```
plugin/
├── host/                 C# WPF + WebView2 宿主（Atlas.Sai2.exe）
│   ├── MainWindow.xaml   面板窗口 / 贴靠跟随 / 热键
│   ├── Sai2Bridge.cs     找 SAI2、版本校验、读/写颜色、消息协议
│   ├── Sai2Memory.cs     ReadProcessMemory / WriteProcessMemory / 远程线程
│   └── Native.cs         Win32 封装
├── embed.css / embed.js  注入 index.html 的面板布局与宿主桥
├── build.ps1             构建 → plugin/dist
├── build-portable.ps1    便携版（自包含 exe，页面资源内嵌）→ plugin/portable/*.zip
├── install/              安装 / 卸载脚本
└── docs/                 PANEL.md（嵌入实现）、BRIDGE.md（颜色桥）
```

调试：
- 离线 UI 调试：用浏览器打开 `index.html?embed=1`，`embed.js` 走 mock 宿主；
- 宿主自检：`Atlas.Sai2.exe --get-color` / `--set-color #RRGGBB` 直接输出 JSON（不建窗口）；
- Win32 机制自检：`Atlas.Sai2.exe --selftest-memory` 打开一个临时子进程，验证 OpenProcess / ReadProcessMemory / WriteProcessMemory / CreateRemoteThread 这条链路（用于确认 P/Invoke 声明，不碰 SAI2 内存）；
- `ATLAS_DEBUG=1` 在 exe 同目录写 `Atlas.Sai2.log` 与 `panel-capture.png`（WebView2 截图），`ATLAS_SELFTEST=1` 额外跑一次「宿主 ↔ 面板」消息自检；`ATLAS_OFFSCREEN=1` 把面板放到屏幕外，不抢前台。

### 验证状态

| 项 | 状态 |
| --- | --- |
| 功能完整性 | 原页面全部模块在 380 宽（单列滚动）与 1093 宽（三栏）下都在：逐 id 探针 `missing=0`，未隐藏任何原模块；`localStorage` / `EyeDropper` / `clipboard` / `WebGL` 全部可用 |
| 两档布局 | 面板内「完整 / 紧凑」切换实测（截图见 `docs/panel-preview.png`、`docs/panel-wide.png`） |
| 导出 / 保存图片 | 面板里真实点击「保存图片」→ 宿主写出 `exports\atlas-<hex>.png`（183 KB，可打开）；CSS/JSON/GPL/ASE 走同一通道与原生保存对话框 |
| 面板嵌入布局 / 消息协议 / 贴靠跟随 | 宿主机上离屏实测通过（截图 + 日志）；贴靠时面板贴在 SAI2 客户区右侧 8px |
| 读 SAI2 前景色（内存或窗口字段两条路） | 对运行中的 SAI2 实测通过 |
| 写 SAI2 前景色（Python 参考实现，同一套 RVA/公式） | 200 次/轮 × 4 轮全部成功，界面刷新正常 |
| 写 SAI2 前景色（本 C# 宿主） | 本机实测通过：200 次随机色连写 **200/200**，平均 **2.37 ms**、最大 **5.02 ms**，SAI2 界面同步刷新且进程保持响应；`--set-color` 单次写入与面板「应用到 SAI2」端到端均验证 |

