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
using Avalonia.Media;

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

public sealed record ModCategoryRow(IReadOnlyList<ModCategorySection> Sections, int Columns, ModCatalogViewModel? ExpandedCatalog);

public sealed class ModCategorySection(ModCategoryFilter category, int enabledCount, string targets, Action expand, Action open) : ViewModelBase
{
    public string Id => category.Id;
    public int Count => category.Count;
    public string EnabledText => $"已启用 {enabledCount} 个";
    public string Targets => string.IsNullOrWhiteSpace(targets) ? category.Description : targets;
    public bool IsExpanded
    {
        get;
        internal set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(ExpandText)); }
    }
    public string ExpandText => IsExpanded ? "收起 ▴" : "展开 ▾";
    public string Name => category.Name switch { "枪械 / 投掷" => "枪械与投掷", "感染者" => "特感与僵尸", "人物" => "生还者", _ => category.Name };
    public string Description => category.Name switch
    {
        "枪械 / 投掷" => "按游戏原始枪位，找外观与手感",
        "感染者" => "从 Hunter 到 Tank，找到你的感染者",
        "人物" => "八位生还者的模型、服装与外观",
        "近战" => "刀、斧、武士刀与其他近战替换",
        "补给" => "医疗包、药品与救援装备",
        "地图 / 战役" => "新战役、新地图与完整分包",
        "界面" => "HUD、图标与游戏界面",
        "声音" => "武器音效、语音与背景音乐",
        "特效" => "粒子、光效与视觉反馈",
        "脚本" => "玩法与脚本内容",
        _ => category.Description
    };
    public IBrush Accent { get; } = Brush.Parse(category.Name switch
    {
        "枪械 / 投掷" => "#8FD2FF", "感染者" => "#D8AAFF", "人物" => "#9FE2BB", "近战" => "#FFC78F",
        "补给" => "#FFE18A", "地图 / 战役" => "#8ADDD6", "声音" => "#F5A8C8", _ => "#A8B8FF"
    });
    public Geometry Icon { get; } = Geometry.Parse(category.Name switch
    {
        "枪械 / 投掷" => "M2,11 L14,11 14,7 25,7 25,10 30,10 30,15 16,15 13,25 8,25 10,15 2,15Z M3,7 H11 V9 H3Z",
        "感染者" => "M16,2 C7,2 3,8 4,16 L8,22 V28 H24 V22 L28,16 C29,8 25,2 16,2Z M8,12 H13 V17 H8Z M19,12 H24 V17 H19Z M14,20 H18 L16,17Z",
        "人物" => "M16,2 A6,6 0 1 0 16,14 A6,6 0 1 0 16,2Z M4,30 V25 C4,15 28,15 28,25 V30Z",
        "近战" => "M24,2 L30,8 12,23 9,20Z M5,20 L12,27 9,30 2,23Z M6,25 L8,27 3,32 1,30Z",
        "补给" => "M4,8 H28 V28 H4Z M11,3 H21 V8 H18 V6 H14 V8 H11Z M13,12 V16 H9 V21 H13 V25 H19 V21 H23 V16 H19 V12Z",
        "地图 / 战役" => "M2,6 L11,2 21,6 30,2 V26 L21,30 11,26 2,30Z M10,5 V24 H12 V5Z M20,8 V27 H22 V8Z",
        "声音" => "M2,12 H9 L19,4 V28 L9,20 H2Z M22,10 L25,7 C31,13 31,19 25,25 L22,22 C26,17 26,15 22,10Z",
        "特效" => "M16,1 L20,11 31,16 20,20 16,31 12,20 1,16 12,11Z",
        "脚本" => "M10,5 L1,16 10,27 13,24 6,16 13,8Z M22,5 L19,8 26,16 19,24 22,27 31,16Z",
        _ => "M2,3 H30 V29 H2Z M5,7 V10 H27 V7Z M5,14 V25 H13 V14Z M17,14 V17 H27 V14Z M17,21 V24 H27 V21Z"
    });
    public bool Matches(string query) => string.IsNullOrWhiteSpace(query)
        || (Name + " " + category.Name + " " + targets).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    public void Expand() => expand();
    public void Open() => open();
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
    private string? _overlapTargetId;
    private bool _isOverview = true;
    private string _categorySearchText = "";
    private IReadOnlyList<ModCategorySection> _sections = [];
    private string? _expandedCategoryId;
    private string _inlineTargetId = "all";
    private int _columns = 3;
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
    public event Action<VpkAddon>? ModRequested;
    public IReadOnlyList<ModCategoryFilter> Categories { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public IReadOnlyList<ModOverlapGroup> OverlapGroups { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = [];
    public string Summary { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "正在读取模组…";
    public bool HasEnabledOverlap { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool HasOverlaps => OverlapGroups.Count > 0;
    public ModStorage Storage => _root.Storage;
    public bool IsOverview
    {
        get => _isOverview;
        private set
        {
            if (_isOverview == value) return;
            this.RaiseAndSetIfChanged(ref _isOverview, value);
            this.RaisePropertyChanged(nameof(IsBrowsing));
            CatalogChanged?.Invoke();
        }
    }
    public bool IsBrowsing => !IsOverview;
    public string CategorySearchText
    {
        get => _categorySearchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _categorySearchText, value);
            this.RaisePropertyChanged(nameof(Sections));
            this.RaisePropertyChanged(nameof(SectionRows));
        }
    }
    public IReadOnlyList<ModCategorySection> Sections => _sections.Where(section => section.Matches(CategorySearchText)).ToArray();
    public int Columns
    {
        get => _columns;
        set { if (_columns == value) return; _columns = value; this.RaisePropertyChanged(nameof(SectionRows)); }
    }
    public IReadOnlyList<ModCategoryRow> SectionRows => Sections.Chunk(Columns)
        .Select(row => new ModCategoryRow(row, Columns, row.Any(section => section.Id == _expandedCategoryId) ? this : null)).ToArray();
    public ModCategorySection? ExpandedSection => _sections.FirstOrDefault(section => section.Id == _expandedCategoryId);
    public IReadOnlyList<ModCategoryFilter> InlineTargets => _expandedCategoryId is not { } id ? [] :
        new[] { new ModCategoryFilter("all", "全部", "这一分类的全部 Mod", ExpandedSection?.Count ?? 0) }
            .Concat(_allCategories.Where(target => target.ParentId == id && target.Count > 0)).ToArray();
    public ModCategoryFilter? InlineTarget
    {
        get => InlineTargets.FirstOrDefault(target => target.Id == _inlineTargetId) ?? InlineTargets.FirstOrDefault();
        set
        {
            if (value is null || value.Id == _inlineTargetId) return;
            _inlineTargetId = value.Id;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(InlineMods));
        }
    }
    public IReadOnlyList<VpkAddonViewModel> InlineMods => _entries.Where(entry => CategoryId(entry.Addon) == _expandedCategoryId
            && (_inlineTargetId == "all" || entry.Addon.CatalogTarget?.Id == _inlineTargetId))
        .OrderBy(entry => _root.DownloadHistory.GetDisplayTitle(entry.Addon), StringComparer.OrdinalIgnoreCase)
        .Select(entry => (VpkAddonViewModel)AddonNodeViewModel.Create(entry.Addon)).ToArray();

    public void ExpandCategory(string id)
    {
        _expandedCategoryId = _expandedCategoryId == id ? null : id;
        _inlineTargetId = "all";
        foreach (var section in _sections)
            section.IsExpanded = section.Id == _expandedCategoryId;
        NotifyInline();
    }

    public void OpenMod(VpkAddon addon)
    {
        OnlyOverlaps = false;
        OpenCategory(CategoryId(addon));
        ModRequested?.Invoke(addon);
    }
    public void ShowOverview() { OnlyOverlaps = false; IsOverview = true; }
    public void ShowAll() => OpenCategory("all");
    public void ShowOverlaps() { OnlyOverlaps = true; OpenCategory("all"); }
    public void OpenCategory(string id)
    {
        IsOverview = false;
        SelectedCategory = _allCategories.FirstOrDefault(category => category.Id == id) ?? _selectedCategory;
    }

    public ReactiveCommand<Unit, Unit> RecheckCommand { get; }
    public string RecheckStatus { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";

    public ModCategoryFilter? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null) return;
            var wasOverlapTarget = _overlapTargetId is not null;
            _overlapTargetId = null;
            if (_selectedCategory == value) { if (wasOverlapTarget) CatalogChanged?.Invoke(); return; }
            IsOverview = false;
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
        if ((_overlapTargetId == _selectedCategory.Id || OnlyOverlaps) && !_selectedCategory.IsGroup && _selectedCategory.Id is not "all" and not "unknown" and not "pending")
            return vpk.Inspection?.Targets.Any(match => match.Target.Id == _selectedCategory.Id) == true;
        return _selectedCategory.Id switch
        {
            "all" => true,
            "unknown" => CategoryId(vpk) == "unknown",
            "pending" => CategoryId(vpk) == "pending",
            var id when id.StartsWith("category:", StringComparison.Ordinal) => CategoryId(vpk) == id,
            var id => vpk.CatalogTarget?.Id == id
        };
    }

    public static string CategoryId(VpkAddon addon) => addon.CatalogTarget is { } target
        ? target.Id == "unknown" ? "unknown" : "category:" + target.Category
        : addon.Inspection is null || addon.Inspection.Error is not null ? "pending" : "unknown";

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
        OpenCategory(id);
        _overlapTargetId = id;
        CatalogChanged?.Invoke();
    }

    public void Activate(CompositeDisposable lifetime)
    {
        var changes = new Subject<Unit>().DisposeWith(lifetime);
        var nodes = new CompositeDisposable().DisposeWith(lifetime);
        void Queue(AddonNode _) => changes.OnNext(Unit.Default);
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(VpkAddon.Inspection) or nameof(VpkAddon.CatalogTargetId) or nameof(AddonNode.Name) or nameof(AddonNode.IsEnabledInHierarchy)
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
        var counts = _entries.Where(entry => entry.Addon.CatalogTarget is not null)
            .GroupBy(entry => entry.Addon.CatalogTarget!).ToDictionary(group => group.Key, group => group.Count());
        foreach (var category in ModTargetClassifier.Targets.Where(target => target.Id != "unknown")
            .OrderBy(target => target.Category).ThenBy(target => target.Name, StringComparer.Ordinal).GroupBy(target => target.Category)
            .OrderByDescending(category => category.Sum(target => counts.GetValueOrDefault(target))))
        {
            var categoryId = "category:" + category.Key;
            categories.Add(new(categoryId, category.Key, "查看这一类的全部模组", _entries.Count(entry => CategoryId(entry.Addon) == categoryId),
                IsGroup: true, IsExpanded: !_collapsedCategories.Contains(categoryId), ToggleAction: () => ToggleCategory(categoryId)));
            foreach (var target in category.OrderByDescending(target => counts.GetValueOrDefault(target)))
                categories.Add(new(target.Id, DisplayName(target), target.Category, counts.GetValueOrDefault(target), ParentId: categoryId));
        }
        categories.Add(new("unknown", "未识别 / 其他", "可手动更改分类标签", _entries.Count(entry => CategoryId(entry.Addon) == "unknown")));
        categories.Add(new("pending", "未读取 / 读取失败", "点击「重新识别」重试", _entries.Count(entry => CategoryId(entry.Addon) == "pending")));

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
        foreach (var name in new[] { "地图 / 战役", "界面", "声音", "特效", "脚本" })
            if (!categories.Any(category => category.Id == "category:" + name))
                categories.Add(new("category:" + name, name, "按游戏中的替换对象浏览", 0, IsGroup: true));
        var selectedId = _selectedCategory.Id;
        _selectedCategory = categories.FirstOrDefault(category => category.Id == selectedId) ?? categories[0];
        _allCategories = categories;
        var preferred = new[] { "枪械 / 投掷", "感染者", "人物", "近战", "补给", "地图 / 战役", "界面", "声音", "特效", "脚本" };
        _sections = categories.Where(category => category.IsGroup || category.Id is "unknown" or "pending")
            .OrderBy(category => Array.IndexOf(preferred, category.Name) is var index && index >= 0 ? index : 99)
            .Select(category => new ModCategorySection(category,
                _entries.Count(entry => entry.Enabled && CategoryId(entry.Addon) == category.Id),
                string.Join(" · ", categories.Where(target => target.ParentId == category.Id).OrderByDescending(target => target.Count).Select(target => target.Name)),
                () => ExpandCategory(category.Id), () => { OnlyOverlaps = false; OpenCategory(category.Id); })
                { IsExpanded = category.Id == _expandedCategoryId }).ToArray();
        this.RaisePropertyChanged(nameof(Sections));
        NotifyInline();
        Categories = categories.Where(category => category.ParentId is null || !_collapsedCategories.Contains(category.ParentId)).ToArray();
        this.RaisePropertyChanged(nameof(SelectedCategory));
        this.RaisePropertyChanged(nameof(HasOverlaps));
        CatalogChanged?.Invoke();
    }

    private void NotifyInline()
    {
        this.RaisePropertyChanged(nameof(ExpandedSection));
        this.RaisePropertyChanged(nameof(InlineTargets));
        this.RaisePropertyChanged(nameof(InlineTarget));
        this.RaisePropertyChanged(nameof(InlineMods));
        this.RaisePropertyChanged(nameof(SectionRows));
    }

    public static string DisplayName(ModTarget target) => target.Id switch
    {
        "melee:fireaxe" => "消防斧 / Fireaxe", "melee:frying_pan" => "平底锅", "melee:machete" => "砍刀 / Machete",
        "melee:baseball_bat" => "棒球棍", "melee:crowbar" => "撬棍", "melee:cricket_bat" => "板球棒",
        "melee:tonfa" => "警棍 / Tonfa", "melee:katana" => "武士刀 / Katana", "melee:electric_guitar" => "电吉他",
        "melee:knife" => "小刀 / Knife", "melee:golfclub" => "高尔夫球杆", "melee:pitchfork" => "草叉", "melee:shovel" => "铲子",
        _ => target.Name
    };
}
