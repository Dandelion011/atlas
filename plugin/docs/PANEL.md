# ColorWitch for SAI2 · 面板嵌入说明

`index.html` 原样保留，插件面板靠宿主在页面加载后注入两个文件实现，不需要改 ColorWitch 本体。

```
plugin/
├── embed.css            嵌入模式样式（全部规则挂在 html.cw-embed 下）
├── embed.js             嵌入模式脚本：重排 DOM、底部操作条、宿主消息桥
└── docs/
    ├── PANEL.md         本文件
    └── panel-preview.png 380 × 820 预览
```

![面板预览](panel-preview.png)

## 宿主注入顺序

宿主是 WPF + WebView2，面板固定 380 宽（可调 340–520），高度跟随宿主。**必须等 `NavigationCompleted` 之后再注入**，此时页面脚本已经初始化完毕：

1. 加载 `index.html?embed=1`（`embed=1` 必须带：没有它 embed.js 直接返回，页面保持普通 Atlas 的完整界面）。
2. 往 `document.head` append 一个 `<style id="cw-embed-style">`，内容是 `embed.css` 全文。
3. `ExecuteScriptAsync(embed.js 全文)`。

embed.js 执行时会依次：给 `<html>` 挂 `cw-embed` → 把需要的节点搬进下方面板 → 建底部操作条 → 定义 `window.__cwFromHost` → 派发一次 `window.resize` 让三维视口按改造后的尺寸重排，并立即上报 `app:resize`。

C# 示例：

```csharp
webView.CoreWebView2.NavigationCompleted += async (_, e) =>
{
    if (!e.IsSuccess) return;
    var css = await File.ReadAllTextAsync(Path.Combine(pluginDir, "embed.css"));
    var js  = await File.ReadAllTextAsync(Path.Combine(pluginDir, "embed.js"));

    await webView.CoreWebView2.ExecuteScriptAsync(
        "(function(){var s=document.createElement('style');s.id='cw-embed-style';" +
        "s.textContent=" + JsonSerializer.Serialize(css) + ";" +
        "document.head.appendChild(s);})()");

    await webView.CoreWebView2.ExecuteScriptAsync(js);
};

// 宿主 → JS：把对象序列化后调用 window.__cwFromHost
await webView.CoreWebView2.ExecuteScriptAsync(
    "window.__cwFromHost(" + JsonSerializer.Serialize(payload) + ")");
```

## 消息契约

### JS → 宿主：`window.chrome.webview.postMessage(object)`

| type | 字段 | 时机 |
| --- | --- | --- |
| `app:apply` | `hex: "#RRGGBB"`, `live: bool` | 点「应用到 SAI2」；或「实时同步」勾选后颜色变化（`live:true` 为拖拽中的连续更新，`live:false` 为落定值） |
| `app:pick` | — | 点「读取当前色」，请求宿主回推一次前景色 |
| `app:toggleAttach` | — | 点「贴靠 / 浮动」 |
| `app:resize` | `width: number` | 面板加载完成、窗口宽度变化（200ms 防抖） |

拖拽取色时 `live:true` 限制在 **≤30Hz**（34ms 间隔），松手 / 指针取消 / 窗口失焦时补一条 `live:false`；连续相同的 live 色值不会重复发送。非拖拽的颜色变化（点历史、改 HEX、点滑条）在 160ms 防抖后发一条 `live:false`。

### 宿主 → JS：`window.__cwFromHost(msg)`

`msg` 是已解析对象（也接受 JSON 字符串）。宿主每次调用只传一条：

| type | 字段 | 效果 |
| --- | --- | --- |
| `sai2:state` | `attached: bool`, `visible: bool`, `bridge: "ok" \| "unsupported" \| "error"` | 刷新状态点（墨色 / 灰 / 红）与「贴靠 ↔ 浮动」按钮文字 |
| `sai2:color` | `hex: "#RRGGBB"` | 把面板当前色改成该值（写进 HEX 输入框并触发页面自己的换算，不会回写成 apply） |
| `apply:ack` | `ok: bool`, `hex` | `ok` 时主按钮反色 120ms 作确认 |

> 兼容：如果宿主改用 `PostWebMessageAsJson`，embed.js 也会通过 `chrome.webview` 的 `message` 事件收到同样的对象。

### 非 WebView2 环境（浏览器调试）

没有 `window.chrome.webview` 时，JS→宿主改为 `window.__cwHostMock.postMessage(msg)`：

- 控制台打印 `[ColorWitch → 宿主 mock] type {…}`；
- 维护一个模拟的 SAI2 前景色（默认 `#D9663D`，改 `window.__cwHostMock.color` 即可）；
- `app:apply` 回 `apply:ack`、`app:pick` 回 `sai2:color`、`app:toggleAttach` 回 `sai2:state`；
- 启动时模拟一次宿主握手（`sai2:state {bridge:"ok"}` + 一条 `sai2:color`），所以浏览器里直接打开 `index.html?embed=1` 也能看到完整状态。

## DOM 依赖

embed.js 只依赖 index.html 现有的节点（按 id / class 取，取不到会在控制台告警并保持原页面不动）：

