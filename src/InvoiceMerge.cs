// 发票拼版 v15：自绘面板版（单文件，两个 DLL 已嵌入 exe；不产生任何日志文件）
// ---------------------------------------------------------------------------
// 【日常用法】双击 exe → 直接打开面板（空状态）→ 拖入 PDF 或点中间区域选文件 → 选专用/普通发票 → 自动生成并打开成品
//            把 1~N 个发票 PDF 或整个文件夹拖到 exe 图标上 → 打开面板（已选状态）→ 选类型
//
// 【静默用法】命令行模式，供脚本/程序调用（此路径零弹窗、零 UI、零文件写入）：
//            InvoiceMerge.exe <发票类型> <PDF路径...>
//              发票类型：0 / 专用发票 / 专票  → 专票模式（每张票上下复制两份，一票一页）
//                        1 / 普通发票 / 普票  → 普票模式（两两配对，上=票1 下=票2；奇数尾张下半留白）
//            InvoiceMerge.exe --version   版本戳（打印到控制台）
//            InvoiceMerge.exe --where     自身路径与 iTextSharp 加载来源（打印到控制台）
//
// 布局与 V4 一致：A4 竖版、边距 28pt、间距 16pt、0.5pt 灰色 4-4 裁切虚线、等比缩放居中
// 输出命名：<源目录>\专票拼版_/普票拼版_<时间戳>.pdf，生成后自动打开
// 退出码：0 成功 / 2 无有效输入（含用户取消） / 3 出错
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using iText = iTextSharp.text;             // 用别名避免与 System.Drawing.Font 等类型冲突
using iTextSharp.text.pdf;

static class Program
{
    const string Ver = "v15";

    [STAThread]
    static int Main(string[] args)
    {
        // 先挂装配解析钩子，再进主流程：保证任何 iTextSharp 类型被 JIT 解析前钩子已生效
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromEmbedded;

        // 静默路径：自检参数，或「类型参数 + 文件」的命令行调用（Quicker 走这里，绝不弹窗）
        if (args.Length >= 1 && (args[0] == "--version" || args[0] == "-v" || args[0] == "--where"))
            return MainCore(args);
        if (args.Length >= 1 && LooksLikeModeArg(args[0]))
            return MainCore(args);

        return RunUi(args);
    }

