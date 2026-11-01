using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Serilog;
using ReactiveUI;

namespace FireAxe.ViewModels;

public sealed record ModCategoryFilter(string Id, string Name, string Description, int Count,
    string? ParentId = null, bool IsGroup = false, bool IsExpanded = true, Action? ToggleAction = null)
{
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
    public void Toggle() => ToggleAction?.Invoke();
}

public sealed class ModOverlapGroup(ModTarget target, string names, int installedCount, int enabledCount, Action show)
{
    public string Name => target.Name;
    public string Names => names;
    public bool HasEnabledOverlap => enabledCount > 1;
    public string Status => $"已安装 {installedCount} · 已启用 {enabledCount}";
    public void Show() => show();
}

public sealed class ModCatalogViewModel : ViewModelBase
{
    private sealed record Entry(VpkAddon Addon, bool Enabled);
    private readonly AddonRoot _root;
    private Entry[] _entries = [];
    private readonly HashSet<VpkAddon> _duplicateAddons = [];
    private readonly HashSet<string> _collapsedCategories = [];
    private IReadOnlyList<ModCategoryFilter> _allCategories = [];
    private bool _onlyOverlaps;
    private ModCategoryFilter _selectedCategory = new("all", "全部模组", "按游戏内替换对象识别", 0);

