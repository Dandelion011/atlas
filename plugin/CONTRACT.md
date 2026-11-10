# Atlas for SAI2 · 插件分支契约

分支：`feat/sai2-plugin`（来自 Atlas 主仓库）
目标：把 Atlas（三维色彩空间取色器）做成能在 PaintTool SAI Ver.2 里使用的插件面板，取到的颜色直接写进 SAI2 的前景色，SAI2 改色时面板跟随。

## 参考实现（别人做的 SAI2「插件」）

`E:\Download\液化小工具.zip` → LiquifyStudio：**独立进程 + PySide6 窗口**，和 SAI2 之间只靠剪贴板（Ctrl+C / Ctrl+V）交换图像，不注入、不改 SAI2 文件。
本分支沿用这个形态：**独立进程 + WebView2 面板**，不需要改 SAI2 本体。

## 组成

```
Atlas/                        (git 分支 feat/sai2-plugin)
├── index.html                     Atlas 原样保留，不做插件相关修改
├── preview.png / README.md        原样
└── plugin/
    ├── CONTRACT.md                本文件
    ├── embed.css                  嵌入模式的样式（注入到 index.html）
    ├── embed.js                   嵌入模式的补充脚本 + 桥接 JS
    ├── host/                      C# WPF + WebView2 宿主（Atlas.Sai2.exe）
    ├── install/                   install.ps1 / uninstall.ps1 / 启动快捷方式
    └── docs/README.md             安装与使用说明
```

宿主启动后：
1. 找到 SAI2 主窗口（`sflRootWindow`，进程 sai2.exe）。
2. 把面板窗口贴到 SAI2 客户区右侧（跟随 SAI2 移动/缩放/最小化；可脱离成浮动窗口）。
3. WebView2 用 **https 虚拟主机**（`https://atlas.app/` → 安装目录）载入 `index.html?embed=1`，
   注入 `embed.css` + `embed.js`。用 https 虚拟主机是为了拿到安全上下文与稳定来源：
   剪贴板、`EyeDropper`、`localStorage` 都与普通网页一致。
4. 双向颜色桥：读 SAI2 前景色、写 SAI2 前景色。

## 面板尺寸与两档布局

面板默认 **380 宽**（紧凑），宽高跟随 SAI2 客户区；标题条上的「完整」按钮切到 **完整界面**（宽 = SAI2 客户区宽度 − 40，最多 1440），再点「紧凑」切回。

两档布局都渲染**原版 index.html 的全部模块**，没有隐藏功能：

- 紧凑（<940 CSS px）：原页面自带的响应式单列布局 —— 命令栏（吸色/复制/链接/操作说明）、三维视口、V/L 两张读数盘、色板、H/S/V 滑条、L/Y、切面开关、四套数值、HEX、对比度、渐变、色卡、导出，全部在页面里纵向滚动可达。
- 完整（≥940 CSS px）：原版三栏布局 —— 左读数盘 / 中视口 / 右整备栏。

宽度只由宿主决定（`LayoutButton`），页面不再反向改宿主尺寸；`app:resize` 仅用于调试日志。

## JS ↔ 宿主 消息契约

宿主 → JS（`window.chrome.webview.postMessage(obj)`）：

| type | 字段 | 说明 |
|---|---|---|
| `sai2:state` | `attached: bool`, `visible: bool`, `bridge: "ok" \| "unsupported" \| "error"` | 连接状态 |
| `sai2:color` | `hex: "#RRGGBB"` | SAI2 前景色（外部改动时推送） |
| `apply:ack` | `ok: bool`, `hex` | 写色结果 |
| `save:ack` | `ok: bool` | 导出文件落盘结果 |

JS → 宿主：

| type | 字段 | 说明 |
|---|---|---|
| `app:apply` | `hex: "#RRGGBB"`, `live: bool` | 把颜色写进 SAI2（live=true 表示拖拽中的连续更新） |
| `app:pick` | — | 请求宿主立即读一次 SAI2 当前前景色并回推 |
| `app:toggleAttach` | — | 贴靠 / 浮动切换 |
| `app:close` | — | 关闭面板（不关 SAI2） |
| `app:resize` | `width: number` | 面板宽度（仅供调试） |
| `app:save` | `name`, `mime`, `data`(base64) | 页面导出：`embed.js` 拦截 `<a download>`，把字节交给宿主保存 |

导出的 CSS / JSON / GPL / ASE / PNG 都由宿主弹出原生保存对话框（离屏/自动测试模式写到 exe 旁 `exports\`）。

非 WebView2 环境（普通浏览器调试）时，`embed.js` 用 `window.__atlasHostMock` 模拟，方便单独验证 UI。

## 颜色桥（T1 产出）

接口（宿主实现、Python 参考实现同构）：

```
find_sai2() -> {pid, hwnd} | None
read_fg_color(pid) -> "#RRGGBB" | None      # 轮询安全，<1ms
set_fg_color(pid, "#RRGGBB") -> bool        # 写进去并让 SAI2 界面/笔刷预览立刻反映
```

要求：
- 不改 SAI2 安装目录、不改 exe/dll；只做运行时读取（必要时写内存 / 发窗口消息）。
- 对用户正在用的 SAI2 版本（`[PivInk]SAI Ver.2\sai2.exe`，Alpha.2026.10.19 64bit，sha256 `FF227172…A15F`）可用；版本不符时返回 `unsupported` 而不是乱写。
- 测试一律用副本：`F:\SAI2COPY`（不要再动 `F:\Sai\plugin-test\sai2`，它是坏副本）。
- 原始安装 `F:\[PivInk]SAI Ver.2` 只读，任何情况下不写、不启动第二实例冲突。
