Atlas for SAI2 · 便携版
========================

一个独立小程序：在 SAI2 旁边开一块 Atlas 三维取色面板，颜色直接写进 SAI2 前景色。

【怎么用】

1. 双击 Atlas.Sai2.exe。
2. 先开 SAI2（面板会自动找它并贴到右侧；SAI2 后开也行）。
3. 在面板里取色 → 点「应用到 SAI2」；勾「实时同步」可以边拖边写。
4. Ctrl+Alt+C 显示/隐藏面板；标题条「完整」切原版三栏界面，「紧凑」回 380 宽单列。
5. 面板里点「读取当前色」可把 SAI2 当前前景色读回来；在 SAI2 里改色时面板也会跟着变。

【环境要求】

- Windows 10 / 11 64 位
- WebView2 Runtime（Windows 11 自带；Windows 10 装过 Edge 一般也有。
  没有的话装一次：https://developer.microsoft.com/microsoft-edge/webview2/ ）
- SAI2：PaintTool SAI Ver.2 (64bit) Alpha.2026.10.19

不需要安装 .NET，不需要管理员权限，不写注册表、不改 SAI2 目录。
本程序的数据（WebView2 缓存、窗口宽度）只在 %LOCALAPPDATA%\Atlas.Sai2 下。

【不支持时】

- 面板状态点变灰：当前 SAI2 版本没验证过，可以读色 / 复制 HEX，但不会写内存。
- 状态点变红：写不进去（常见于 SAI2 以管理员运行、Atlas 不是）。此时颜色会复制到剪贴板。
- 想彻底清掉：删掉本目录，再删 %LOCALAPPDATA%\Atlas.Sai2。

【快捷键 / 操作】

- 中键拖拽：旋转色体；滚轮：缩放；左键：在色体或切面上取色；右键上下拖：改竖直坐标
- 顶部 HSV / HVS / OKLCH 切换色彩空间
- H / S / V 滑条与 HEX 输入同步

Atlas 本体是单文件网页应用（index.html），本程序用 WebView2 把它包成面板。
