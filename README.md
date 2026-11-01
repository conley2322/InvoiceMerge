# InvoiceMerge · 发票 A4 拼版工具

把电子发票 PDF 拼成 A4 版式：一张 A4 放两张票，中间是一条灰色裁切虚线，打印后沿线裁开就能用。

Windows 单文件工具，纯本地处理，不联网、不上传、不收集任何数据。**把发票 PDF 拖到 exe 图标上就能用**，也保留命令行模式供脚本调用。

---

## 效果

| 模式 | 参数 | 排版规则 |
|---|---|---|
| **专用发票** | `0` | 每张票**上下复制两份**放满一页 A4；多个文件生成一份多页 PDF，每张票占一页 |
| **普通发票** | `1` | 发票按顺序**两两配对**：上 = 第 1 张，下 = 第 2 张；单张或奇数尾张只放上半，下半留白 |

版式参数（与原纸质打印版一致）：

- A4 竖版 595.28 × 841.89 pt
- 上下边距 28 pt，两票间距 16 pt
- 裁切虚线 0.5 pt，RGB(180, 180, 180)，线型 `[4 4]`
- 发票等比缩放、居中，矢量保真（文字可选中，不是截图）

输出文件：`专票拼版_yyyyMMdd_HHmmss.pdf` / `普票拼版_yyyyMMdd_HHmmss.pdf`，自动保存在**第一张发票所在目录**，并自动打开。

---

## 下载

直接取 [release/InvoiceMerge_v15.exe](release/InvoiceMerge_v15.exe)（9.16 MB / 9,609,728 字节）。

- 单文件版：两个依赖 DLL 已嵌入 exe 内部，复制到任何目录双击即可运行，不需要同目录放 DLL。
- 运行环境：Windows + .NET Framework 4.x（Win10/11 自带，无需另外安装）。
- 界面：无边框圆角面板（Win11 走系统圆角 + 投影，Win10 自动回退），按住空白处可拖动；右上角有 GitHub 图标，点击直达本仓库。

> **国内网络下载不下来怎么办？**
> GitHub 的文件直链走 `raw.githubusercontent.com`，在国内经常被阻断。三个办法任选：
> 1. 把仓库 clone 下来，直接取 `release/` 目录里的 exe：
>    `git clone https://github.com/conley2322/InvoiceMerge.git`
> 2. 在直链前面拼一个可用的 GitHub 加速镜像前缀再下载；
> 3. 打开仓库网页 → `release` 目录 → 点文件名 → 页面上点 `Download raw file`，如果这个按钮也没反应，说明是直链被墙，只能用前两种。

---

## 用法

### 方式一：拖拽（推荐日常使用）

1. 把 1 个或多个发票 PDF（或整个文件夹）**直接拖到 `InvoiceMerge.exe` 图标上**；
2. 弹出选择窗，点「专用发票」或「普通发票」两张卡片（也支持键盘 `1` / `2`）；
3. 处理完成后成品自动生成并打开。

双击 exe（不带文件）会打开同一个面板：中间是拖放区，点它可以弹文件选择框，也可以直接把文件（或文件夹）拖进窗口；选好后按需求点卡片或按 `1` / `2`。处理中或出错时窗口内直接显示状态与原因。

### 方式二：命令行 / 脚本调用（静默，零弹窗）

```bat
:: 专用发票：一张票，上下两份，输出 1 页
InvoiceMerge.exe 0 "D:\发票\2501专票.pdf"

:: 专用发票：三张票，输出 3 页
InvoiceMerge.exe 0 "D:\发票\a.pdf" "D:\发票\b.pdf" "D:\发票\c.pdf"

:: 普通发票：四张票，两两配对，输出 2 页
InvoiceMerge.exe 1 "D:\发票\1.pdf" "D:\发票\2.pdf" "D:\发票\3.pdf" "D:\发票\4.pdf"
```

参数说明：

| 参数 | 含义 |
|---|---|
| 第 1 个 | 发票类型：`0` / `专用发票` / `专票` = 专票模式；`1` / `普通发票` / `普票` = 普票模式。按关键字识别，缺省按普票 |
| 第 2..N 个 | PDF 完整路径，**用英文双引号包裹**（路径含空格、中文都能正常处理）。也可以传一个 `.txt` 清单文件，一行一个路径 |

自检命令：

```bat
InvoiceMerge.exe --version    :: 版本戳，打印到控制台
InvoiceMerge.exe --where      :: 自身路径 + iTextSharp 加载来源（来源为空 = 从内存加载，正常）
```

退出码：

| 退出码 | 含义 |
|---|---|
| `0` | 成功 |
| `2` | 没有有效输入（文件不存在，或全部是已拼好的成品） |
| `3` | 处理出错 |

结果只打印到控制台（有父控制台时自动挂接），命令行模式**全程零弹窗**，方便被其他程序（如 Quicker）调用后按退出码判断结果。

**不产生任何日志文件**：程序只在目标目录写一个成品 PDF，不会在你放 exe 或发票的目录里留下 `last_result.txt`、临时文件或配置。

已做防护：文件名以 `专票拼版_` / `普票拼版_` 开头的成品会被自动跳过，避免重复拼版。

---

## 在 Quicker 中使用

配套动作「**一键A4发票拼版**」可在 [Quicker 动作库](https://getquicker.net/Share) 搜索到，纯代码实现、不需要本 exe；本仓库的 exe 适合命令行、批处理或其他程序调用。

Quicker「运行或打开」模块的参数写法：

```
0 "{选中的文件列表}"
```

---

## 自己编译

需要：

1. Windows 自带的 C# 编译器 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（.NET Framework 4.x 附带，无需装 Visual Studio）
2. 两个依赖 DLL，放进仓库根目录的 `lib\` 文件夹（不入库，请自行获取）：
   - `itextsharp.dll` —— NuGet 包 `iTextSharp` 5.5.13.3（或 `iTextSharp-LGPL`）
   - `BouncyCastle.Cryptography.dll` —— NuGet 包 `BouncyCastle.Cryptography` 2.x

然后双击 `build.bat`。编译脚本会自动补 UTF-8 BOM、嵌入两个 DLL、引用系统的 `System.Windows.Forms` / `System.Drawing`（图形界面所需，均为 Windows 自带）、嵌入 `assets\app.ico` 图标（如存在）、跑 `--version` 自检，并把 exe 单独拷到空目录做「无 DLL 运行」验证。

---

## 许可证

本项目以 **GNU AGPL-3.0** 发布，见 [LICENSE](LICENSE)。

原因是依赖的 iTextSharp 5.x 采用 AGPL-3.0：链接该库的程序整体须以 AGPL 授权，分发二进制时必须同时提供完整源码（本仓库已提供）。如果你需要在闭源/商业产品中集成，请改用本项目的另一实现（基于 MIT 许可的 PDFsharp）或向 iText 购买商业许可。

第三方组件：

- [iTextSharp](https://github.com/itext/itextsharp) 5.5.13.3 — AGPL-3.0
- [BouncyCastle](https://www.bouncycastle.org/) — MIT

---

## 免责声明

本工具只做 PDF 版式拼接，不读取、不上传、不保存发票内容到任何外部位置，所有处理均在本机完成。请仅用于处理你合法取得、且有权复制的发票文件。
