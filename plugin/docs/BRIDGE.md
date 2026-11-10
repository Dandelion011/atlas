# 颜色桥 · 实现与逆向记录

面板怎么读、怎么写 SAI2 的前景色，以及这些偏移量是怎么来的。

目标版本：`PaintTool SAI Ver.2 (64bit) Alpha.2026.10.19`
`sai2.exe` SHA-256：`FF22717250618BE0DFD6BB56B23EACE08F6EB2FBF344A66CE9DA41E9857EA15F`

> 所有内存访问都先校验这个 SHA-256；不匹配时写路径直接拒绝（读色与复制 HEX 仍可用），不向未知版本写任何字节。
> 逆向依据是 `F:\Sai\ghidra_out\sai2_all.c`（10,018 个函数的全量反编译）与运行时实测。

## 1. 前景色在 SAI2 里是什么

`.data` 里前景色是一组 float 加一个打包色（RVA，相对 `sai2.exe` 模块基址）：

| RVA | 类型 | 含义 |
| --- | --- | --- |
| `0x47A7F8` | float | **V**（明度 0..1） |
| `0x47A7FC` | float | **S**（饱和度 0..1，SAI2 自己的曲线） |
| `0x47A800` | float | **H**（色相 ÷ 60，0..6） |
| `0x47A804` | float | A（不透明度；setter 会写成 255.0，转换器再钳到 1.0） |
| `0x47A818` | uint32 | 当前前景色 `0xAARRGGBB`（读色与写后校验都用它） |
| `0x47A808/0C/10/14` | float×4 | 背景色（同结构） |
| `0x47A81C` | uint32 | 背景色 `0xAARRGGBB` |
| `0x47B4A7` | uint8 | 颜色面板空间：`0`=V-HSV（默认）、`1`=HSL、`2`=HSV |

SAI2 的颜色不是标准 HSV，而是自定义 **V-HSV**：

```
S_eff = S + 0.5*(V-1)*S²
```

正向（SAI2 `FUN_1402b04f0`，系数表在 `.rdata 0x3E74A8`）：

```
C      = frac(H) * S_eff
sector = 6 - int(H)                    // 1..6
R = tab[2*sector]   *S_eff + V + tab[2*sector+1]*C
G = tab[2*sector+4] *S_eff + V + tab[2*sector+5]*C
B = tab[2*sector+8] *S_eff + V + tab[2*sector+9]*C
结果钳到 0..1，再 ROUND(x*255)
```

反过来（写色用，`FUN_1402b06c0` 的逆）：

```python
V = max(r,g,b);  chroma = V - min(r,g,b)
H = 标准六段色相，结果在 [0,6)
d = (V-1) * 0.5
S = 0 if chroma≈0 else (sqrt(0.25 + d*chroma) - 0.5) / d   # d==0（V==1）时 S = chroma
```

2000 个随机颜色（含 0/255 边界）正逆 round-trip 全部一致；C# 侧 `Sai2Memory.ToVsh` 与它逐行对应。

## 2. 写：调用 SAI2 自己的 setter

`RVA 0x194FD0` 是 SAI2 的「应用前景色」例程：

```
void __fastcall FUN_140194fd0(float *vsh)     // 参数指向 0x47A7F8
```

它做四件事：

1. 把 3 个 float 写回 `0x47A7F8..0x47A800`，并把 `0x47A804 = 255.0f`；
2. 用当前颜色空间转换器（函数指针 `DAT_14047a7e8`）把 (V,S,H) 转成 RGB，写入 `0x47A818`；
3. 调 `FUN_140194fb0()` 刷新 RGB 数值字段（`FUN_140171c70`）、HSV 数值字段（`FUN_140171d10`）、颜色面板与色板；
4. 调 `FUN_1401f8f00(...)` 触发界面重绘（色轮标记、SV 方块、笔刷预览随之更新）。

因为参数就是全局变量自身的地址，函数先写 A 再自拷贝 3 个 float，所以**只需写 12 字节 {V,S,H}**。

### 写入步骤

```
1. sha256(sai2.exe) == FF227172…7EA15F           不符 → unsupported，直接返回
2. h = OpenProcess(0x043A)                        VM_OPERATION|VM_READ|VM_WRITE|CREATE_THREAD|QUERY_INFORMATION
3. base = 目标进程 sai2.exe 模块基址              Toolhelp Module32FirstW；失败退回 psapi EnumProcessModulesEx
4. *(u8*)(base+0x47B4A7) != 0                     → 颜色面板不是默认 V-HSV，拒绝写
5. {V,S,H} = rgb_to_vsh(R,G,B)
6. WriteProcessMemory(base + 0x47A7F8, 12 字节)
7. t = CreateRemoteThread(h, entry=base+0x194FD0, param=base+0x47A7F8)
   WaitForSingleObject(t, 1000); CloseHandle(t)
8. 读回 *(u32*)(base+0x47A818)，低 24 位 == 目标颜色 → 成功
```

要点：

- 远程线程只调用一次普通内部函数，没有 CRT 初始化依赖；**退出码是垃圾值（无返回值），不要用退出码判断成功**，用第 8 步读回校验。
- 一次写 = 一次 `WriteProcessMemory(12B)` + 一次 `CreateRemoteThread`，不需要 `VirtualAllocEx`。
- 句柄 / 版本校验 / 模块基址按 (pid, 进程创建时间) 缓存；否则每次写都重算 5MB sha256，单次会从 1.3ms 退化到 ~6ms。
- 只支持默认的 V-HSV 颜色面板；用户把面板切成 HSL/HSV 时直接拒绝（读色不受影响）。

