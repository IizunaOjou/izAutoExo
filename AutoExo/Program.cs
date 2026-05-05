// .NET 6+ WinForms
// csproj に <UseWindowsForms>true</UseWindowsForms> が必要

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AviUtlAutoSubtitleExo
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        TextBox txtVideo = new() { Left = 12, Top = 12, Width = 520 };
        Button btnVideo = new() { Left = 540, Top = 10, Width = 100, Text = "動画選択" };

        TextBox txtModel = new() { Left = 12, Top = 44, Width = 520, Text = "ggml-large-v3.bin" };
        Button btnModel = new() { Left = 540, Top = 42, Width = 100, Text = "モデル選択" };

        Label lblVideoInfo = new() { Left = 12, Top = 72, Width = 520, Text = "動画情報：未取得" };

        NumericUpDown numLayer = new() { Left = 12, Top = 110, Width = 80, Minimum = 1, Maximum = 100, Value = 1 };
        TextBox txtFont = new() { Left = 110, Top = 110, Width = 180, Text = "メイリオ" };
        NumericUpDown numFontSize = new() { Left = 310, Top = 110, Width = 80, Minimum = 8, Maximum = 200, Value = 48 };

        RadioButton rbCpu = new() { Left = 420, Top = 140, Width = 80, Text = "CPU", Checked = false };
        RadioButton rbCuda = new() { Left = 500, Top = 140, Width = 120, Text = "GPU(CUDA)", Checked = true };

        Button btnRun = new() { Left = 12, Top = 170, Width = 160, Height = 36, Text = "字幕EXO生成" };

        TextBox log = new()
        {
            Left = 12,
            Top = 210,
            Width = 640,
            Height = 240,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical
        };

        int videoFps = 30;
        int videoWidth = 1920;
        int videoHeight = 1080;

        public MainForm()
        {
            Text = "izAutoExo mp4動画からAviUtl字幕EXOを生成";
            Width = 680;
            Height = 500;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            Controls.AddRange(new Control[]
            {
                txtVideo, btnVideo,
                txtModel, btnModel,
                lblVideoInfo,
                numLayer, txtFont, numFontSize,
                rbCpu, rbCuda,
                btnRun, log
            });

            Controls.Add(new Label { Left = 12, Top = 94, Width = 90, Text = "字幕レイヤー" });
            Controls.Add(new Label { Left = 110, Top = 94, Width = 90, Text = "フォント名" });
            Controls.Add(new Label { Left = 310, Top = 94, Width = 100, Text = "フォントサイズ" });

            btnVideo.Click += async (_, _) =>
            {
                using var ofd = new OpenFileDialog
                {
                    Filter = "動画ファイル|*.mp4;*.mkv;*.mov;*.avi;*.webm|すべて|*.*"
                };

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    txtVideo.Text = ofd.FileName;
                    await LoadVideoInfoAsync(ofd.FileName);
                }
            };

            btnModel.Click += (_, _) =>
            {
                using var ofd = new OpenFileDialog
                {
                    Filter = "Whisper model|*.bin|すべて|*.*"
                };

                if (ofd.ShowDialog() == DialogResult.OK)
                    txtModel.Text = ofd.FileName;
            };

            btnRun.Click += async (_, _) => await RunAsync();
        }

        async Task LoadVideoInfoAsync(string path)
        {
            try
            {
                string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");

                if (!File.Exists(ffprobe))
                {
                    lblVideoInfo.Text = "動画情報：ffprobe.exeなし（既定値 1920x1080 / 30fps）";
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = "-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate -of default=noprint_wrappers=1 \"" + path + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var p = Process.Start(psi)!;
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();

                int w = 1920;
                int h = 1080;
                int fps = 30;

                foreach (var line in output.Split('\n'))
                {
                    var s = line.Trim();

                    if (s.StartsWith("width="))
                        int.TryParse(s[6..], out w);

                    if (s.StartsWith("height="))
                        int.TryParse(s[7..], out h);

                    if (s.StartsWith("r_frame_rate="))
                        fps = ParseFps(s[13..]);
                }

                videoWidth = w;
                videoHeight = h;
                videoFps = fps;

                lblVideoInfo.Text = $"動画情報：{videoWidth}x{videoHeight} / {videoFps}fps";
            }
            catch
            {
                videoWidth = 1920;
                videoHeight = 1080;
                videoFps = 30;
                lblVideoInfo.Text = "動画情報取得失敗（既定値 1920x1080 / 30fps）";
            }
        }

        int ParseFps(string rate)
        {
            rate = rate.Trim();

            if (rate.Contains('/'))
            {
                var p = rate.Split('/');

                if (p.Length == 2 &&
                    double.TryParse(p[0], out double n) &&
                    double.TryParse(p[1], out double d) &&
                    d != 0)
                {
                    return Math.Max(1, (int)Math.Round(n / d));
                }
            }

            if (double.TryParse(rate, out double f))
                return Math.Max(1, (int)Math.Round(f));

            return 30;
        }

        async Task EnsureModelAsync(string modelPath)
        {
            if (File.Exists(modelPath))
                return;

            var url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin";

            var result = MessageBox.Show(
                "Whisperモデルが見つかりません。\n約3GBのファイルをダウンロードします。よろしいですか？",
                "モデルダウンロード",
                MessageBoxButtons.YesNo
            );

            if (result != DialogResult.Yes)
                throw new Exception("モデルが必要です。");

            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

            using var client = new HttpClient(
                new HttpClientHandler
                {
                    MaxConnectionsPerServer = 8
                }
            );

            client.Timeout = TimeSpan.FromHours(1);

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var fs = new FileStream(modelPath, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buffer = new byte[1024 * 1024];
            long readTotal = 0;
            double lastReported = 0;

            while (true)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) break;

                await fs.WriteAsync(buffer, 0, read);
                readTotal += read;

                if (total.HasValue)
                {
                    double percent = readTotal * 100.0 / total.Value;

                    // ★ 1%以上進んだときだけ表示
                    if (percent - lastReported >= 1.0)
                    {
                        Append($"ダウンロード中... {percent:0}%");
                        lastReported = percent;
                    }
                }
            }

            Append("モデルダウンロード完了");
        }

        async Task RunAsync()
        {
            try
            {
                btnRun.Enabled = false;
                log.Clear();

                string baseDir = AppContext.BaseDirectory;

                string ffmpeg = Path.Combine(baseDir, "ffmpeg.exe");
                string whisperCpu = Path.Combine(baseDir, "whisper-cli.exe");
                string whisperCuda = Path.Combine(baseDir, "whisper-cli_cuda.exe");

                if (!File.Exists(ffmpeg))
                    throw new FileNotFoundException("ffmpeg.exe がありません", ffmpeg);

                if (!File.Exists(whisperCpu))
                    throw new FileNotFoundException("whisper-cli.exe がありません", whisperCpu);

                if (!File.Exists(txtVideo.Text))
                    throw new FileNotFoundException("動画ファイルがありません", txtVideo.Text);

                // ここからモデル処理
                string modelPath = ResolveModelPath(txtModel.Text);

                if (!File.Exists(modelPath))
                {
                    Append("モデルが見つからないためダウンロードします...");
                    await EnsureModelAsync(modelPath);
                }

                if (!File.Exists(modelPath))
                    throw new FileNotFoundException("モデルファイルがありません", modelPath);

                string whisper = whisperCpu;

                if (rbCuda.Checked)
                {
                    if (File.Exists(whisperCuda))
                    {
                        whisper = whisperCuda;
                        Append("Whisper: GPU(CUDA)版を使用");
                    }
                    else
                    {
                        Append("GPU(CUDA)版が見つからないためCPU版を使用します。");
                        whisper = whisperCpu;
                    }
                }
                else
                {
                    Append("Whisper: CPU版を使用");
                }

                string work = Path.Combine(Path.GetTempPath(), "aviutl_sub_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);

                string wav = Path.Combine(work, "audio.wav");
                string outBase = Path.Combine(work, "subtitle");
                string srt = outBase + ".srt";

                Append("音声抽出中...");
                await RunProcess(
                    ffmpeg,
                    $"-y -i \"{txtVideo.Text}\" -vn -ac 1 -ar 16000 -acodec pcm_s16le \"{wav}\""
                );

                Append("Whisperで音声認識中...");

                int threads = Math.Max(1, Environment.ProcessorCount - 1);

                // 精度・安定優先：
                // -bo 1 -bs 1 は使わない
                // beam search は標準設定のまま
                // --no-speech-thold で無音区間の幻聴を軽減
                bool useCuda = rbCuda.Checked && File.Exists(whisperCuda);

                string whisperArgs =
                    $"-m \"{modelPath}\" " +
                    $"-f \"{wav}\" " +
                    "-l ja " +
                    "-osrt " +
                    "--no-speech-thold 0.80 " +
                    "--max-context 0 " +
                    $"-t {threads} ";

                if (!useCuda)
                {
                    whisperArgs += "--no-gpu ";
                }

                whisperArgs += $"-of \"{outBase}\"";

                try
                {
                    await RunProcess(whisper, whisperArgs);
                }
                catch
                {
                    if (whisper != whisperCpu)
                    {
                        Append("GPU(CUDA)版の実行に失敗。CPU版で再実行します。");
                        await RunProcess(whisperCpu, whisperArgs);
                    }
                    else
                    {
                        throw;
                    }
                }

                if (!File.Exists(srt))
                    throw new Exception("SRTが生成されませんでした。whisper-cli のオプションが違う版かもしれません。");

                Append("SRT読み込み中...");
                var caps = SrtReader.Load(srt);

                int before = caps.Count;
                caps = SubtitleFilters.RemoveRepeatedCaptions(caps);
                int after = caps.Count;

                if (before != after)
                    Append($"重複字幕を削除: {before - after}件");

                string exo = Path.Combine(
                    Path.GetDirectoryName(txtVideo.Text)!,
                    Path.GetFileNameWithoutExtension(txtVideo.Text) + "_subtitle.exo"
                );

                var wavData = WavData.Load(wav);

                /*
                Append("終了位置から字幕開始位置を逆算中...");
                int fixedBackCount = SubtitleTimingFixer.FixStartByLookingBackFromEnd(caps, wavData);
                Append($"逆算補正: {fixedBackCount}件");

                Append("字幕開始位置の最終チェック中...");
                int fixedCount = SubtitleTimingFixer.FixEarlyStarts(
                    caps,
                    wavData,
                    maxDelaySec: 60.0
                );
                Append($"開始位置補正: {fixedCount}件");
                */

                Append("文章量ベースで長すぎる字幕を補正中...");
                int fixedByLength = SubtitleLengthFixer.FixTooLongCaptions(caps);
                Append($"文章量補正: {fixedByLength}件");

                AviUtlExoWriter.WriteExo(
                    exo,
                    caps,
                    videoFps,
                    videoWidth,
                    videoHeight,
                    (int)numLayer.Value,
                    txtFont.Text.Trim(),
                    (int)numFontSize.Value
                );

                Append("完了：" + exo);
                MessageBox.Show("EXO生成完了\n" + exo);
            }
            catch (Exception ex)
            {
                Append("ERROR: " + ex.Message);
                MessageBox.Show(ex.Message, "エラー");
            }
            finally
            {
                btnRun.Enabled = true;
            }
        }

        string ResolveModelPath(string input)
        {
            if (Path.IsPathRooted(input))
                return input;

            string baseDir = AppContext.BaseDirectory;

            string direct = Path.Combine(baseDir, input);
            if (File.Exists(direct))
                return direct;

            string modelsDir = Path.Combine(baseDir, "models", input);
            if (File.Exists(modelsDir))
                return modelsDir;

            return Path.GetFullPath(input);
        }

        async Task RunProcess(string exe, string args)
        {
            Append("> " + Path.GetFileName(exe) + " " + args);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) Append(e.Data);
            };

            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) Append(e.Data);
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            await p.WaitForExitAsync();

            if (p.ExitCode != 0)
                throw new Exception($"{Path.GetFileName(exe)} が失敗しました。ExitCode={p.ExitCode}");
        }

        void Append(string s)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Append), s);
                return;
            }

            log.AppendText(s + Environment.NewLine);
        }
    }

    public class Caption
    {
        public double StartSec { get; set; }
        public double EndSec { get; set; }
        public string Text { get; set; } = "";
    }

    public static class SrtReader
    {
        static readonly Regex TimeLine = new(
            @"(?<s>\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(?<e>\d{2}:\d{2}:\d{2},\d{3})",
            RegexOptions.Compiled
        );

        public static List<Caption> Load(string path)
        {
            string srt = File.ReadAllText(path, DetectEncoding(path));
            srt = srt.Replace("\r\n", "\n").Replace("\r", "\n");

            var blocks = srt.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<Caption>();

            foreach (var block in blocks)
            {
                var lines = block.Split('\n');
                int idx = Array.FindIndex(lines, x => TimeLine.IsMatch(x));

                if (idx < 0)
                    continue;

                var m = TimeLine.Match(lines[idx].Trim());

                double start = ParseTime(m.Groups["s"].Value);
                double end = ParseTime(m.Groups["e"].Value);

                if (end <= start)
                    continue;

                string text = string.Join("\n", lines[(idx + 1)..]).Trim();
                text = CleanText(text);
                text = WrapJapanese(text, 18);

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                list.Add(new Caption
                {
                    StartSec = start,
                    EndSec = end,
                    Text = text
                });
            }

            return list;
        }

        static Encoding DetectEncoding(string path)
        {
            var bytes = File.ReadAllBytes(path);

            if (bytes.Length >= 3 &&
                bytes[0] == 0xEF &&
                bytes[1] == 0xBB &&
                bytes[2] == 0xBF)
            {
                return new UTF8Encoding(true);
            }

            return new UTF8Encoding(false);
        }

        static double ParseTime(string s)
        {
            s = s.Trim();

            if (TimeSpan.TryParseExact(
                s,
                @"hh\:mm\:ss\,fff",
                CultureInfo.InvariantCulture,
                out var t))
            {
                return t.TotalSeconds;
            }

            return 0;
        }

        static string CleanText(string text)
        {
            return text
                .Replace("<br>", "\n")
                .Replace("<br/>", "\n")
                .Replace("<br />", "\n")
                .Replace("[BLANK_AUDIO]", "")
                .Replace("(無音)", "")
                .Trim();
        }

        static string WrapJapanese(string text, int maxChars)
        {
            text = text.Replace("\n", "");

            if (text.Length <= maxChars)
                return text;

            var sb = new StringBuilder();
            int count = 0;

            foreach (char c in text)
            {
                sb.Append(c);
                count++;

                if (count >= maxChars && "、。！？!? ".IndexOf(c) >= 0)
                {
                    sb.Append('\n');
                    count = 0;
                }
                else if (count >= maxChars + 6)
                {
                    sb.Append('\n');
                    count = 0;
                }
            }

            return sb.ToString().Trim();
        }
    }

    public static class SubtitleFilters
    {
        public static List<Caption> RemoveRepeatedCaptions(List<Caption> caps)
        {
            var result = new List<Caption>();

            string lastText = "";
            int repeatCount = 0;

            foreach (var c in caps)
            {
                string normalized = Normalize(c.Text);

                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (normalized == lastText)
                {
                    repeatCount++;

                    // 連続同文は1回目だけ残す
                    if (repeatCount >= 1)
                        continue;
                }
                else
                {
                    repeatCount = 0;
                    lastText = normalized;
                }

                result.Add(c);
            }

            return result;
        }

        static string Normalize(string text)
        {
            text = Regex.Replace(text.Trim(), @"\s+", "");
            text = text.Replace("。", "").Replace("、", "");
            text = text.Replace("！", "").Replace("?", "").Replace("？", "");
            return text;
        }
    }

    public class WavData
    {
        public short[] Samples { get; set; } = Array.Empty<short>();
        public int SampleRate { get; set; }

        public static WavData Load(string path)
        {
            var bytes = File.ReadAllBytes(path);

            int sampleRate = BitConverter.ToInt32(bytes, 24);
            int dataOffset = FindDataChunkOffset(bytes);

            int sampleCount = (bytes.Length - dataOffset) / 2;
            short[] samples = new short[sampleCount];

            Buffer.BlockCopy(bytes, dataOffset, samples, 0, sampleCount * 2);

            return new WavData
            {
                Samples = samples,
                SampleRate = sampleRate
            };
        }

        static int FindDataChunkOffset(byte[] bytes)
        {
            for (int i = 12; i < bytes.Length - 8; i++)
            {
                if (bytes[i] == (byte)'d' &&
                    bytes[i + 1] == (byte)'a' &&
                    bytes[i + 2] == (byte)'t' &&
                    bytes[i + 3] == (byte)'a')
                {
                    return i + 8;
                }
            }

            return 44;
        }
    }

    public static class SubtitleLengthFixer
    {
        public static int FixTooLongCaptions(List<Caption> caps)
        {
            int fixedCount = 0;

            foreach (var c in caps)
            {
                string text = NormalizeText(c.Text);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                double currentDuration = c.EndSec - c.StartSec;
                double maxDuration = EstimateMaxDuration(text);

                // 今の表示時間が文章量に対して長すぎるなら、
                // 終了位置から逆算して開始位置を後ろへ詰める
                if (currentDuration > maxDuration)
                {
                    double newStart = c.EndSec - maxDuration;

                    if (newStart > c.StartSec && newStart < c.EndSec)
                    {
                        c.StartSec = newStart;
                        fixedCount++;
                    }
                }

                if (c.EndSec <= c.StartSec)
                    c.EndSec = c.StartSec + 0.5;
            }

            return fixedCount;
        }

        static double EstimateMaxDuration(string text)
        {
            int len = CountJapaneseReadableLength(text);

            // 厳しめ：
            // 日本語 1秒あたり約6文字読める前提
            double seconds = len / 6.0;

            // 最低1.2秒、最大14秒
            seconds = Math.Max(1.2, seconds);
            seconds = Math.Min(14.0, seconds);

            return seconds;
        }

        static int CountJapaneseReadableLength(string text)
        {
            int count = 0;

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                    continue;

                // 句読点は軽めに扱う
                if ("、。,.!?！？「」『』（）()[]【】".IndexOf(c) >= 0)
                    continue;

                count++;
            }

            return count;
        }

        static string NormalizeText(string text)
        {
            return Regex.Replace(text.Trim(), @"\s+", "");
        }
    }

    public static class SubtitleTimingFixer
    {
        public static int FixStartByLookingBackFromEnd(List<Caption> caps, WavData wav)
        {
            int fixedCount = 0;

            foreach (var c in caps)
            {
                double newStart = FindSoundStartBeforeEnd(wav, c.StartSec, c.EndSec);

                if (newStart > c.StartSec && newStart < c.EndSec)
                {
                    c.StartSec = newStart;
                    fixedCount++;
                }
            }

            return fixedCount;
        }

        static double FindSoundStartBeforeEnd(WavData wav, double currentStartSec, double endSec)
        {
            double threshold = GetThreshold(wav);

            double stepSec = 0.02;
            double windowSec = 0.22;

            double searchStartSec = Math.Max(currentStartSec, endSec - 60.0);

            int silentCount = 0;
            int requiredSilent = 10; // 約0.2秒以上の無音が必要

            bool foundSound = false;
            double soundStartCandidate = currentStartSec;

            for (double t = endSec; t >= searchStartSec; t -= stepSec)
            {
                double rms = GetRms(wav, t, windowSec);

                if (rms >= threshold)
                {
                    foundSound = true;
                    silentCount = 0;
                    soundStartCandidate = t;
                }
                else
                {
                    if (foundSound)
                    {
                        silentCount++;

                        if (silentCount >= requiredSilent)
                        {
                            // 無音が十分続いた後なので、その直後を発声開始とみなす
                            return Math.Max(currentStartSec, soundStartCandidate);
                        }
                    }
                }
            }

            return currentStartSec;
        }

        public static int FixEarlyStarts(List<Caption> caps, WavData wav, double maxDelaySec)
        {
            int fixedCount = 0;

            foreach (var c in caps)
            {
                if (HasSoundAt(wav, c.StartSec))
                    continue;

                double fixedStart = FindNextSoundStart(wav, c.StartSec, maxDelaySec);

                if (fixedStart > c.StartSec)
                {
                    c.StartSec = fixedStart;

                    if (c.EndSec <= c.StartSec)
                        c.EndSec = c.StartSec + 0.5;

                    fixedCount++;
                }
            }

            return fixedCount;
        }

        static bool HasSoundAt(WavData wav, double sec)
        {
            double rms = GetRms(wav, sec, 0.18);
            return rms >= GetThreshold(wav);
        }

        static double FindNextSoundStart(WavData wav, double startSec, double maxDelaySec)
        {
            double threshold = GetThreshold(wav);

            double stepSec = 0.03;
            double windowSec = 0.18;

            double endSec = Math.Min(
                startSec + maxDelaySec,
                (double)wav.Samples.Length / wav.SampleRate
            );

            int sustained = 0;
            int required = 8;

            for (double t = startSec; t <= endSec; t += stepSec)
            {
                double rms = GetRms(wav, t, windowSec);

                if (rms >= threshold)
                {
                    sustained++;

                    if (sustained >= required)
                        return Math.Max(startSec, t - stepSec * (required - 1));
                }
                else
                {
                    sustained = 0;
                }
            }

            return startSec;
        }

        static double GetThreshold(WavData wav)
        {
            return 3500.0;
        }

        static double GetRms(WavData wav, double startSec, double durationSec)
        {
            int sr = wav.SampleRate;
            int start = Math.Max(0, (int)(startSec * sr));
            int length = Math.Max(1, (int)(durationSec * sr));
            int end = Math.Min(wav.Samples.Length, start + length);

            if (start >= end)
                return 0;

            double sum = 0;
            int count = 0;

            for (int i = start; i < end; i++)
            {
                double v = wav.Samples[i];
                sum += v * v;
                count++;
            }

            return Math.Sqrt(sum / Math.Max(1, count));
        }
    }

    public static class AviUtlExoWriter
    {
        public static void WriteExo(
            string path,
            List<Caption> caps,
            int fps,
            int w,
            int h,
            int layer,
            string font,
            int size)
        {
            if (string.IsNullOrWhiteSpace(font))
                font = "メイリオ";

            if (size < 8)
                size = 64;

            var sjis = Encoding.GetEncoding(932);
            var sb = new StringBuilder();

            int totalLength = Math.Max(
                1,
                caps.Count == 0
                    ? 1
                    : (int)Math.Ceiling(caps[^1].EndSec * fps) + fps
            );

            sb.AppendLine("[exedit]");
            sb.AppendLine($"width={w}");
            sb.AppendLine($"height={h}");
            sb.AppendLine($"rate={fps}");
            sb.AppendLine("scale=1");
            sb.AppendLine($"length={totalLength}");
            sb.AppendLine("audio_rate=44100");
            sb.AppendLine("audio_ch=2");

            double y = h >= 1080 ? 420.0 : h * 0.4;

            for (int i = 0; i < caps.Count; i++)
            {
                var c = caps[i];

                int start = Math.Max(1, (int)Math.Round(c.StartSec * fps));
                int end = Math.Max(start + 1, (int)Math.Round(c.EndSec * fps));

                sb.AppendLine($"[{i}]");
                sb.AppendLine($"start={start}");
                sb.AppendLine($"end={end}");
                sb.AppendLine($"layer={layer}");
                sb.AppendLine("group=1");
                sb.AppendLine("overlay=1");
                sb.AppendLine("camera=0");

                sb.AppendLine($"[{i}.0]");
                sb.AppendLine("_name=テキスト");
                sb.AppendLine($"サイズ={size}");
                sb.AppendLine("表示速度=0.0");
                sb.AppendLine("文字毎に個別オブジェクト=0");
                sb.AppendLine("移動座標上に表示する=0");
                sb.AppendLine("自動スクロール=0");
                sb.AppendLine("B=0");
                sb.AppendLine("I=0");
                sb.AppendLine("type=4");
                sb.AppendLine("autoadjust=0");
                sb.AppendLine("soft=1");
                sb.AppendLine("monospace=0");
                sb.AppendLine("align=4");
                sb.AppendLine("spacing_x=0");
                sb.AppendLine("spacing_y=0");
                sb.AppendLine("precision=1");
                sb.AppendLine("color=ffffff");
                sb.AppendLine("color2=000000");
                sb.AppendLine($"font={font}");
                sb.AppendLine("text=" + ToExoText(c.Text));

                sb.AppendLine($"[{i}.1]");
                sb.AppendLine("_name=標準描画");
                sb.AppendLine("X=0.0");
                sb.AppendLine($"Y={y:0.0}");
                sb.AppendLine("Z=0.0");
                sb.AppendLine("拡大率=100.00");
                sb.AppendLine("透明度=0.0");
                sb.AppendLine("回転=0.00");
                sb.AppendLine("blend=0");
            }

            File.WriteAllText(path, sb.ToString(), sjis);
        }

        static string ToExoText(string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            string hex = BitConverter.ToString(bytes).Replace("-", "");

            if (hex.Length > 4096)
                hex = hex[..4096];

            return hex.PadRight(4096, '0');
        }
    }
}