    // 判断首参数是「发票类型」而不是文件路径：含路径分隔符、以 .pdf/.txt 结尾、真实存在的文件，都不算类型
    static bool LooksLikeModeArg(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s == "0" || s == "1") return true;
        if (s.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.IndexOf('\\') >= 0 || s.IndexOf('/') >= 0) return false;
        try { if (File.Exists(s)) return false; } catch { }
        return IsTypeArg(s);
    }

    // 同目录缺 DLL 时从 exe 内嵌资源加载，实现单文件可移植
    static Assembly ResolveFromEmbedded(object sender, ResolveEventArgs e)
    {
        try
        {
            string want = new AssemblyName(e.Name).Name;
            Assembly self = Assembly.GetExecutingAssembly();
            foreach (string res in self.GetManifestResourceNames())
            {
                if (!res.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Path.GetFileNameWithoutExtension(res), want, StringComparison.OrdinalIgnoreCase))
                    continue;
                using (Stream s = self.GetManifestResourceStream(res))
                {
                    if (s == null) continue;
                    byte[] buf = new byte[s.Length];
                    int off = 0;
                    while (off < buf.Length)
                    {
                        int n = s.Read(buf, off, buf.Length - off);
                        if (n <= 0) break;
                        off += n;
                    }
                    return Assembly.Load(buf);
                }
            }
        }
        catch { }
        return null;
    }

    // ===================== 交互模式（双击 / 拖放） =====================
    static int RunUi(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 拖进来的东西先展开、过滤；空参数（双击）则直接进面板空状态
        List<string> files = new List<string>();
        if (args.Length > 0)
        {
            List<string> collected = CollectFiles(args);
            if (collected.Count == 0)
            {
                MessageBox.Show("拖进来的内容里没有可用的 PDF 文件。\r\n\r\n请直接把发票 PDF（或存放 PDF 的文件夹）拖到本程序图标上。",
                    "发票拼版", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }
            files = ResolveInputPaths(collected.ToArray());
            if (files.Count == 0)
            {
                MessageBox.Show("没有可处理的 PDF。\r\n\r\n以「专票拼版_」「普票拼版_」开头的成品文件会被自动跳过，避免重复拼版。",
                    "发票拼版", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }
        }

        using (var form = new PickForm(files))
        {
            form.ShowDialog();
            return form.ExitCode;
        }
    }

    // 展开拖入的内容：文件夹自动取其中的 PDF；其余原样保留（后续统一做存在性过滤）
    static List<string> CollectFiles(string[] args)
    {
        var list = new List<string>();
        foreach (string a in args)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            string p = a.Trim().Trim('"');
            try
            {
                if (Directory.Exists(p))
                {
                    var found = Directory.GetFiles(p, "*.pdf");
                    Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                    list.AddRange(found);
                }
                else
                {
                    list.Add(p);
                }
            }
            catch { list.Add(p); }
        }
        return list;
    }

    // ===================== 视觉令牌（换肤只改这一块） =====================
    internal static class Skin
    {
        public static Color Accent       = Color.FromArgb(47, 107, 255);   // 主色
        public static Color AccentDeep   = Color.FromArgb(24, 58, 140);    // 卡片标题
        public static Color TextStrong   = Color.FromArgb(20, 46, 110);    // 窗口标题
        public static Color TextBody     = Color.FromArgb(105, 115, 135);  // 正文灰
        public static Color TextMuted    = Color.FromArgb(150, 158, 172);  // 弱化灰
        public static Color TextFaint    = Color.FromArgb(168, 176, 189);  // 提示灰

        public static Color CardFill     = Color.FromArgb(246, 249, 255);
        public static Color CardFillHot  = Color.FromArgb(233, 241, 255);
        public static Color CardLine     = Color.FromArgb(201, 214, 245);
        public static Color CardDisabled = Color.FromArgb(247, 248, 250);
        public static Color CardDisText  = Color.FromArgb(168, 176, 189);
        public static Color CardDisSub   = Color.FromArgb(188, 194, 205);

        public static Color Surface      = Color.White;
        public static Color WindowLine   = Color.FromArgb(227, 232, 242);
        public static Color DropFill     = Color.FromArgb(250, 251, 254);
        public static Color DropLine     = Color.FromArgb(195, 211, 245);
        public static Color BarFill      = Color.FromArgb(242, 246, 254);
        public static Color BarLine      = Color.FromArgb(220, 230, 250);

        public static Color BtnLine      = Color.FromArgb(213, 220, 232);
        public static Color BtnText      = Color.FromArgb(60, 70, 88);
        public static Color BadgeText    = Color.FromArgb(150, 158, 172);
        public static Color ErrorRed     = Color.FromArgb(190, 40, 40);
        public static Color HoverTint    = Color.FromArgb(240, 244, 251);

        public static Color GhRest       = Color.FromArgb(110, 120, 140);  // GitHub 图标默认
        public static Color GhHoverBg    = Color.FromArgb(234, 240, 255);  // GitHub 图标悬停底
        public static Color TipLine      = Color.FromArgb(221, 227, 239);  // 悬停气泡描边

        public static int Radius = 12;     // 窗口圆角
    }

    // ===================== 绘图基元 =====================
    internal static class Draw
    {
        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            if (radius <= 0.1f) { p.AddRectangle(r); return p; }
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color fill)
        {
            using (GraphicsPath p = RoundRect(r, radius))
            using (var b = new SolidBrush(fill))
                g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float radius, Color line, float width)
        {
            using (GraphicsPath p = RoundRect(r, radius))
            using (var pen = new Pen(line, width))
                g.DrawPath(pen, p);
        }

        // 应用图标：0..64 坐标系手绘，任意尺寸缩放
        public static void AppIcon(Graphics g, RectangleF rr)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(rr.X, rr.Y);
            g.ScaleTransform(rr.Width / 64f, rr.Height / 64f);

            using (GraphicsPath bg = RoundRect(new RectangleF(0, 0, 64, 64), 14f))
            using (var b = new SolidBrush(Skin.Accent))
                g.FillPath(b, bg);

            using (GraphicsPath paper = RoundRect(new RectangleF(12, 7, 40, 50), 3.5f))
            using (var b = new SolidBrush(Color.White))
                g.FillPath(b, paper);

            using (var b = new SolidBrush(Color.FromArgb(228, 236, 255)))
            {
                using (GraphicsPath t1 = RoundRect(new RectangleF(16.5f, 12.5f, 31, 15), 1.8f)) g.FillPath(b, t1);
                using (GraphicsPath t2 = RoundRect(new RectangleF(16.5f, 36.5f, 31, 15), 1.8f)) g.FillPath(b, t2);
            }
            using (var b = new SolidBrush(Color.FromArgb(155, 184, 242)))
            {
                g.FillRectangle(b, 20f, 16.8f, 15f, 2.4f);
                g.FillRectangle(b, 20f, 21.8f, 21f, 2.4f);
                g.FillRectangle(b, 20f, 40.8f, 15f, 2.4f);
                g.FillRectangle(b, 20f, 45.8f, 21f, 2.4f);
            }
            using (var pen = new Pen(Skin.Accent, 2f))
            {
                pen.DashStyle = DashStyle.Custom;
                pen.DashPattern = new float[] { 1.6f, 1.3f };  // 会乘以线宽 → 视觉 3.2/2.6
                g.DrawLine(pen, 13.5f, 32f, 50.5f, 32f);
            }
            using (var b = new SolidBrush(Color.FromArgb(255, 138, 43)))
            using (var pen = new Pen(Color.White, 2.4f))
            {
                g.FillEllipse(b, 42.5f, 37.5f, 19f, 19f);
                g.DrawEllipse(pen, 42.5f, 37.5f, 19f, 19f);
            }
            var bolt = new PointF[]
            {
                new PointF(54.2f, 41.2f), new PointF(47.4f, 48.6f), new PointF(51.5f, 48.6f),
                new PointF(50.2f, 53.4f), new PointF(57.0f, 46.0f), new PointF(52.9f, 46.0f)
            };
            using (var b = new SolidBrush(Color.White))
                g.FillPolygon(b, bolt);

            g.Restore(st);
        }

        // 上传/投放图标：0..24 坐标系
        public static void Inbox(Graphics g, RectangleF rr, Color c)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(rr.X, rr.Y);
            g.ScaleTransform(rr.Width / 24f, rr.Height / 24f);
            using (var pen = new Pen(c, 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLine(pen, 12f, 15.5f, 12f, 4.5f);
                g.DrawLines(pen, new PointF[] { new PointF(7.5f, 9f), new PointF(12f, 4.5f), new PointF(16.5f, 9f) });
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(4f, 15.5f), new PointF(4f, 18.5f),
                    new PointF(4.5f, 19.8f), new PointF(5.8f, 20.5f), new PointF(7f, 20.5f)
                });
                g.DrawLine(pen, 4f, 20.5f, 20f, 20.5f);
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(17f, 20.5f), new PointF(18.2f, 20.5f),
                    new PointF(19.5f, 19.8f), new PointF(20f, 18.5f), new PointF(20f, 15.5f)
                });
            }
            g.Restore(st);
        }

        // 专票图标：竖版票价，0..24
        public static void SpecialIcon(Graphics g, RectangleF rr, Color c)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(rr.X, rr.Y);
            g.ScaleTransform(rr.Width / 24f, rr.Height / 24f);
            using (var pen = new Pen(c, 1.4f))
            using (GraphicsPath p = RoundRect(new RectangleF(4.5f, 2.5f, 15f, 19f), 2.2f))
                g.DrawPath(pen, p);
            using (var pen = new Pen(Color.FromArgb(155, 184, 242), 1.4f))
            {
                g.DrawLine(pen, 8f, 8f, 16f, 8f);
                g.DrawLine(pen, 8f, 12f, 16f, 12f);
                g.DrawLine(pen, 8f, 16f, 13f, 16f);
            }
            g.Restore(st);
        }

        // 普票图标：横版双联，0..24
        public static void NormalIcon(Graphics g, RectangleF rr, Color c)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(rr.X, rr.Y);
            g.ScaleTransform(rr.Width / 24f, rr.Height / 24f);
            using (var pen = new Pen(c, 1.4f))
            using (GraphicsPath p = RoundRect(new RectangleF(2.5f, 5f, 19f, 14f), 2.2f))
                g.DrawPath(pen, p);
            using (var pen = new Pen(Color.FromArgb(155, 184, 242), 1.3f))
            {
                pen.DashStyle = DashStyle.Custom;
                pen.DashPattern = new float[] { 1.6f, 1.4f };
                g.DrawLine(pen, 12f, 5.5f, 12f, 18.5f);
                g.DrawLine(pen, 5f, 9.5f, 9.5f, 9.5f);
                g.DrawLine(pen, 14.5f, 14f, 19f, 14f);
            }
            g.Restore(st);
        }

        // 小 PDF 图标（状态条左侧）
        public static void PdfIcon(Graphics g, RectangleF rr, Color c)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(rr.X, rr.Y);
            g.ScaleTransform(rr.Width / 24f, rr.Height / 24f);
            using (var pen = new Pen(c, 1.6f))
            using (GraphicsPath p = RoundRect(new RectangleF(4.5f, 2.5f, 15f, 19f), 2f))
                g.DrawPath(pen, p);
            using (var pen = new Pen(Color.FromArgb(155, 184, 242), 1.5f))
            {
                g.DrawLine(pen, 8f, 8.5f, 16f, 8.5f);
                g.DrawLine(pen, 8f, 13f, 16f, 13f);
            }
            g.Restore(st);
        }

        public static void Chevron(Graphics g, float cx, float cy, Color c)
        {
            using (var pen = new Pen(c, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[] {
                    new PointF(cx - 2.5f, cy - 4f), new PointF(cx + 2f, cy), new PointF(cx - 2.5f, cy + 4f)
                });
            }
        }

        // GitHub Octocat 标志（官方 16x16 路径转 GDI+ 贝塞尔）
        public static GraphicsPath GithubPath()
        {
            var p = new GraphicsPath();
            p.AddBezier(8f, 0f, 3.58f, 0f, 0f, 3.58f, 0f, 8f);
            p.AddBezier(0f, 8f, 0f, 11.54f, 2.29f, 14.53f, 5.47f, 15.59f);
            p.AddBezier(5.47f, 15.59f, 5.87f, 15.66f, 6.02f, 15.42f, 6.02f, 15.21f);
            p.AddBezier(6.02f, 15.21f, 6.02f, 15.02f, 6.01f, 14.39f, 6.01f, 13.72f);
            p.AddBezier(6.01f, 13.72f, 4f, 14.09f, 3.48f, 13.23f, 3.32f, 12.78f);
            p.AddBezier(3.32f, 12.78f, 3.23f, 12.55f, 2.84f, 11.84f, 2.5f, 11.65f);
            p.AddBezier(2.5f, 11.65f, 2.22f, 11.5f, 1.82f, 11.13f, 2.49f, 11.12f);
            p.AddBezier(2.49f, 11.12f, 3.12f, 11.11f, 3.57f, 11.7f, 3.72f, 11.94f);
            p.AddBezier(3.72f, 11.94f, 4.44f, 13.15f, 5.59f, 12.81f, 6.05f, 12.6f);
            p.AddBezier(6.05f, 12.6f, 6.12f, 12.08f, 6.33f, 11.73f, 6.56f, 11.53f);
            p.AddBezier(6.56f, 11.53f, 4.78f, 11.33f, 2.92f, 10.64f, 2.92f, 7.58f);
            p.AddBezier(2.92f, 7.58f, 2.92f, 6.71f, 3.23f, 5.99f, 3.74f, 5.43f);
            p.AddBezier(3.74f, 5.43f, 3.66f, 5.23f, 3.38f, 4.41f, 3.82f, 3.31f);
            p.AddBezier(3.82f, 3.31f, 3.82f, 3.31f, 4.49f, 3.1f, 6.02f, 4.13f);
            p.AddBezier(6.02f, 4.13f, 6.66f, 3.95f, 7.34f, 3.86f, 8.02f, 3.86f);
            p.AddBezier(8.02f, 3.86f, 8.7f, 3.86f, 9.38f, 3.95f, 10.02f, 4.13f);
            p.AddBezier(10.02f, 4.13f, 11.55f, 3.09f, 12.22f, 3.31f, 12.22f, 3.31f);
            p.AddBezier(12.22f, 3.31f, 12.66f, 4.41f, 12.38f, 5.23f, 12.3f, 5.43f);
            p.AddBezier(12.3f, 5.43f, 12.81f, 5.99f, 13.12f, 6.7f, 13.12f, 7.58f);
            p.AddBezier(13.12f, 7.58f, 13.12f, 10.65f, 11.25f, 11.33f, 9.47f, 11.53f);
            p.AddBezier(9.47f, 11.53f, 9.76f, 11.78f, 10.01f, 12.26f, 10.01f, 13.01f);
            p.AddBezier(10.01f, 13.01f, 10.01f, 14.08f, 10f, 14.94f, 10f, 15.21f);
            p.AddBezier(10f, 15.21f, 10f, 15.42f, 10.15f, 15.67f, 10.55f, 15.59f);
            p.AddBezier(10.55f, 15.59f, 13.806f, 14.491f, 15.999f, 11.437f, 16f, 8f);
            p.AddBezier(16f, 8f, 16f, 3.58f, 12.42f, 0f, 8f, 0f);
            p.CloseFigure();
            return p;
        }

        public static void Github(Graphics g, RectangleF rr, Color c)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(rr.X, rr.Y);
            g.ScaleTransform(rr.Width / 16f, rr.Height / 16f);
            using (GraphicsPath p = GithubPath())
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
            g.Restore(st);
        }
    }

    // ===================== 主面板（整窗自绘） =====================
    class PickForm : Form
    {
        enum Phase { Empty, Ready, Busy, Fail }

        // 布局常量（ClientSize 408 x 384）
        const int CW = 408, CH = 384, PadX = 20;
        const int CardW = CW - PadX * 2;
        const int CardH = 62;
        const string RepoUrl = "https://github.com/conley2322/InvoiceMerge";

        readonly Rectangle _rcClose = new Rectangle(CW - PadX - 24, 16, 24, 24);
        readonly Rectangle _rcGit = new Rectangle(CW - PadX - 56, 16, 24, 24);
        readonly Rectangle _rcDrop = new Rectangle(PadX, 66, CW - PadX * 2, 113);
        readonly Rectangle _rcBar = new Rectangle(PadX, 66, CW - PadX * 2, 44);
        readonly Rectangle _rcCard1 = new Rectangle(PadX, 193, CardW, CardH);
        readonly Rectangle _rcCard2 = new Rectangle(PadX, 265, CardW, CardH);
        readonly Rectangle _rcCancel = new Rectangle(CW - PadX - 74, 340, 74, 28);
        readonly Rectangle _rcTip = new Rectangle(CW - PadX - 244, 48, 244, 52);
        readonly Rectangle _rcTipHit = new Rectangle(CW - PadX - 244, 40, 244, 60);

        Phase _phase = Phase.Empty;
        List<string> _files = new List<string>();
        int _skipped;
        int _hover;                 // 0 无 / 1 投放区 / 2 卡1 / 3 卡2 / 4 取消 / 5 关闭 / 6 GitHub
        bool _dragOver;
        bool _dragging;
        bool _tipOn;                // GitHub 悬停气泡是否显示
        readonly Timer _tipTimer = new Timer();
        Point _dragFrom;
        string _failText = "";
        public int ExitCode = 0;

        public PickForm(List<string> files)
        {
            if (files != null && files.Count > 0) _files = files;
            BuildWindow();
            _phase = _files.Count > 0 ? Phase.Ready : Phase.Empty;
        }

        void BuildWindow()
        {
            Text = "发票拼版";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ShowIcon = false;
            KeyPreview = true;
            AllowDrop = true;
            BackColor = Skin.Surface;
            DoubleBuffered = true;
            Font = new Font("微软雅黑", 9f);
            ClientSize = new Size(CW, CH);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            KeyDown += OnKeyDown;
            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            MouseLeave += delegate { _hover = 0; HideTip(); Invalidate(); };
            DragEnter += OnDragEnter;
            DragLeave += delegate { _dragOver = false; Invalidate(); };
            DragDrop += OnDragDrop;

            _tipTimer.Interval = 400;
            _tipTimer.Tick += delegate
            {
                _tipTimer.Stop();
                _tipOn = true;
                Invalidate();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW：无边框窗也给一点投影
                return cp;
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Win11 上交给系统做圆角；失败（Win10/7）则退回 Region 硬裁剪
            int round = 2;  // DWMWCP_ROUND
            int hr = -1;
            try { hr = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); }
            catch { hr = -1; }
            if (hr != 0)
                Region = new Region(Draw.RoundRect(new RectangleF(0, 0, CW, CH), Skin.Radius));
        }

        // ---------- 状态 ----------
        void SetFiles(List<string> files, int skipped)
        {
            _files = files;
            _skipped = skipped;
            _phase = _files.Count > 0 ? Phase.Ready : Phase.Empty;
            _failText = "";
            Invalidate();
        }

        void AddPaths(string[] paths)
        {
            var raw = new List<string>();
            foreach (string p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    if (Directory.Exists(p))
                    {
                        var found = Directory.GetFiles(p, "*.pdf");
                        Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                        raw.AddRange(found);
                    }
                    else raw.Add(p.Trim().Trim('"'));
                }
                catch { raw.Add(p); }
            }
            int before = raw.Count;
            List<string> ok = Program.ResolveInputPaths(raw.ToArray());
            int skipped = Math.Max(0, before - ok.Count);
            if (ok.Count > 0) SetFiles(ok, skipped);
            else SetFiles(_files, _skipped + skipped);   // 全是无效文件：保留原有选择
        }

        void PickFiles()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要拼版的发票 PDF（可多选）";
                dlg.Filter = "PDF 文件 (*.pdf)|*.pdf";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AddPaths(dlg.FileNames);
            }
        }

        // ---------- 命中 ----------
        int HitTest(Point pt)
        {
            if (_rcClose.Contains(pt)) return 5;
            if (_rcGit.Contains(pt) || (_tipOn && _rcTipHit.Contains(pt))) return 6;
            if (_phase == Phase.Empty && _rcDrop.Contains(pt)) return 1;
            if (_rcBar.Contains(pt) && _phase == Phase.Ready) return 1;
            if (_rcCard1.Contains(pt) && _phase == Phase.Ready) return 2;
            if (_rcCard2.Contains(pt) && _phase == Phase.Ready) return 3;
            if (_rcCancel.Contains(pt) && _phase != Phase.Busy) return 4;
            return 0;
        }

        void HideTip()
        {
            _tipTimer.Stop();
            if (_tipOn) { _tipOn = false; Invalidate(); }
        }

        void OpenRepo()
        {
            try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); }
            catch { }
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging)
            {
                HideTip();
                Location = new Point(Location.X + (e.X - _dragFrom.X), Location.Y + (e.Y - _dragFrom.Y));
                return;
            }
            int h = HitTest(e.Location);
            if (h != _hover)
            {
                if (h == 6)
                {
                    if (!_tipOn && !_tipTimer.Enabled) _tipTimer.Start();
                }
                else HideTip();
                _hover = h;
                Cursor = h == 0 ? Cursors.Default : Cursors.Hand;
                Invalidate();
            }
        }

        void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (HitTest(e.Location) == 0)
            {
                _dragging = true;
                _dragFrom = e.Location;
                return;
            }
            _dragFrom = e.Location;
        }

        void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (_dragging) { _dragging = false; return; }
            int h = HitTest(e.Location);
            if (h == 5) { HideTip(); Close(); return; }
            if (h == 6) { HideTip(); OpenRepo(); return; }
            if (h == 4) { Close(); return; }
            if (h == 1) { PickFiles(); return; }
            if (h == 2) { Run(true); return; }
            if (h == 3) { Run(false); return; }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (_phase != Phase.Ready) return;
            if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1) Run(true);
            else if (e.KeyCode == Keys.D2 || e.KeyCode == Keys.NumPad2) Run(false);
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            HideTip();
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                _dragOver = true;
                Invalidate();
            }
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            _dragOver = false;
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) { Invalidate(); return; }
            var arr = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (arr != null) AddPaths(arr);
            Invalidate();
        }

        // ---------- 主流程 ----------
        void Run(bool special)
        {
            HideTip();
            _phase = Phase.Busy;
            _hover = 0;
            Invalidate();
            Update();
            Application.DoEvents();

            try
            {
                int invoiceCount = 0, outPages = 0;
                byte[] bytes = Program.Build(_files, special, ref invoiceCount, ref outPages);

                if (bytes.Length < 2000)
                    throw new Exception("生成结果仅 " + bytes.Length + " 字节，已阻止写入。");

                int expectPages = special ? invoiceCount : (invoiceCount + 1) / 2;
                using (PdfReader v = new PdfReader(bytes))
                {
                    int n = v.NumberOfPages;
                    v.Close();
                    if (n != expectPages)
                        throw new Exception("自校验失败：预期 " + expectPages + " 页，实际 " + n + " 页。");
                }

                string dir = Path.GetDirectoryName(Path.GetFullPath(_files[0]));
                if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string outPath = Path.Combine(dir,
                    (special ? "专票拼版_" : "普票拼版_") + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf");
                File.WriteAllBytes(outPath, bytes);

                ExitCode = 0;
                try { Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true }); }
                catch { }
                Close();
            }
            catch (Exception ex)
            {
                ExitCode = 3;
                _failText = ex.Message;
                _phase = Phase.Fail;
                Invalidate();
            }
        }

        // ---------- 绘制 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Skin.Surface);

            // 外框
            Draw.Stroke(g, new RectangleF(0.5f, 0.5f, CW - 1f, CH - 1f), Skin.Radius, Skin.WindowLine, 1f);

            DrawHeader(g);

            if (_phase == Phase.Empty) DrawEmpty(g);
            else if (_phase == Phase.Ready) DrawReady(g);
            else if (_phase == Phase.Busy) DrawBusy(g);
            else DrawFail(g);

            if (_phase != Phase.Busy) DrawFooter(g);

            DrawTip(g);   // 最上层
        }

        void DrawHeader(Graphics g)
        {
            Draw.AppIcon(g, new RectangleF(PadX, 18, 34, 34));

            using (var f = new Font("微软雅黑", 11.5f, FontStyle.Bold))
            using (var b = new SolidBrush(Skin.TextStrong))
                g.DrawString("发票拼版", f, b, PadX + 44, 24f);

            // GitHub 图标
            bool ghot = _hover == 6;
            if (ghot) Draw.Fill(g, _rcGit, 6f, Skin.GhHoverBg);
            Draw.Github(g, new RectangleF(_rcGit.X + 3.5f, _rcGit.Y + 3.5f, 17f, 17f),
                ghot ? Skin.Accent : Skin.GhRest);

            // 关闭按钮
            if (_hover == 5)
                Draw.Fill(g, _rcClose, 6f, Skin.HoverTint);
            using (var pen = new Pen(Skin.TextMuted, 1.3f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                float cx = _rcClose.X + _rcClose.Width / 2f, cy = _rcClose.Y + _rcClose.Height / 2f;
                g.DrawLine(pen, cx - 4f, cy - 4f, cx + 4f, cy + 4f);
                g.DrawLine(pen, cx + 4f, cy - 4f, cx - 4f, cy + 4f);
            }
        }

        void DrawEmpty(Graphics g)
        {
            bool hot = _dragOver || _hover == 1;
            Draw.Fill(g, _rcDrop, 10f, hot ? Color.FromArgb(243, 247, 255) : Skin.DropFill);

            using (GraphicsPath p = Draw.RoundRect(_rcDrop, 10f))
            using (var pen = new Pen(hot ? Skin.Accent : Skin.DropLine, 1.5f))
            {
                pen.DashStyle = DashStyle.Custom;
                pen.DashPattern = new float[] { 2.2f, 1.8f };   // 乘线宽 1.5 → 视觉 3.3/2.7
                g.DrawPath(pen, p);
            }

            Draw.Inbox(g, new RectangleF(CW / 2f - 14f, _rcDrop.Y + 20f, 28f, 28f), Skin.Accent);

            using (var f = new Font("微软雅黑", 9.75f, FontStyle.Bold))
            using (var b = new SolidBrush(Skin.AccentDeep))
            {
                StringFormat sf = Center();
                g.DrawString(_dragOver ? "松开即可添加" : "拖入发票 PDF，或点击选择", f, b,
                    new RectangleF(_rcDrop.X, _rcDrop.Y + 56f, _rcDrop.Width, 20f), sf);
            }
            using (var f = new Font("微软雅黑", 8.25f))
            using (var b = new SolidBrush(Skin.TextMuted))
            {
                StringFormat sf = Center();
                g.DrawString("支持多选，也可以直接拖整个文件夹", f, b,
                    new RectangleF(_rcDrop.X, _rcDrop.Y + 78f, _rcDrop.Width, 20f), sf);
            }

            DrawCard(g, _rcCard1, true, false, 1, "专用发票", "每张票上下复制两份，一张票一页");
            DrawCard(g, _rcCard2, false, false, 2, "普通发票", "按顺序两两配对，一页放两张票");
        }

        void DrawReady(Graphics g)
        {
            // 状态条
            bool hot = _hover == 1 || _dragOver;
            Draw.Fill(g, _rcBar, 8f, Skin.BarFill);
            Draw.Stroke(g, _rcBar, 8f, hot ? Skin.Accent : Skin.BarLine, 1f);

            Draw.PdfIcon(g, new RectangleF(_rcBar.X + 12, _rcBar.Y + 14, 16, 16), Skin.Accent);

            string summary = SummaryLine();
            using (var f = new Font("微软雅黑", 8.5f))
            using (var b = new SolidBrush(Skin.AccentDeep))
                g.DrawString(summary, f, b, new RectangleF(_rcBar.X + 36, _rcBar.Y + 15f, _rcBar.Width - 96, 18f));

            using (var f = new Font("微软雅黑", 8.25f))
            using (var b = new SolidBrush(hot ? Skin.Accent : Color.FromArgb(120, 140, 190)))
                g.DrawString("更换", f, b, new RectangleF(_rcBar.Right - 48, _rcBar.Y + 15f, 36, 18f));

            using (var f = new Font("微软雅黑", 9.5f, FontStyle.Bold))
            using (var b = new SolidBrush(Skin.TextStrong))
                g.DrawString("请选择发票类型", f, b, PadX, 122f);

            string tip = _skipped > 0
                ? "已跳过 " + _skipped + " 个（成品文件或非 PDF）"
                : "成品会存到源文件所在文件夹，生成后自动打开";
            using (var f = new Font("微软雅黑", 8.25f))
            using (var b = new SolidBrush(_skipped > 0 ? Color.FromArgb(180, 120, 40) : Skin.TextFaint))
                g.DrawString(tip, f, b, PadX, 146f);

            DrawCard(g, _rcCard1, true, _hover == 2, 1, "专用发票", "每张票上下复制两份，一张票一页");
            DrawCard(g, _rcCard2, false, _hover == 3, 2, "普通发票", "按顺序两两配对，一页放两张票");
        }

        void DrawCard(Graphics g, Rectangle rc, bool special, bool hot, int index, string title, string desc)
        {
            bool on = _phase == Phase.Ready;
            Color fill = !on ? Skin.CardDisabled : (hot ? Skin.CardFillHot : Skin.CardFill);
            Color line = !on ? Color.FromArgb(230, 234, 242) : Skin.CardLine;

            Draw.Fill(g, rc, 9f, fill);
            Draw.Stroke(g, rc, 9f, line, hot ? 2f : 1f);

            RectangleF iconRc = new RectangleF(rc.X + 14, rc.Y + (rc.Height - 26) / 2f, 26, 26);
            if (special) Draw.SpecialIcon(g, iconRc, on ? Skin.Accent : Skin.CardDisSub);
            else Draw.NormalIcon(g, iconRc, on ? Skin.Accent : Skin.CardDisSub);

            RectangleF trc = new RectangleF(rc.X + 52, rc.Y + 12f, rc.Width - 110, 18f);
            using (var f = new Font("微软雅黑", 9.75f, FontStyle.Bold))
            using (var b = new SolidBrush(on ? Skin.AccentDeep : Skin.CardDisText))
                g.DrawString(title, f, b, trc);

            using (var f = new Font("微软雅黑", 8.25f))
            using (var b = new SolidBrush(on ? Color.FromArgb(110, 120, 140) : Skin.CardDisSub))
                g.DrawString(desc, f, b, new RectangleF(rc.X + 52, rc.Y + 34f, rc.Width - 110, 18f));

            // 快捷键徽章
            RectangleF br = new RectangleF(rc.Right - 52, rc.Y + (rc.Height - 22) / 2f, 22, 22);
            if (on) Draw.Fill(g, br, 6f, Color.White);
            Draw.Stroke(g, br, 6f, on ? Skin.CardLine : Color.FromArgb(232, 236, 243), 1f);
            using (var f = new Font("微软雅黑", 8.25f))
            using (var b = new SolidBrush(on ? Skin.Accent : Color.FromArgb(198, 203, 213)))
            {
                StringFormat sf = Center();
                g.DrawString(index.ToString(), f, b, br, sf);
            }

            if (on) Draw.Chevron(g, rc.Right - 20f, rc.Y + rc.Height / 2f, Skin.Accent);
        }

        void DrawBusy(Graphics g)
        {
            using (var f = new Font("微软雅黑", 10.5f, FontStyle.Bold))
            using (var b = new SolidBrush(Skin.TextStrong))
            {
                StringFormat sf = Center();
                g.DrawString("正在生成拼版…", f, b, new RectangleF(PadX, CH / 2f - 30f, CardW, 26f), sf);
            }
            using (var f = new Font("微软雅黑", 8.25f))
            using (var b = new SolidBrush(Skin.TextMuted))
            {
                StringFormat sf = Center();
                g.DrawString("页数较多时需要几秒，请不要关闭窗口", f, b,
                    new RectangleF(PadX, CH / 2f + 2f, CardW, 20f), sf);
            }
        }

        void DrawFail(Graphics g)
        {
            using (var f = new Font("微软雅黑", 9f, FontStyle.Bold))
            using (var b = new SolidBrush(Skin.ErrorRed))
                g.DrawString("处理失败", f, b, PadX, 96f);

            using (var f = new Font("微软雅黑", 8.25f))
            using (var b = new SolidBrush(Skin.TextBody))
                g.DrawString(_failText, f, b, new RectangleF(PadX, 122f, CardW, 180f));
        }

        void DrawFooter(Graphics g)
        {
            string hint;
            if (_phase == Phase.Empty) hint = "添加文件后才能选择发票类型";
            else if (_phase == Phase.Fail) hint = "按 Esc 或点取消关闭";
            else hint = "按 1 / 2 快速选择，Esc 取消";

            using (var f = new Font("微软雅黑", 8.25f))
            using (var b = new SolidBrush(Skin.TextFaint))
                g.DrawString(hint, f, b, PadX, 346f);

            bool hot = _hover == 4;
            Draw.Fill(g, _rcCancel, 6f, hot ? Skin.HoverTint : Color.White);
            Draw.Stroke(g, _rcCancel, 6f, hot ? Color.FromArgb(190, 202, 222) : Skin.BtnLine, 1f);
            using (var f = new Font("微软雅黑", 9f))
            using (var b = new SolidBrush(Skin.BtnText))
            {
                StringFormat sf = Center();
                g.DrawString(_phase == Phase.Fail ? "关闭" : "取消", f, b, _rcCancel, sf);
            }
        }

        void DrawTip(Graphics g)
        {
            if (!_tipOn) return;
            Draw.Fill(g, _rcTip, 8f, Color.White);
            Draw.Stroke(g, _rcTip, 8f, Skin.TipLine, 1f);

            using (var f = new Font("微软雅黑", 9f, FontStyle.Bold))
            using (var b = new SolidBrush(Skin.TextStrong))
                g.DrawString("在 GitHub 上查看源码", f, b, _rcTip.X + 12f, _rcTip.Y + 9f);
            using (var f = new Font("微软雅黑", 8f))
            using (var b = new SolidBrush(Skin.TextMuted))
                g.DrawString("github.com/conley2322/InvoiceMerge", f, b, _rcTip.X + 12f, _rcTip.Y + 28f);

            using (var pen = new Pen(Skin.TextMuted, 1.2f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                float ax = _rcTip.Right - 18f, ay = _rcTip.Y + 17f;
                g.DrawLine(pen, ax - 3.5f, ay + 3.5f, ax + 3f, ay - 3f);
                g.DrawLine(pen, ax - 2.6f, ay - 3.2f, ax + 3.2f, ay - 3.2f);
                g.DrawLine(pen, ax + 3.2f, ay - 3.2f, ax + 3.2f, ay + 2.8f);
            }
        }

        static StringFormat Center()
        {
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            return sf;
        }

        string SummaryLine()
        {
            var names = new List<string>();
            int show = Math.Min(_files.Count, 3);
            for (int i = 0; i < show; i++) names.Add(Path.GetFileName(_files[i]));
            string s = string.Join("、", names.ToArray());
            if (_files.Count > show) s += " 等 " + _files.Count + " 个";
            else if (_files.Count == 1) s = names[0];
            return s;
        }
    }

    // ===================== 静默（命令行 / Quicker）模式 =====================
    static int MainCore(string[] args)
    {
        try
        {
            if (args.Length >= 1 && args[0] == "--where")
            {
                string self = Assembly.GetExecutingAssembly().Location;
                string dep;
                try { dep = typeof(PdfReader).Assembly.Location; }
                catch { dep = "(加载失败)"; }
                Say("WHERE " + Ver);
                Say("exe: " + self);
                Say("iTextSharp 来源: " + dep);
                return 0;
            }

            if (args.Length >= 1 && (args[0] == "--version" || args[0] == "-v"))
            {
                Say("VERSION " + Ver);
                return 0;
            }

            bool specialMode = false;
            string typeArg = "(未指定)";
            var rest = new List<string>(args);

            if (rest.Count >= 1 && IsTypeArg(rest[0]))
            {
                specialMode = IsSpecial(rest[0]);
                typeArg = rest[0].Trim();
                rest.RemoveAt(0);
            }

            List<string> paths = ResolveInputPaths(rest.ToArray());
            if (paths.Count == 0)
            {
                SayError("ERROR 未取得有效的 PDF 输入 | 类型参数=" + typeArg
                    + " | 参数：" + (args.Length == 0 ? "(无)" : string.Join(" | ", args)));
                return 2;
            }

            int invoiceCount = 0;
            int outPages = 0;
            byte[] bytes = Build(paths, specialMode, ref invoiceCount, ref outPages);

            if (bytes.Length < 2000)
                throw new Exception("生成结果仅 " + bytes.Length + " 字节，已阻止写入。");

            int expectPages = specialMode ? invoiceCount : (invoiceCount + 1) / 2;
            using (PdfReader v = new PdfReader(bytes))
            {
                int n = v.NumberOfPages;
                v.Close();
                if (n != expectPages)
                    throw new Exception("自校验失败：预期 " + expectPages + " 页，实际 " + n + " 页。");
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(paths[0]));
            if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string outPath = Path.Combine(dir,
                (specialMode ? "专票拼版_" : "普票拼版_") + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf");
            File.WriteAllBytes(outPath, bytes);

            try { Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true }); }
            catch { }

            Say("OK " + (specialMode ? "专票模式" : "普票模式")
                + " | 输入 " + paths.Count + " 个文件 / " + invoiceCount + " 张票 → 输出 " + outPages + " 页 A4");
            Say(outPath);
            return 0;
        }
        catch (Exception ex)
        {
            SayError("ERROR " + ex.GetType().Name + "：" + ex.Message);
            return 3;
        }
    }

    static bool IsTypeArg(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || s == "1") return true;
        if (s.Contains("专")) return true;      // 专用发票 / 专票
        if (s.Contains("普")) return true;      // 普通发票 / 普票
        return false;
    }

    static bool IsSpecial(string s)
    {
        s = (s ?? "").Trim();
        if (s == "0") return true;
        if (s == "1") return false;
        return s.Contains("专");
    }

    // 命令行模式需要往控制台输出：winexe 不自动继承父控制台，主动挂上去。
    // 双击运行时没有父控制台，挂接失败也没关系，不写任何文件。
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);
    const int AttachParentProcess = -1;
    static bool _consoleTried;

    static void Say(string text)
    {
        try
        {
            if (!_consoleTried) { _consoleTried = true; AttachConsole(AttachParentProcess); }
            Console.WriteLine(text);
        }
        catch { }
    }

    static void SayError(string text)
    {
        try
        {
            if (!_consoleTried) { _consoleTried = true; AttachConsole(AttachParentProcess); }
            Console.Error.WriteLine(text);
        }
        catch { }
    }

    // ===== 构建：special=true 专票复制模式 / false 普票配对模式 =====
    internal static byte[] Build(List<string> paths, bool special, ref int invoiceCount, ref int outPages)
    {
        float pageW = iText.PageSize.A4.Width;
        float pageH = iText.PageSize.A4.Height;
        float margin = 28f;
        float gap = 16f;
        float bandH = (pageH - 2f * margin - gap) / 2f;

        var ms = new MemoryStream();
        iText.Document doc = new iText.Document(iText.PageSize.A4, 0f, 0f, 0f, 0f);
        PdfWriter writer = PdfWriter.GetInstance(doc, ms);
        doc.Open();

        var readers = new List<PdfReader>();
        var pages = new List<KeyValuePair<PdfReader, int>>();
        Exception closeErr = null;
        try
        {
            foreach (string file in paths)
            {
                PdfReader r = new PdfReader(file);
                readers.Add(r);
                for (int p = 1; p <= r.NumberOfPages; p++)
                    pages.Add(new KeyValuePair<PdfReader, int>(r, p));
            }
            invoiceCount = pages.Count;
            if (invoiceCount == 0) throw new Exception("源文件没有可用的页面。");

            if (special)
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    if (outPages > 0) doc.NewPage();
                    PdfContentByte cb = writer.DirectContent;
                    PlaceInBand(cb, writer, pages[i].Key, pages[i].Value, pageW, margin + bandH + gap, margin, bandH);
                    PlaceInBand(cb, writer, pages[i].Key, pages[i].Value, pageW, margin, margin, bandH);
                    DrawCutLine(cb, pageW, margin, bandH, gap);
                    outPages++;
                }
            }
            else
            {
                for (int i = 0; i < pages.Count; i += 2)
                {
                    if (outPages > 0) doc.NewPage();
                    PdfContentByte cb = writer.DirectContent;
                    PlaceInBand(cb, writer, pages[i].Key, pages[i].Value, pageW, margin + bandH + gap, margin, bandH);
                    if (i + 1 < pages.Count)
                        PlaceInBand(cb, writer, pages[i + 1].Key, pages[i + 1].Value, pageW, margin, margin, bandH);
                    DrawCutLine(cb, pageW, margin, bandH, gap);
                    outPages++;
                }
            }
        }
        finally
        {
            try { doc.Close(); }
            catch (Exception ex) { closeErr = ex; }
            foreach (PdfReader r in readers) { try { r.Close(); } catch { } }
        }

        if (closeErr != null)
            throw new Exception("写入 PDF 时出错（writer 关闭阶段）：" + closeErr.Message, closeErr);
        if (outPages == 0)
            throw new Exception("源文件没有可用的页面。");

        return ms.ToArray();
    }

    static void PlaceInBand(PdfContentByte cb, PdfWriter writer, PdfReader reader, int pageNo,
        float pageW, float bandBottomY, float margin, float bandH)
    {
        PdfImportedPage page = writer.GetImportedPage(reader, pageNo);
        float availW = pageW - 2f * margin;
        float scale = Math.Min(availW / page.Width, bandH / page.Height);
        float w = page.Width * scale;
        float h = page.Height * scale;
        float x = (pageW - w) / 2f;
        float y = bandBottomY + (bandH - h) / 2f;
        cb.AddTemplate(page, scale, 0f, 0f, scale, x, y);
    }

    static void DrawCutLine(PdfContentByte cb, float pageW, float margin, float bandH, float gap)
    {
        cb.SaveState();
        cb.SetLineWidth(0.5f);
        cb.SetColorStroke(new iText.BaseColor(180, 180, 180));
        cb.SetLineDash(4f, 4f);
        float ly = margin + bandH + gap / 2f;
        cb.MoveTo(margin / 2f, ly);
        cb.LineTo(pageW - margin / 2f, ly);
        cb.Stroke();
        cb.RestoreState();
    }

    // ===== 输入解析：清单文件 / 直接传 PDF / 默认清单 =====
    internal static List<string> ResolveInputPaths(string[] args)
    {
        var candidates = new List<string>();

        if (args.Length == 0)
        {
            string def = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selected_paths.txt");
            if (File.Exists(def))
            {
                var fi = new FileInfo(def);
                if ((DateTime.Now - fi.LastWriteTime).TotalMinutes <= 10)
                    candidates.Add(def);
            }
        }
        else if (args.Length == 1 && args[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(args[0]);
        }
        else
        {
            foreach (string a in args) candidates.Add(a);
        }

        var paths = new List<string>();
        foreach (string c in candidates)
        {
            if (c.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && File.Exists(c))
            {
                foreach (string line in ReadAllLinesSmart(c))
                {
                    string p = line.Trim().Trim('"');
                    if (p.Length > 0 && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        paths.Add(p);
                }
            }
            else if (c.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(c))
            {
                paths.Add(c);
            }
        }

        // 防呆：跳过本程序自己的成品文件，避免"成品再当输入"
        var final = new List<string>();
        foreach (string p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string n = Path.GetFileName(p);
            if (n.StartsWith("专票拼版_") || n.StartsWith("普票拼版_")) continue;
            final.Add(p);
        }
        return final;
    }

    static string[] ReadAllLinesSmart(string file)
    {
        byte[] raw = File.ReadAllBytes(file);
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        try
        {
            string asUtf8 = Encoding.UTF8.GetString(raw);
            if (!asUtf8.Contains("\uFFFD"))
                return asUtf8.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        }
        catch { }
        return Encoding.Default.GetString(raw)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }
}
