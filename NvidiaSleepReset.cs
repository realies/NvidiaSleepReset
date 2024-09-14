using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using System.Management;

namespace NvidiaSleepResetApp
{
    public partial class NvidiaSleepReset : Form
    {
        #region Constants and DllImports
        private const int MAX_DATA_POINTS = 60;
        private static readonly Color NVIDIA_GREEN = Color.FromArgb(118, 185, 0);
        private const string NVIDIA_SMI_PATH = @"C:\Windows\System32\nvidia-smi.exe";

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        [FlagsAttribute]
        public enum EXECUTION_STATE : uint
        {
            ES_SYSTEM_REQUIRED = 0x00000001,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_USER_PRESENT = 0x00000004,
            ES_CONTINUOUS = 0x80000000,
            ES_AWAYMODE_REQUIRED = 0x00000040
        }

        #endregion

        #region Private Fields
        private readonly Dictionary<string, List<int>> gpuData = new Dictionary<string, List<int>>();
        private readonly System.Threading.Timer updateTimer;
        private readonly object gpuDataLock = new object();
        private DateTime lastActiveTime = DateTime.MinValue;
        private BufferedGraphics graphicsBuffer;
        private bool isUpdating = false;
        private DateTime lastGpuActivityTime;
        private int activityThresholdSeconds;
        #endregion

        #region Constructor and Main Method
        public NvidiaSleepReset()
        {
            InitializeComponent();
            this.Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            lastActiveTime = DateTime.Now;
            gpuData["GPU 0"] = new List<int>();

            updateTimer = new System.Threading.Timer(UpdateTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            this.Shown += NvidiaSleepReset_Shown;
            UpdateActivityThreshold();
            lastGpuActivityTime = DateTime.Now; // Initialize to current time
        }

        private void NvidiaSleepReset_Shown(object sender, EventArgs e)
        {
            updateTimer.Change(0, Timeout.Infinite);
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new NvidiaSleepReset());
        }
        #endregion

        #region Form Initialization and Event Handlers
        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.Size = new System.Drawing.Size(600, 220);
            this.MinimumSize = new System.Drawing.Size(600, 220);
            this.Text = "NvidiaSleepReset";
            this.BackColor = Color.Black;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.ResizeRedraw = true;
            this.Resize += new EventHandler(this.Form_Resize);
            this.Padding = new Padding(0);

            this.ResumeLayout(false);
            UpdateColors();
        }

        private void Form_Resize(object sender, EventArgs e)
        {
            if (graphicsBuffer != null)
            {
                graphicsBuffer.Dispose();
                graphicsBuffer = null;
            }
            this.Invalidate();
        }