| 用途 | 节点 | 处理 |
| --- | --- | --- |
| 三维视口 | `#stage`、`#gl`、`#path`、`#hud` | 保留；`#stage` 固定 340px 高，随窗口高度可压缩到 220px |
| 视口工具 | `.tools.top`（默认 / 俯视 / 平视 / 网格） | 保留；`.tools.export`（保存图片）隐藏 |
| 空间切换 | `#spHsv`、`#spHvs`、`#spOk` | 保留原样 |
| 顶栏当前色 | `#cmdHex` | 保留；embed.js 用它感知颜色变化 |
| 当前色大色块 | `#swatch`（内含 `#swHex`） | 从右栏移进 `#cwShelf`，成为大色块 |
| HEX 输入 | `.hexrow` → `#hexIn` | 从右栏移进 `#cwShelf` 的 HEX 行 |
| 当前色 / 去色小色块 | `#cmdHex` 色值 + `#swGray` | HEX 行右端两枚 16px 直角色块：新建的当前色块跟着 `#cmdHex` 刷底色，去色那块复用 `#swGray`（隐藏它自带的文字） |
| 历史色带 | `#histRow` | 留在隐藏的 `#panel` 里，不显示；`Ctrl+Z` 撤销仍可用 |
| 三条坐标 | `.sec.first` 里的三个 `.srow`（`#hRange/#hNum`、`#sRange/#sNum`、`#vRange/#vNum`） | 移进 `#cwShelf`；切到 OKLCH 时标签/量程由页面自己换成 H / C / L |
| 左栏读数 | `#rail` | 隐藏（两张俯视图仍按原逻辑小尺寸绘制，不占位） |
| 右栏整备 | `#panel` 其余部分（色板、收藏、L/Y、切面开关、四套数值、对比度、渐变、色卡、导出） | 隐藏 |
| 顶栏按钮 | `吸色 / 复制 / 链接` | 隐藏；`操作说明` 保留 |
| 页面装饰 | `.tri`（顶部三色条） | 隐藏，面板顶部从命令栏开始 |

`index.html` 的结构变了、某个节点取不到时，embed.js 只告警不改造页面（不会留下半成品布局）。

## 交互

- **三维色体**：中键旋转、滚轮缩放、左键取色、右键上下拖改竖直坐标；双击复位；右上角视角预设与网格照旧。
- **当前色区**：整宽大色块（左下角 HEX 随底色翻黑/翻白）；下面一行是 HEX 输入，右端两枚 16px 小色块 —— 左为当前色、右为去色预览，只做对照不可点。
- **底部操作条**（44px，纸面底 + `#D9D9D6` 细线，从左到右均分）：
  - `应用到 SAI2`：荧光蓝填充、白字、右下切角；`apply:ack ok` 时反色 120ms。
  - `读取当前色`：发 `app:pick`，宿主回 `sai2:color`。
  - `实时同步`：勾上后拖拽取色实时写进 SAI2（≤30Hz live，松手补 live:false）；勾选瞬间先同步一次当前色。
  - `贴靠 / 浮动`：发 `app:toggleAttach`，按钮文字跟 `sai2:state.attached` 变。
  - 状态点：只有一枚 6px 圆点，距右边缘 8px —— 已连接荧光蓝 `#1F51FF`、未连接灰 `#D9D9D6`、错误红 `#B3261E`；状态文字放在 `title` 里，不占版面。
- 顶栏：HSV / HVS / OKLCH 切换、16px 当前 HEX、操作说明；空间切换与视口手势全部沿用原页面。

## 宽度适配

- 380 是设计基准；340–369 时底栏收紧间距（8px 内边距、4px 间隙），主按钮略窄，其余弹性。
- 面板宽度 340–520、高度由宿主给：`#deck` 是列向 flex，色块吃掉剩余高度；页面 `overflow:hidden`，任何尺寸都没有横向滚动条。

## 已知限制

- 面板里**没有**：色板收藏、渐变编辑、色卡、L/Y 目标值、等亮度/等灰度切面开关、四套数值、对比度、导出、两张读数图、保存图片、吸色 / 复制 / 链接。要这些功能请直接打开普通 `index.html`。
- 历史色带在面板里隐藏；`Ctrl+Z / ⌘Z` 撤销上一步颜色仍可用（页面自己的快捷键，不经过面板）。
- 需要 WebGL；不可用时页面自己的 `#fatal` 会给出提示。
- 视觉沿用 README 的设计系统（纸面 / 板 / 墨 / 细线 / 荧光蓝，9.5–12px 五档字号，无圆角阴影），不引入外部字体、图标或资源；Saira 字体在 index.html 内嵌。

## 验证

`panel-preview.png` 是 **380 × 820** 整帧截图：用 msedge headless 打开普通 `index.html?embed=1`，按上面的「注入顺序」在同一页面里注入 `embed.css` + `embed.js`（即宿主真实路径），再用 CDP 把视口固定为 380×820 后截图。

> 本机 Chrome / Edge 的 `--headless=new --window-size=380,820` 会被窗口最小宽度限制（实际视口约 514×722），所以改由 CDP `Emulation.setDeviceMetricsOverride` 精确锁定 380×820；渲染引擎与宿主相同（Chromium + SwiftShader WebGL）。

自检过：三维视口正常渲染、大色块 / HEX / 三条坐标 / 底栏齐全；`scrollWidth === clientWidth`、`scrollHeight === clientHeight`（无横/纵向滚动，底栏贴住面板底边）；380×820、380×794、316×800 三种视口都验证过。
