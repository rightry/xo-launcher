using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace XO_Launcher.Models
{
    /// <summary>
    /// 单个下载任务。进度状态变化通过 INotifyPropertyChanged 通知消息中心的绑定界面。
    /// 所有属性必须在 UI 线程上更新（由 DownloadService 负责调度）。
    /// </summary>
    public sealed class DownloadTask : INotifyPropertyChanged
    {
        private readonly CancellationTokenSource _cts = new();
        private double _progress;
        private string _status = "等待下载";
        private string _fileName;
        private string _detail = "";
        private long _downloadedBytes;
        private long _totalBytes;
        private string? _progressHint;
        private bool _showSteps;
        private string _stepTitle = "";
        private string _stepLog = "";
        private int _stepIndex;
        private IReadOnlyList<string> _stepNames = Array.Empty<string>();

        public DownloadTask(string title, string fileName)
        {
            Title = title;
            _fileName = fileName;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Title { get; }
        public string? ProjectType { get; set; }
        /// <summary>为 true 时不因本任务自动跳转到消息中心（例如开服页内下载）。</summary>
        public bool SuppressMessageSwitch { get; set; }
        public CancellationToken Token => _cts.Token;

        public string FileName
        {
            get => _fileName;
            set { _fileName = value; OnPropertyChanged(); }
        }

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
        }

        public string ProgressText => $"{Progress:0}%";

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(IsIndeterminate));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanDelete));
            }
        }

        /// <summary>附加信息：完成后的文件路径 / 失败原因等。</summary>
        public string Detail
        {
            get => _detail;
            set { _detail = value; OnPropertyChanged(); }
        }

        public long DownloadedBytes
        {
            get => _downloadedBytes;
            set { _downloadedBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(TransferText)); }
        }

        public long TotalBytes
        {
            get => _totalBytes;
            set { _totalBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(TransferText)); OnPropertyChanged(nameof(IsIndeterminate)); }
        }

        public string TransferText => string.IsNullOrEmpty(ProgressHint)
            ? $"{FormatHelper.FormatBytes(DownloadedBytes)} / {FormatHelper.FormatBytes(TotalBytes)}"
            : ProgressHint;

        public bool IsIndeterminate => TotalBytes <= 0 && Status == "下载中" && string.IsNullOrEmpty(_progressHint);
        public bool IsActive => Status is "下载中" or "等待下载" or "正在取消";
        public bool CanCancel => Status is "下载中" or "等待下载";
        /// <summary>任务已结束（完成/失败/取消）后允许从消息中心删除记录。</summary>
        public bool CanDelete => !IsActive;

        /// <summary>安装类任务用「12 / 40 个文件」替代字节显示。</summary>
        public string? ProgressHint
        {
            get => _progressHint;
            set { _progressHint = value; OnPropertyChanged(); OnPropertyChanged(nameof(TransferText)); OnPropertyChanged(nameof(IsIndeterminate)); }
        }

        public bool ShowSteps
        {
            get => _showSteps;
            private set { _showSteps = value; OnPropertyChanged(); }
        }

        public string StepTitle
        {
            get => _stepTitle;
            private set { _stepTitle = value; OnPropertyChanged(); }
        }

        public string StepLog
        {
            get => _stepLog;
            private set { _stepLog = value; OnPropertyChanged(); }
        }

        public IReadOnlyList<string> StepNames => _stepNames;

        /// <summary>设置安装步骤清单，并标出当前步骤（从 0 起）。current 等于步骤数表示全部完成。</summary>
        public void ApplyStep(IReadOnlyList<string> names, int current, string? extra = null)
        {
            _stepNames = names ?? Array.Empty<string>();
            _stepIndex = current;
            ShowSteps = _stepNames.Count > 0;
            if (_stepNames.Count == 0)
            {
                StepTitle = "";
                StepLog = "";
                return;
            }

            var done = current >= _stepNames.Count;
            StepTitle = done
                ? $"已完成全部 {_stepNames.Count} 个步骤"
                : $"步骤 {current + 1} / {_stepNames.Count}  ·  {_stepNames[current]}";

            var log = new StringBuilder();
            for (var i = 0; i < _stepNames.Count; i++)
            {
                var mark = i < current || done ? "✓" : i == current ? "●" : "○";
                log.Append(mark).Append(' ').Append(_stepNames[i]);
                if (i == current && !done && !string.IsNullOrWhiteSpace(extra))
                    log.Append("  ·  ").Append(extra);
                if (i < _stepNames.Count - 1)
                    log.AppendLine();
            }
            StepLog = log.ToString();
            if (!string.IsNullOrWhiteSpace(extra))
                Detail = extra;
            else if (!done)
                Detail = _stepNames[current];
            ProgressHint = StepTitle;
        }

        public void RefreshStepExtra(string extra)
        {
            if (!ShowSteps || _stepNames.Count == 0) return;
            ApplyStep(_stepNames, _stepIndex, extra);
        }

        public void RequestCancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