        private void UpdateTimerCallback(object state)
        {
            if (isUpdating) return;

            isUpdating = true;
            try
            {
                if (this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        List<GpuUsageData> currentGpuUsages = UpdateGpuUsage();
                        UpdateActivityStatus(currentGpuUsages);
                        this.Invalidate();
                    });
                }
            }
            finally
            {
                isUpdating = false;
                updateTimer.Change(1000, Timeout.Infinite);
            }
        }
        #endregion

        #region GPU Usage Update and Drawing
        private List<GpuUsageData> UpdateGpuUsage()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = NVIDIA_SMI_PATH,
                        Arguments = "--query-gpu=index,utilization.gpu --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                var gpuUsages = ParseNvidiaSmiOutput(output);

                UpdateGpuData(gpuUsages);
                UpdateSystemSleep(gpuUsages);
                UpdateActivityStatus(gpuUsages);

                return gpuUsages;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating GPU usage: {ex.Message}");
                Debug.WriteLine($"Exception type: {ex.GetType().FullName}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                
                UpdateActivityStatus(new List<GpuUsageData> { new GpuUsageData { Index = "0", Usage = 0 } });
                return new List<GpuUsageData>();
            }
        }

        private List<GpuUsageData> ParseNvidiaSmiOutput(string output)
        {
            return output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(line => line.Split(','))
                         .Where(parts => parts.Length == 2)
                         .Select(parts => new GpuUsageData { Index = parts[0].Trim(), Usage = int.Parse(parts[1].Trim()) })
                         .ToList();
        }

        private void UpdateGpuData(List<GpuUsageData> gpuUsages)
        {
            lock (gpuDataLock)
            {
                foreach (var gpu in gpuUsages)
                {
                    string gpuName = $"GPU {gpu.Index}";
                    if (!gpuData.ContainsKey(gpuName))
                    {
                        gpuData[gpuName] = new List<int>();
                    }
                    gpuData[gpuName].Insert(0, gpu.Usage);
                    if (gpuData[gpuName].Count > MAX_DATA_POINTS)
                    {
                        gpuData[gpuName].RemoveAt(gpuData[gpuName].Count - 1);
                    }
                }

                var currentGpus = gpuUsages.Select(g => $"GPU {g.Index}").ToHashSet();
                foreach (var gpuName in gpuData.Keys.ToList())
                {
                    if (!currentGpus.Contains(gpuName))
                    {
                        gpuData.Remove(gpuName);
                    }
                }
            }
        }

        private void UpdateSystemSleep(List<GpuUsageData> gpuUsages)
        {
            bool isGpuActive = gpuUsages.Any(gpu => gpu.Usage > 0);
            if (isGpuActive)
            {
                lastGpuActivityTime = DateTime.Now;
                ResetSystemIdleTimer();
            }
            else if ((DateTime.Now - lastGpuActivityTime).TotalSeconds < activityThresholdSeconds)
            {
                ResetSystemIdleTimer();
            }
            else
            {
                AllowSystemSleep();
            }
        }

        private void ResetSystemIdleTimer()
        {
            try
            {
                // Reset only the system idle timer, not the display
                SetThreadExecutionState(EXECUTION_STATE.ES_SYSTEM_REQUIRED);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error resetting system idle timer: {ex.Message}");
            }
        }

        private void AllowSystemSleep()
        {
            try
            {
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error allowing system sleep: {ex.Message}");
            }
        }

        private void UpdateActivityStatus(List<GpuUsageData> gpuUsages)
        {
            int currentUsage = gpuUsages.Any() ? gpuUsages.Max(gpu => gpu.Usage) : 0;
            TimeSpan timeSinceLastActivity = DateTime.Now - lastGpuActivityTime;
            string activityStatus;

            if (currentUsage > 0)
            {
                activityStatus = $"Now ({currentUsage}%)";
                lastGpuActivityTime = DateTime.Now;
            }
            else if (timeSinceLastActivity.TotalSeconds < 5)
            {
                activityStatus = "Just now";
            }
            else if (timeSinceLastActivity.TotalMinutes < 1)
            {
                activityStatus = $"{timeSinceLastActivity.Seconds}s ago";
            }
            else if (timeSinceLastActivity.TotalHours < 1)
            {
                activityStatus = $"{(int)timeSinceLastActivity.TotalMinutes}m ago";
            }
            else if (timeSinceLastActivity.TotalDays < 1)
            {
                activityStatus = $"{(int)timeSinceLastActivity.TotalHours}h ago";
            }
            else
            {
                activityStatus = $"{(int)timeSinceLastActivity.TotalDays}d ago";
            }

            this.Text = $"NvidiaSleepReset - GPU: {activityStatus}";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (graphicsBuffer == null || graphicsBuffer.Graphics.VisibleClipBounds.Size != this.ClientSize)
            {
                graphicsBuffer?.Dispose();
                graphicsBuffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, this.ClientRectangle);
            }

            Graphics g = graphicsBuffer.Graphics;
            g.Clear(this.BackColor);

            int leftAxisWidth = 40;
            int bottomMargin = 22;
            int graphHeight = this.ClientSize.Height - bottomMargin;
            int graphWidth = this.ClientSize.Width - leftAxisWidth;

            DrawGraph(g, leftAxisWidth, graphHeight, graphWidth, 0);

            graphicsBuffer.Render(e.Graphics);
        }

        private void DrawGraph(Graphics g, int axisWidth, int height, int width, int topMargin)
        {
            DrawGridAndAxes(g, axisWidth, height, width, topMargin);
            DrawGpuLines(g, axisWidth, height, width, topMargin);
        }

        private void DrawGridAndAxes(Graphics g, int axisWidth, int height, int width, int topMargin)
        {
            Color gridColor = this.BackColor.GetBrightness() > 0.5 ? 
                Color.FromArgb(20, Color.Black) : Color.FromArgb(20, Color.White);
            Color textColor = GetTextColor();

            using (Pen gridPen = new Pen(gridColor, 1))
            using (Pen axisPen = new Pen(textColor, 1))
            using (SolidBrush textBrush = new SolidBrush(textColor))
            {
                // Draw horizontal grid lines and labels
                for (int i = 0; i <= 100; i += 25)
                {
                    float y = height - (i * height / 100f);
                    g.DrawLine(gridPen, axisWidth, y, this.ClientSize.Width, y);

                    string label = $"{i}%";
                    SizeF labelSize = g.MeasureString(label, this.Font);
                    float labelY = i == 100 ? 2 : y - labelSize.Height / 2;
                    
                    g.DrawString(label, this.Font, textBrush, axisWidth - labelSize.Width - 5, labelY);
                    g.DrawLine(axisPen, axisWidth - 3, y, axisWidth, y);
                }

                // Draw vertical grid lines and labels
                for (int i = MAX_DATA_POINTS; i >= 0; i -= 15)
                {
                    float x = axisWidth + ((MAX_DATA_POINTS - i) * width / (float)(MAX_DATA_POINTS - 1));
                    g.DrawLine(gridPen, x, topMargin, x, topMargin + height);

                    if (i % 15 == 0)
                    {
                        string label = i == 0 ? "0s" : $"-{i}s";
                        SizeF labelSize = g.MeasureString(label, this.Font);
                        float labelX = i == 0 ? this.ClientSize.Width - labelSize.Width - 2 
                                              : Math.Min(x - labelSize.Width / 2, this.ClientSize.Width - labelSize.Width);
                        
                        g.DrawString(label, this.Font, textBrush, labelX, this.ClientSize.Height - labelSize.Height - 2);
                        g.DrawLine(axisPen, x, topMargin + height, x, topMargin + height + 3);
                    }
                }

                // Draw axes
                g.DrawLine(axisPen, axisWidth, 0, axisWidth, height);
                g.DrawLine(axisPen, axisWidth, height, this.ClientSize.Width, height);
            }
        }

        private void DrawGpuLines(Graphics g, int axisWidth, int height, int width, int topMargin)
        {
            lock (gpuDataLock)
            {
                // Enable anti-aliasing for smoother lines
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                foreach (var gpu in gpuData)
                {
                    using (Pen pen = new Pen(NVIDIA_GREEN, 2))
                    {
                        var points = new List<PointF>();
                        int dataCount = gpu.Value.Count;

                        for (int i = 0; i < dataCount; i++)
                        {
                            float x = this.ClientSize.Width - (i * width / (float)(MAX_DATA_POINTS - 1));
                            float y = height - (gpu.Value[i] * height / 100f);
                            points.Add(new PointF(x, y));
                        }

                        if (points.Count > 1)
                        {
                            g.DrawLines(pen, points.ToArray());
                        }

                        if (points.Count > 0)
                        {
                            PointF firstPoint = points[0];
                            PointF rightEdgePoint = new PointF(this.ClientSize.Width, firstPoint.Y);
                            g.DrawLine(pen, firstPoint, rightEdgePoint);
                        }
                    }
                }

                // Reset smoothing mode to default for other drawing operations
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            }
        }
        #endregion

        #region Utility Methods
        private void UpdateColors()
        {
            Color textColor = GetTextColor();
            this.ForeColor = textColor;
            this.Invalidate();
        }

        private Color GetTextColor()
        {
            return this.BackColor.GetBrightness() > 0.5 ? Color.Black : Color.White;
        }
        #endregion

        #region Cleanup
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                graphicsBuffer?.Dispose();
                updateTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
        #endregion

        private void UpdateActivityThreshold()
        {
            try
            {
                activityThresholdSeconds = GetSystemIdleTimeout();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting system idle timeout: {ex.Message}");
                activityThresholdSeconds = 300; // Default to 5 minutes if we can't get the system setting
            }
        }

        private int GetSystemIdleTimeout()
        {
            try
            {
                // Get the GUID of the active power scheme
                string output = ExecuteCommand("powercfg", "/getactivescheme");
                string[] parts = output.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    throw new Exception("Unexpected output format from powercfg /getactivescheme");
                }
                string schemeGuid = parts[3];

                // Get the sleep timeout value
                output = ExecuteCommand("powercfg", $"/q {schemeGuid} 238c9fa8-0aad-41ed-83f4-97be242c8f20 29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.Trim().StartsWith("Current AC Power Setting Index:"))
                    {
                        string[] valueParts = line.Split(':');
                        if (valueParts.Length == 2)
                        {
                            string hexValue = valueParts[1].Trim();
                            if (hexValue.StartsWith("0x") && int.TryParse(hexValue.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out int timeout))
                            {
                                return timeout;  // The value is already in seconds
                            }
                        }
                    }
                }

                throw new Exception("Could not find sleep timeout value in powercfg output");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting system idle timeout: {ex.Message}");
                return 300; // Default to 5 minutes if we can't get the system setting
            }
        }

        private string ExecuteCommand(string command, string arguments)
        {
            using (Process process = new Process())
            {
                process.StartInfo.FileName = command;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return output;
            }
        }

        private List<GpuUsageData> GetGpuUsages()
        {
            List<GpuUsageData> gpuUsages = new List<GpuUsageData>();
            try
            {
                string output = ExecuteCommand("nvidia-smi", "--query-gpu=utilization.gpu --format=csv,noheader,nounits");
                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                for (int i = 0; i < lines.Length; i++)
                {
                    if (int.TryParse(lines[i].Trim(), out int usage))
                    {
                        gpuUsages.Add(new GpuUsageData
                        {
                            Index = i.ToString(),
                            Usage = usage
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting GPU usage: {ex.Message}");
            }
            return gpuUsages;
        }

        private class GpuUsageData
        {
            public string Index { get; set; }
            public int Usage { get; set; }
        }
    }
}
