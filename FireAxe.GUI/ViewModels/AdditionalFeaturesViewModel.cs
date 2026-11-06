using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;

namespace FireAxe.ViewModels;

public sealed class ReloadEditRow : ViewModelBase
{
    private string _cutText = "0";
    private string _drawCutText = "0";
    public ReloadEditRow(string title, ReloadAnimationAnalysis? analysis, string? error = null, VpkAddon? source = null)
    {
        Title = title; Analysis = analysis; ReadError = error; SourceAddon = source; UpdatePreview();
    }
    public string Title { get; }
    public ReloadAnimationAnalysis? Analysis { get; }
    public string? ReadError { get; }
    public VpkAddon? SourceAddon { get; }
    public bool CanEdit => Analysis is not null && !ReplacementApplied;
    public bool CanEditReload => CanEdit && Analysis!.Clips.Any(clip => clip.Kind == WeaponAnimationKind.Reload);
    public bool CanEditDraw => CanEdit && Analysis!.Clips.Any(clip => clip.Kind == WeaponAnimationKind.Draw);
    public bool ReplacementApplied
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(CanEdit));
            this.RaisePropertyChanged(nameof(CanEditReload)); this.RaisePropertyChanged(nameof(CanEditDraw));
        }
    }
    public string CurrentDuration => Analysis is null ? "读取失败" : string.Join("\n", Analysis.Clips.GroupBy(clip => clip.Kind)
        .Select(group => KindName(group.Key) + " " + Range(group.Select(clip => clip.Seconds).ToArray()) + " s"));
    public string CutText
    {
        get => _cutText;
        set
        {
            if (value == _cutText) return;
            this.RaiseAndSetIfChanged(ref _cutText, value);
            OutputPath = ""; Result = ""; UpdatePreview();
        }
    }
    public string DrawCutText
    {
        get => _drawCutText;
        set
        {
            if (value == _drawCutText) return;
            this.RaiseAndSetIfChanged(ref _drawCutText, value);
            OutputPath = ""; Result = ""; UpdatePreview();
        }
    }
    public string PreviewText { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Details { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Result { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string OutputPath { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool TrySteps(out AnimationTrimStep[] steps, out string? error)
    {
        steps = []; error = ReadError;
        if (Analysis is null) return false;
        var result = new System.Collections.Generic.List<AnimationTrimStep>();
        foreach (var (text, kind) in new[] { (CutText, WeaponAnimationKind.Reload), (DrawCutText, WeaponAnimationKind.Draw) })
        {
            if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value > 0)
            { error = "请输入 0 或负数，例如 -0.5；只支持裁尾。"; return false; }
            if (value < 0) result.Add(new(kind, -value));
        }
        steps = result.ToArray();
        return true;
    }
    private void UpdatePreview()
    {
        if (!TrySteps(out var steps, out var error)) { PreviewText = error ?? "读取失败"; Details = ""; return; }
        Details = string.Join("\n", Analysis!.Clips.Select(clip => $"{KindName(clip.Kind)} · {Path.GetFileName(clip.Model)} · {clip.Name}：{clip.Seconds:0.###} s，{clip.Frames} 帧，{clip.Fps:0.###} FPS"
            + (clip.Unsupported is { } reason ? " · " + reason : "")));
        if (steps.Length == 0) { PreviewText = "未设置裁尾（0：跳过）"; return; }
        try
        {
            var changes = steps.SelectMany(step => ReloadAnimationEditor.Preview(Analysis, step)).ToArray();
            PreviewText = string.Join("\n", changes.GroupBy(change => change.Clip.Kind).Select(group =>
                KindName(group.Key) + "裁后 " + Range(group.Select(change => change.NewSeconds).ToArray()) + " s · 实际裁尾 "
                + Range(group.Select(change => change.ActualCutSeconds).ToArray()) + " s"
                + (group.Sum(change => change.RemovedEvents) is > 0 and var events ? $" · 将移除 {events} 个尾段事件" : "")));
            Details = string.Join("\n", changes.Select(change => $"{KindName(change.Clip.Kind)} · {change.Clip.Name}：{change.Clip.Seconds:0.###} → {change.NewSeconds:0.###} s"
                + $"（实际 -{change.ActualCutSeconds:0.###} s，保留 {change.NewFrames} 帧，移除事件 {change.RemovedEvents}）"));
        }
        catch (Exception ex) { PreviewText = ex.Message; }
    }
    private static string KindName(WeaponAnimationKind kind) => kind == WeaponAnimationKind.Reload ? "换弹" : "切枪";
    private static string Range(double[] values) => Math.Abs(values.Max() - values.Min()) < 0.0005
        ? values[0].ToString("0.###", CultureInfo.InvariantCulture)
        : values.Min().ToString("0.###", CultureInfo.InvariantCulture) + "～" + values.Max().ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed class AdditionalFeaturesViewModel : ViewModelBase, IDisposable
{
    private readonly AddonNodeExplorerViewModel _explorer;
    private readonly IDisposable _invalidHandler;
    private CancellationTokenSource? _operation;
    public AdditionalFeaturesViewModel(AddonNodeExplorerViewModel explorer, string? outputDirectory = null)
    {
        _explorer = explorer;
        OutputDirectory = outputDirectory ?? Path.Combine(AppDataPaths.DataDirectory, "EditedMods");
        _invalidHandler = explorer.AddonRoot.RegisterInvalidHandler(() => CloseRequested?.Invoke());
        ((INotifyCollectionChanged)explorer.SelectedNodes).CollectionChanged += SelectionChanged;
        UpdateSelection();
        var ready = this.WhenAnyValue(model => model.IsBusy).Select(busy => !busy);
        AnalyzeCommand = ReactiveCommand.CreateFromTask(() => Run(async token =>
        {
            var selected = _explorer.SelectedNodes.Select(ModCatalogViewModel.GetVpk).OfType<VpkAddon>().Distinct().ToArray();
            if (selected.Length == 0) { Status = "请先在主页面展开分类与对象，勾选 Mod 左侧的「选择」。"; return; }
            foreach (var row in Rows) row.PropertyChanged -= RowChanged;
            Rows.Clear(); HasRows = false;
            foreach (var addon in selected)
            {
                token.ThrowIfCancellationRequested();
                var title = _explorer.AddonRoot.DownloadHistory.GetDisplayTitle(addon);
                Status = "正在读取：" + title;
                ReloadEditRow row;
                try
                {
                    var path = addon.VpkFilePath ?? throw new IOException("这个 Mod 尚无可读取的 VPK。");
                    var analysis = await ReloadAnimationEditor.AnalyzeAsync(path, token);
                    row = new(title, analysis with { Recipe = analysis.Recipe ?? addon.AnimationEdits }, source: addon);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { row = new(title, null, ex.Message, addon); }
                row.PropertyChanged += RowChanged; Rows.Add(row);
            }
            HasRows = Rows.Count > 0;
            Status = $"已读取 {Rows.Count} 个 Mod。每行可分别填 -0.5、-1.7 等值；0 表示跳过。";
        }), ready);
        GenerateCommand = ReactiveCommand.CreateFromTask(() => Run(async token =>
        {
            var completed = 0; var failed = 0;
            var replace = ReplaceAndEnable;
            var replacements = new System.Collections.Generic.List<VpkAddon>();
            foreach (var row in Rows)
            {
                token.ThrowIfCancellationRequested();
                if (row.ReplacementApplied) { row.Result = "已处理；重新读取当前版本后可继续微调。"; continue; }
                if (!row.TrySteps(out var steps, out var error)) { row.Result = error ?? "无法读取"; failed++; continue; }
                if (steps.Length == 0) { row.Result = "跳过（0）"; continue; }
                row.OutputPath = ""; row.Result = "正在生成…"; Status = "正在生成：" + row.Title;
                try
                {
                    var analysis = row.Analysis!;
                    if (row.SourceAddon is { IsValid: true, VpkFilePath: { } currentPath } source
                        && string.Equals(source.ContentHash, analysis.SourceHash, StringComparison.OrdinalIgnoreCase))
                        analysis = analysis with { SourcePath = currentPath };
                    row.OutputPath = await ReloadAnimationEditor.ExportAsync(analysis, steps, OutputDirectory, token,
                        row.SourceAddon is { IsValid: true } addon ? WorkshopManifest.KnownId(addon) : null);
                    if (replace && row.SourceAddon is { } original)
                    {
                        var result = await _explorer.AddonRoot.Storage.ReplaceEditedModAsync(original, analysis.SourceHash, row.OutputPath, token);
                        row.Result = result.Message; row.ReplacementApplied = true; replacements.Add(result.Mod);
                    }
                    else row.Result = "副本已生成";
                    completed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { row.Result = ex.Message; failed++; }
            }
            if (replacements.Count > 0)
            {
                _explorer.LocateNode(replacements[^1]); _explorer.SelectNodes(replacements);
            }
            Status = replace ? $"已处理并启用 {completed} 个，未完成 {failed} 个；旧版保留并停用。继续微调请重新读取。"
                : $"已生成 {completed} 个副本，未完成 {failed} 个；原 Mod 保留。可导入副本后手动调整开关。";
        }), ready);
        ImportCopiesCommand = ReactiveCommand.CreateFromTask(() => Run(async token =>
        {
            var paths = Rows.Where(row => !row.ReplacementApplied).Select(row => row.OutputPath).Where(File.Exists).ToArray();
            if (paths.Length == 0) { Status = "请先生成副本。"; return; }
            var imported = await _explorer.AddonRoot.Storage.ImportLocalVpkAsync(paths, token: token);
            Status = $"副本导入 {imported.Imported.Length} 个，已有 {imported.Existing.Length} 个，失败 {imported.Errors.Length} 个。"
                + "请停用对应旧版，再点击主页面「应用 Mod」。"
                + (imported.Errors.Length > 0 ? "\n" + string.Join("\n", imported.Errors) : "");
            if ((imported.Imported.LastOrDefault() ?? imported.Existing.LastOrDefault()) is { } last) _explorer.LocateNode(last);
        }), ready);
        CancelCommand = ReactiveCommand.Create(() => _operation?.Cancel());
        ClearSelectionCommand = ReactiveCommand.Create(() => _explorer.SelectNodes([]), ready);
        OpenOutputCommand = ReactiveCommand.Create(() => { if (Directory.Exists(OutputDirectory)) Utils.ShowInFileExplorer(OutputDirectory, true); });
    }
    public KillFeedbackSettings KillFeedback => _explorer.KillFeedback;
    public ObservableCollection<ReloadEditRow> Rows { get; } = [];
    public string OutputDirectory { get; }
    public bool HasRows { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool HasOutputs => Rows.Any(row => File.Exists(row.OutputPath));
    public bool HasImportableOutputs => Rows.Any(row => !row.ReplacementApplied && File.Exists(row.OutputPath));
    public bool ReplaceAndEnable
    {
        get;
        set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(GenerateButtonText)); }
    }
    public string GenerateButtonText => ReplaceAndEnable ? "处理并替换启用" : "批量生成副本";
    public bool IsBusy { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string SelectionText { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string SelectedModNames { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Status { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "尚未读取动画。先选中 Mod，再点击「读取选中 Mod」。";
    public ReactiveCommand<Unit, Unit> AnalyzeCommand { get; }
    public ReactiveCommand<Unit, Unit> GenerateCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCopiesCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearSelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenOutputCommand { get; }
    public event Action? CloseRequested;
    private void SelectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        var selected = _explorer.SelectedNodes.Select(ModCatalogViewModel.GetVpk).OfType<VpkAddon>().Distinct().ToArray();
        SelectionText = "已选择 " + selected.Length + " 个 Mod";
        SelectedModNames = selected.Length == 0 ? "尚未选择 Mod。可返回主页面勾选，窗口保持打开。"
            : string.Join("\n", selected.Select(addon => "• " + _explorer.AddonRoot.DownloadHistory.GetDisplayTitle(addon)));
        if (Rows.Count == 0 && !IsBusy) Status = selected.Length == 0 ? "勾选需要处理的 Mod 后，点击「读取选中 Mod」。" : "请核对以上已选名称，再点击「读取选中 Mod」。";
    }
    private void RowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReloadEditRow.OutputPath) or nameof(ReloadEditRow.ReplacementApplied))
        { this.RaisePropertyChanged(nameof(HasOutputs)); this.RaisePropertyChanged(nameof(HasImportableOutputs)); }
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        using var operation = new CancellationTokenSource(); _operation = operation;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "已取消；已完成的副本保留。"; }
        catch (Exception ex) { Status = "处理未完成：" + ex.Message; }
        finally { _operation = null; IsBusy = false; this.RaisePropertyChanged(nameof(HasOutputs)); this.RaisePropertyChanged(nameof(HasImportableOutputs)); }
    }
    public void Dispose()
    {
        _operation?.Cancel(); _invalidHandler.Dispose();
        ((INotifyCollectionChanged)_explorer.SelectedNodes).CollectionChanged -= SelectionChanged;
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
    }
}