    public ModCatalogViewModel(AddonRoot root)
    {
        _root = root;
        Categories = [_selectedCategory];
        RecheckCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            RecheckStatus = "正在扫描备份本体、恢复入口…";
            try
            {
                var result = await ModLibraryRecovery.RefreshAsync(_root);
                RecheckStatus = $"恢复链接 {result.RestoredLinks} · 找回备份 Mod {result.DiscoveredMods} · 清理失效记录 {result.RemovedRecords}"
                    + (result.Warnings.Length == 0 ? "" : $" · 跳过 {result.Warnings.Length}：{result.Warnings[0]}");
                _root.Check();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to refresh mod storage.");
                RecheckStatus = "重新识别未完成：" + ex.Message;
            }
        });
    }

    public event Action? CatalogChanged;
    public IReadOnlyList<ModCategoryFilter> Categories { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public IReadOnlyList<ModOverlapGroup> OverlapGroups { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = [];
    public string Summary { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "正在读取模组…";
    public bool HasEnabledOverlap { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool HasOverlaps => OverlapGroups.Count > 0;
    public ModStorage Storage => _root.Storage;

    public ReactiveCommand<Unit, Unit> RecheckCommand { get; }
    public string RecheckStatus { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";

    public ModCategoryFilter? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || _selectedCategory == value) return;
            this.RaiseAndSetIfChanged(ref _selectedCategory, value);
            CatalogChanged?.Invoke();
        }
    }

    public bool OnlyOverlaps
    {
        get => _onlyOverlaps;
        set
        {
            if (_onlyOverlaps == value) return;
            this.RaiseAndSetIfChanged(ref _onlyOverlaps, value);
            CatalogChanged?.Invoke();
        }
    }

    public bool IsFiltering => _selectedCategory.Id != "all" || OnlyOverlaps;

    public static VpkAddon? GetVpk(AddonNode? node) => node is { IsValid: true } ? node switch
    {
        VpkAddon vpk => vpk,
        RefAddonNode reference => reference.ActualSourceAddon as VpkAddon,
        _ => null
    } : null;

    public bool Matches(AddonNode node)
    {
        if (GetVpk(node) is not { } vpk) return false;
        if (OnlyOverlaps && !_duplicateAddons.Contains(vpk)) return false;
        return _selectedCategory.Id switch
        {
            "all" => true,
            "unknown" => vpk.Inspection is { Error: null, Targets.Count: 0 },
            "pending" => vpk.Inspection is null || vpk.Inspection.Error is not null,
            var id when id.StartsWith("category:", StringComparison.Ordinal) => vpk.Inspection?.Targets.Any(match => "category:" + match.Target.Category == id) == true,
            var id => vpk.Inspection?.Targets.Any(match => match.Target.Id == id) == true
        };
    }

    public void ExpandAll()
    {
        _collapsedCategories.Clear();
        RefreshCategories();
    }

    public void CollapseAll()
    {
        foreach (var category in _allCategories.Where(category => category.IsGroup))
            _collapsedCategories.Add(category.Id);
        RefreshCategories();
    }

    private void ToggleCategory(string id)
    {
        if (!_collapsedCategories.Add(id)) _collapsedCategories.Remove(id);
        RefreshCategories();
    }

    private void ShowTarget(string id)
    {
        var target = _allCategories.FirstOrDefault(category => category.Id == id);
        if (target?.ParentId is { } parent && _collapsedCategories.Remove(parent)) RefreshCategories();
        SelectedCategory = _allCategories.FirstOrDefault(category => category.Id == id);
    }

    public void Activate(CompositeDisposable lifetime)
    {
        var changes = new Subject<Unit>().DisposeWith(lifetime);
        var nodes = new CompositeDisposable().DisposeWith(lifetime);
        void Queue(AddonNode _) => changes.OnNext(Unit.Default);
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(VpkAddon.Inspection) or nameof(AddonNode.Name) or nameof(AddonNode.IsEnabledInHierarchy)
                or nameof(AddonNode.IsValid) or nameof(RefAddonNode.SourceAddonId))
                changes.OnNext(Unit.Default);
        }
        void Rebuild()
        {
            if (!_root.IsValid) return;
            nodes.Clear();
            var allNodes = _root.GetDescendants().Where(node => node.IsValid).ToArray();
            foreach (var node in allNodes)
            {
                node.PropertyChanged += OnPropertyChanged;
                Disposable.Create(() => node.PropertyChanged -= OnPropertyChanged).DisposeWith(nodes);
            }
            _entries = allNodes.Select(node => (Node: node, Vpk: GetVpk(node))).Where(pair => pair.Vpk is not null)
                .GroupBy(pair => pair.Vpk!).Select(group => new Entry(group.Key, group.Any(pair => pair.Node.IsEnabledInHierarchy))).ToArray();
            RefreshCategories();
        }
        changes.Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler).Subscribe(_ => Rebuild()).DisposeWith(lifetime);
        _root.DescendantNodeCreated += Queue;
        _root.DescendantNodeMoved += Queue;
        _root.DescendantNodeDestroyed += Queue;
        _root.NewNodeIdRegistered += Queue;
        Disposable.Create(() =>
        {
            _root.DescendantNodeCreated -= Queue;
            _root.DescendantNodeMoved -= Queue;
            _root.DescendantNodeDestroyed -= Queue;
            _root.NewNodeIdRegistered -= Queue;
        }).DisposeWith(lifetime);
        Rebuild();
    }

    private void RefreshCategories()
    {
        var byTarget = _entries.SelectMany(entry => (entry.Addon.Inspection?.Targets ?? []).Select(match => (Entry: entry, match.Target)))
            .GroupBy(pair => pair.Target).OrderBy(group => group.Key.Category).ThenBy(group => group.Key.Name, StringComparer.Ordinal).ToArray();
        var categories = new List<ModCategoryFilter> { new("all", "全部模组", "整个目录 · 含子分组", _entries.Length) };
        var counts = byTarget.ToDictionary(group => group.Key, group => group.Count());
        foreach (var category in ModTargetClassifier.Targets.Concat(byTarget.Select(group => group.Key)).Distinct()
            .OrderBy(target => target.Category).ThenBy(target => target.Name, StringComparer.Ordinal).GroupBy(target => target.Category)
            .OrderByDescending(category => category.Sum(target => counts.GetValueOrDefault(target))))
        {
            var categoryId = "category:" + category.Key;
            categories.Add(new(categoryId, category.Key, "查看这一类的全部模组", _entries.Count(entry => entry.Addon.Inspection?.Targets.Any(match => match.Target.Category == category.Key) == true),
                IsGroup: true, IsExpanded: !_collapsedCategories.Contains(categoryId), ToggleAction: () => ToggleCategory(categoryId)));
            foreach (var target in category)
                categories.Add(new(target.Id, target.Name, target.Category, counts.GetValueOrDefault(target), ParentId: categoryId));
        }
        categories.Add(new("unknown", "未识别 / 其他", "可用原有标签手动标记", _entries.Count(entry => entry.Addon.Inspection is { Error: null, Targets.Count: 0 })));
        categories.Add(new("pending", "未读取 / 读取失败", "点击「重新识别」重试", _entries.Count(entry => entry.Addon.Inspection is null || entry.Addon.Inspection.Error is not null)));

        _duplicateAddons.Clear();
        OverlapGroups = byTarget.Where(group => group.Key.WarnOnOverlap && group.Count() > 1)
            .Select(group =>
            {
                foreach (var pair in group) _duplicateAddons.Add(pair.Entry.Addon);
                return new ModOverlapGroup(group.Key,
                    string.Join("\n", group.Select(pair => $"{(pair.Entry.Enabled ? "●" : "○")} {_root.DownloadHistory.GetDisplayTitle(pair.Entry.Addon)}")),
                    group.Count(), group.Count(pair => pair.Entry.Enabled), () => ShowTarget(group.Key.Id));
            }).OrderByDescending(group => group.HasEnabledOverlap).ToArray();
        HasEnabledOverlap = OverlapGroups.Any(group => group.HasEnabledOverlap);
        Summary = $"模组 {_entries.Length}  ·  已识别 {_entries.Count(entry => entry.Addon.Inspection?.Targets.Count > 0)}  ·  重复对象 {OverlapGroups.Count}  ·  同时启用重叠 {OverlapGroups.Count(group => group.HasEnabledOverlap)}";
        var selectedId = _selectedCategory.Id;
        _selectedCategory = categories.FirstOrDefault(category => category.Id == selectedId) ?? categories[0];
        _allCategories = categories;
        Categories = categories.Where(category => category.ParentId is null || !_collapsedCategories.Contains(category.ParentId)).ToArray();
        this.RaisePropertyChanged(nameof(SelectedCategory));
        this.RaisePropertyChanged(nameof(HasOverlaps));
        CatalogChanged?.Invoke();
    }
}
