// 发票拼版 v14：拖拽交互版（单文件，两个 DLL 已嵌入 exe；不产生任何日志文件）
// ---------------------------------------------------------------------------
// 【日常用法】把 1~N 个发票 PDF 直接拖到 exe 图标上 → 弹出选择窗 → 选专用/普通发票 → 自动生成并打开成品
//            双击 exe（不带文件）→ 弹出文件选择框，流程相同
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
    const string Ver = "v14";

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

    // ===================== 交互模式（拖放 / 双击） =====================
    static int RunUi(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        List<string> files = CollectFiles(args);

        if (files.Count == 0)
        {
            if (args.Length > 0)
            {
                MessageBox.Show("拖进来的内容里没有可用的 PDF 文件。\r\n\r\n请直接把发票 PDF 拖到本程序图标上。",
                    "发票拼版", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }

            // 双击运行：让用户挑文件
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要拼版的发票 PDF（可多选）";
                dlg.Filter = "PDF 文件 (*.pdf)|*.pdf";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() != DialogResult.OK) return 0;
                files.AddRange(dlg.FileNames);
            }
        }

        files = ResolveInputPaths(files.ToArray());
        if (files.Count == 0)
        {
            MessageBox.Show("没有可处理的 PDF。\r\n\r\n以「专票拼版_」「普票拼版_」开头的成品文件会被自动跳过，避免重复拼版。",
                "发票拼版", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
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

    // ===================== 选择窗 =====================
    class PickForm : Form
    {
        readonly List<string> _files;
        CardPanel _cardSpecial, _cardNormal;
        Button _btnCancel;
        Label _lblTitle, _lblSub, _lblList, _lblStatus;
        public int ExitCode = 0;

        public PickForm(List<string> files)
        {
            _files = files;
            BuildUi();
        }

        void BuildUi()
        {
            Text = "发票拼版";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            BackColor = Color.White;
            Font = new Font("微软雅黑", 9f);
            ClientSize = new Size(408, 300);

            _lblTitle = new Label();
            _lblTitle.Text = "发票拼版";
            _lblTitle.Font = new Font("微软雅黑", 15f, FontStyle.Bold);
            _lblTitle.ForeColor = Color.FromArgb(20, 46, 110);
            _lblTitle.AutoSize = true;
            _lblTitle.Location = new Point(22, 16);
            Controls.Add(_lblTitle);

            _lblSub = new Label();
            _lblSub.Text = "已选择 " + _files.Count + " 个文件，请选择发票类型：";
            _lblSub.Font = new Font("微软雅黑", 9f);
            _lblSub.ForeColor = Color.FromArgb(105, 115, 135);
            _lblSub.AutoSize = true;
            _lblSub.Location = new Point(24, 54);
            Controls.Add(_lblSub);

            _lblList = new Label();
            _lblList.Text = BuildFileSummary();
            _lblList.Font = new Font("微软雅黑", 8f);
            _lblList.ForeColor = Color.FromArgb(150, 158, 172);
            _lblList.AutoSize = false;
            _lblList.Size = new Size(360, 34);
            _lblList.Location = new Point(24, 74);
            Controls.Add(_lblList);

            _cardSpecial = new CardPanel();
            _cardSpecial.Title = "专用发票";
            _cardSpecial.Desc = "每张票上下复制两份，一张票一页";
            _cardSpecial.Location = new Point(16, 112);
            _cardSpecial.Size = new Size(376, 62);
            _cardSpecial.Click += delegate { OnPick(true); };
            Controls.Add(_cardSpecial);

            _cardNormal = new CardPanel();
            _cardNormal.Title = "普通发票";
            _cardNormal.Desc = "按顺序两两配对，一页放两张票";
            _cardNormal.Location = new Point(16, 182);
            _cardNormal.Size = new Size(376, 62);
            _cardNormal.Click += delegate { OnPick(false); };
            Controls.Add(_cardNormal);

            _btnCancel = new Button();
            _btnCancel.Text = "取消";
            _btnCancel.FlatStyle = FlatStyle.System;
            _btnCancel.Size = new Size(84, 28);
            _btnCancel.Location = new Point(308, 254);
            _btnCancel.Click += delegate { Close(); };
            Controls.Add(_btnCancel);

            _lblStatus = new Label();
            _lblStatus.AutoSize = false;
            _lblStatus.Size = new Size(376, 92);
            _lblStatus.Location = new Point(16, 112);
            _lblStatus.TextAlign = ContentAlignment.MiddleCenter;
            _lblStatus.Font = new Font("微软雅黑", 10.5f);
            _lblStatus.ForeColor = Color.FromArgb(20, 46, 110);
            _lblStatus.Visible = false;
            Controls.Add(_lblStatus);

            AcceptButton = null;
            CancelButton = _btnCancel;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (!_cardSpecial.Visible) { if (e.KeyCode == Keys.Escape) Close(); return; }
                if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1) OnPick(true);
                else if (e.KeyCode == Keys.D2 || e.KeyCode == Keys.NumPad2) OnPick(false);
            };
        }

        string BuildFileSummary()
        {
            var names = new List<string>();
            int show = Math.Min(_files.Count, 3);
            for (int i = 0; i < show; i++) names.Add(Path.GetFileName(_files[i]));
            string s = string.Join("、", names.ToArray());
            if (_files.Count > show) s += " …共 " + _files.Count + " 个";
            return s;
        }

        void OnPick(bool special)
        {
            _cardSpecial.Visible = false;
            _cardNormal.Visible = false;
            _btnCancel.Visible = false;
            _lblStatus.Visible = true;
            _lblStatus.ForeColor = Color.FromArgb(20, 46, 110);
            _lblStatus.Text = special ? "正在生成专票拼版…" : "正在生成普票拼版…";
            Application.DoEvents();

            try
            {
                int invoiceCount = 0;
                int outPages = 0;
                byte[] bytes = Build(_files, special, ref invoiceCount, ref outPages);

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

                _lblStatus.ForeColor = Color.FromArgb(190, 40, 40);
                _lblStatus.Font = new Font("微软雅黑", 9f);
                _lblStatus.Text = "处理失败：\r\n\r\n" + ex.Message;

                _btnCancel.Visible = true;
                _btnCancel.Text = "关闭";
                _btnCancel.Location = new Point(308, 254);
            }
        }
    }

    // 可自绘的卡片（整块可点击，鼠标悬停高亮）
    class CardPanel : Panel
    {
        public string Title = "";
        public string Desc = "";
        bool _hover;

        public CardPanel()
        {
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true; Invalidate(); base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false; Invalidate(); base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color fill = _hover ? Color.FromArgb(233, 241, 255) : Color.FromArgb(246, 249, 255);
            Color line = _hover ? Color.FromArgb(31, 79, 216) : Color.FromArgb(201, 214, 245);
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, ClientRectangle);
            using (var pen = new Pen(line, _hover ? 2f : 1f))
                g.DrawRectangle(pen, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

            using (var f1 = new Font("微软雅黑", 11.5f, FontStyle.Bold))
            using (var f2 = new Font("微软雅黑", 8.5f))
            using (var br1 = new SolidBrush(Color.FromArgb(24, 58, 140)))
            using (var br2 = new SolidBrush(Color.FromArgb(110, 120, 140)))
            {
                g.DrawString(Title, f1, br1, 16f, 9f);
                g.DrawString(Desc, f2, br2, 17f, 36f);
            }
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
