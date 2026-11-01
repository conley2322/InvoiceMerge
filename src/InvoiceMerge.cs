// 发票拼版 CLI v12：单文件版（两个 DLL 已嵌入 exe，运行时不依赖同目录文件）
// 标准用法：InvoiceMerge.exe <发票类型> <PDF路径...>
//   参数1 发票类型：0 = 专用发票（每张票上下复制两份+虚线，多文件合出一份多页 PDF）
//                  1 = 普通发票（按顺序两两配对：上=票1 下=票2+虚线；单张/奇数尾张 → 只放上票，下半留白）
//                  兼容写法：也可传「专用发票」「普通发票」「专票」「普票」等字样，按关键字识别；缺省按普票
//   参数2..N：PDF 完整路径（引号包裹）；首个参数若为 .txt 清单文件亦可
// 自检：InvoiceMerge.exe --version → 写 last_result.txt "VERSION v12"；--where → 打印自身路径与依赖状态
// 零弹窗：成功/失败结果一律写入同目录 last_result.txt，退出代码 0 成功 / 2 无有效输入 / 3 出错
// 布局与 V4 一致：A4 竖版、边距 28pt、间距 16pt、0.5pt 灰色 4-4 裁切虚线、等比缩放居中
// 输出命名：<源目录>\专票拼版_/普票拼版_<时间戳>.pdf
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using iTextSharp.text;
using iTextSharp.text.pdf;

static class Program
{
    const string Ver = "v12";
    static readonly string ResultFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_result.txt");

    // 入口：先把「从嵌入资源加载程序集」的钩子挂上，再调用真正的主流程。
    // 关键点：MainCore 单独成一个方法，JIT 只在调用时才编译它，
    // 保证任何 iTextSharp 类型被解析之前钩子已经生效。
    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromEmbedded;
        return MainCore(args);
    }

    // 同目录缺 DLL 时，从 exe 内部的嵌入资源里加载，实现"单文件可移植"
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

    static int MainCore(string[] args)
    {
        try
        {
            // 诊断：打印自身位置 + 依赖来源（排查"别的电脑跑不起来"时用）
            if (args.Length >= 1 && args[0] == "--where")
            {
                string self = Assembly.GetExecutingAssembly().Location;
                string dep;
                try { dep = typeof(PdfReader).Assembly.Location; }
                catch { dep = "(加载失败)"; }
                WriteResult("WHERE " + Ver + Environment.NewLine
                    + "exe: " + self + Environment.NewLine
                    + "iTextSharp 来源: " + dep);
                return 0;
            }

            // 版本自证：编译后 build.bat 会调用，用来确认跑的是最新 exe
            if (args.Length >= 1 && (args[0] == "--version" || args[0] == "-v"))
            {
                WriteResult("VERSION " + Ver);
                return 0;
            }

            bool specialMode = false;
            string typeArg = "(未指定)";
            var rest = new List<string>(args);

            // 参数1 = 发票类型：数字 0/1，或含「专/普」字样的文本（兼容 Quicker 用户选择的两种输出）
            if (rest.Count >= 1 && IsTypeArg(rest[0]))
            {
                specialMode = IsSpecial(rest[0]);
                typeArg = rest[0].Trim();
                rest.RemoveAt(0);
            }

            List<string> paths = ResolveInputPaths(rest.ToArray());
            if (paths.Count == 0)
            {
                WriteResult("ERROR 未取得有效的 PDF 输入 | 类型参数=" + typeArg
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

            string dir = Path.GetDirectoryName(paths[0]);
            if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string outPath = Path.Combine(dir,
                (specialMode ? "专票拼版_" : "普票拼版_") + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf");
            File.WriteAllBytes(outPath, bytes);

            try { Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true }); }
            catch { }

            WriteResult("OK " + (specialMode ? "专票模式" : "普票模式")
                + " | 类型参数=" + typeArg
                + " | 输入 " + paths.Count + " 个文件 / " + invoiceCount + " 张票 → 输出 " + outPages + " 页 A4"
                + Environment.NewLine + outPath);
            return 0;
        }
        catch (Exception ex)
        {
            WriteResult("ERROR " + ex.GetType().Name + "：" + ex.Message);
            return 3;
        }
    }

    // 参数是否为发票类型标识
    static bool IsTypeArg(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || s == "1") return true;
        if (s.Contains("专")) return true;      // 专用发票 / 专票
        if (s.Contains("普")) return true;      // 普通发票 / 普票
        return false;
    }

    // 是否专票模式（数字 0 或含「专」；缺省/无法识别 → 普票）
    static bool IsSpecial(string s)
    {
        s = (s ?? "").Trim();
        if (s == "0") return true;
        if (s == "1") return false;
        return s.Contains("专");
    }

    // 结果写入 last_result.txt（覆盖），供 Quicker 或人工排查；写入失败不影响主流程
    static void WriteResult(string text)
    {
        try
        {
            File.WriteAllText(ResultFile,
                "[" + Ver + "] [" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text + Environment.NewLine,
                new UTF8Encoding(true));
        }
        catch { }
    }

    // ===== 构建：special=true 专票复制模式 / false 普票配对模式 =====
    static byte[] Build(List<string> paths, bool special, ref int invoiceCount, ref int outPages)
    {
        float pageW = PageSize.A4.Width;
        float pageH = PageSize.A4.Height;
        float margin = 28f;
        float gap = 16f;
        float bandH = (pageH - 2f * margin - gap) / 2f;

        var ms = new MemoryStream();
        Document doc = new Document(PageSize.A4, 0f, 0f, 0f, 0f);
        PdfWriter writer = PdfWriter.GetInstance(doc, ms);
        doc.Open();

        var readers = new List<PdfReader>();
        var pages = new List<KeyValuePair<PdfReader, int>>();   // 展平的"票"序列，按传入顺序
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
                // 专票：每张票一页 A4，上下同票 + 虚线
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
                // 普票：两两配对（上=第1张，下=第2张）+ 虚线
                // 单张 / 奇数尾张：只画上带，下带保持空白（不复制、不留残影）
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
            // 关键顺序：先关 writer（此阶段回读源 PDF 内容），再关全部 reader
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

    // 等比缩放后居中放入指定高度的"带"
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

    // 上下两票之间的灰色裁切虚线（与 Mac 端网页版 / V4 一致）
    static void DrawCutLine(PdfContentByte cb, float pageW, float margin, float bandH, float gap)
    {
        cb.SaveState();
        cb.SetLineWidth(0.5f);
        cb.SetColorStroke(new BaseColor(180, 180, 180));
        cb.SetLineDash(4f, 4f);
        float ly = margin + bandH + gap / 2f;
        cb.MoveTo(margin / 2f, ly);
        cb.LineTo(pageW - margin / 2f, ly);
        cb.Stroke();
        cb.RestoreState();
    }

    // ===== 输入解析：清单文件 / 直接传 PDF / 默认清单 =====
    static List<string> ResolveInputPaths(string[] args)
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
        // 防呆：跳过本程序自己的成品文件，避免"成品再当输入"导致页面上又出现一张票
        var final = new List<string>();
        foreach (string p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string n = Path.GetFileName(p);
            if (n.StartsWith("专票拼版_") || n.StartsWith("普票拼版_")) continue;
            final.Add(p);
        }
        return final;
    }

    // 兼容 UTF-8 BOM / UTF-8 / ANSI（GBK）
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