### 实测（`F:\Sai\plugin-test\bridge\stress200.json`）

| 项目 | 结果 |
| --- | --- |
| 读延时 | 0.005–0.017 ms/次 |
| SAI2 内改色 → 桥读到新值 | 1.1–1.6 ms |
| 30 色写入→读回 | 30/30 |
| 4 轮 × 200 次连写 | 200/200 全成功 |
| 连写耗时 | 平均 1.30–2.43 ms/次，最大 3.7–12.3 ms，整轮 260–486 ms |
| 连写后 SAI2 | 未崩溃/未卡死，`WM_NULL` 有响应，读回 = 最后一次写入 |
| 最小化时写入 | 成功 |
| 面板刷新 | 色轮标记 / SV 方块 / 前景色块 / R-G-B / H-S-V 字段 / 色板 / 笔刷预览全部同步 |

## 3. 读

优先 `ReadProcessMemory(base+0x47A818, 4)`；内存不可读时降级读颜色面板里的隐藏数值控件（父窗口下同级含 `R`/`G`/`B` 标签，标签后第一个 `^\d{1,3}$` 的兄弟窗口就是数值）。两条路在正常环境下一致。

## 4. 失败模式与降级

| 情况 | 表现 | 处理 |
| --- | --- | --- |
| SAI2 未运行 | 面板状态「未运行」 | 看护定时器 150ms 重新找，找到后自动贴靠 |
| exe SHA-256 不匹配 | `unsupported`，写被拒 | 读色/复制 HEX 继续可用 |
| 颜色面板切成 HSL/HSV | 写被拒，状态栏提示切回默认面板 | 读色不受影响 |
| `OpenProcess` 失败（err=5；SAI2 以管理员运行而面板不是，或沙箱/完整性级别不同） | `写入失败：内存桥未打开（OpenProcess failed: 5）` | 复制 HEX 到剪贴板，不写内存 |
| `CreateRemoteThread` 被安全软件拦 | 写失败，读回未变 | 同上 |
| 写后读回不一致 | `写入校验不一致：读回 #xxxxxx` | 同上 |

### 已否决的方案（别再走）

- `WM_SETTEXT` 改 R/G/B 隐藏字段标题：只改显示文本，不提交颜色。
- `PostMessage/SendMessage` 鼠标消息点色轮：无效（面板用真实光标位置/捕获状态）。
- 打开颜色对话框后程序化填值：对话框控件是自绘 `sflChildWindow`，标题为空，`WM_SETTEXT` 无效。
- 只 `WriteProcessMemory` 不调刷新函数：颜色值变了但界面不刷新，下一次 SAI2 自身颜色操作可能基于旧状态。
- 真实鼠标/键盘输入：只能落在色轮像素量化上，做不到精确 `#RRGGBB`，且需要前台窗口。

## 5. C# 移植（plugin/host）

| 文件 | 内容 |
| --- | --- |
| `Sai2Memory.cs` | `OpenProcess` / `ReadProcessMemory` / `WriteProcessMemory` / `CreateRemoteThread` / `CreateToolhelp32Snapshot` / psapi 兜底；`ToVsh()`；颜色模式守卫 |
| `Sai2Bridge.cs` | 找窗口（`sflRootWindow` + 标题前缀 + `sai2.exe`）、SHA-256 版本校验、内存桥生命周期、读/写、失败时复制 HEX |
| `Native.cs` | 窗口/进程路径/热键等 Win32 封装 |
| `MainWindow.xaml.cs` | 面板窗口、贴靠跟随、WebView2 消息协议、150ms 颜色轮询 |

版本守卫常量：`Sai2Bridge.SupportedSha256`。全程 64 位、float 小端。

## 6. 证据与验证状态

- 全量反编译：`F:\Sai\ghidra_out\sai2_all.c`（10,018 函数）。
- Python 参考实现（与本 C# 实现同构）：`F:\Sai\plugin-test\bridge\sai2color.py`；`verify_read.py` / `verify_write.py` 全 PASS。
- 压测：`stress200.json`；写色前后截图：`verify_write_before/after.png`、`evidence_{orange,blue,green}_panel.png`。
- 测试一律用副本 `F:\SAI2COPY`；用户原始安装 `F:\[PivInk]SAI Ver.2` 未被改动。

| 项 | 方式 | 结果 |
| --- | --- | --- |
| RVA / VHSV 换算 | Ghidra 正逆变换 + 2000 随机色 round-trip | 2000/2000 一致 |
| 读路径 | 对运行中的 SAI2 副本：内存读 + 窗口字段交叉校验 | 一致，0.005 ms/次 |
| 写路径 | Python 参考实现对 SAI2 副本 | 4×200 次全成功，界面同步刷新 |
| C# P/Invoke 链路 | `Atlas.Sai2.exe --selftest-memory`（对临时子进程做 OpenProcess / Read/WriteProcessMemory / VirtualAllocEx / CreateRemoteThread） | 全部通过 |
| C# 写 SAI2 | `Atlas.Sai2.exe --set-color` / 面板「应用到 SAI2」/ `--stress 200`（对 SAI2 副本） | 单次写入读回一致；200 次随机色连写 200/200，平均 2.37 ms、最大 5.02 ms，SAI2 界面同步刷新、进程保持响应 |

