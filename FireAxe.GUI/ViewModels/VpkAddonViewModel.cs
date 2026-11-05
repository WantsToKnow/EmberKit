using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading;

namespace FireAxe.ViewModels;

public sealed record ModCatalogTag(string? Id, string Label);

public abstract class VpkAddonViewModel : AddonNodeViewModel
{
    public static IReadOnlyList<ModCatalogTag> CatalogTags { get; } = new[] { new ModCatalogTag(null, "自动识别") }
        .Concat(ModTargetClassifier.Targets.OrderBy(target => target.Category).ThenBy(target => ModCatalogViewModel.DisplayName(target))
            .Select(target => new ModCatalogTag(target.Id, target.Category + " · " + ModCatalogViewModel.DisplayName(target)))).ToArray();

    public ModCatalogTag? SelectedCatalogTag
    {
        get => CatalogTags.FirstOrDefault(tag => tag.Id == Addon?.CatalogTargetId);
        set { if (value is not null && Addon is { } addon) addon.CatalogTargetId = value.Id; }
    }
    public string CatalogLabel => Addon?.CatalogTarget is { } target
        ? target.Category + " · " + ModCatalogViewModel.DisplayName(target) + (Addon.CatalogTargetId is null ? "（自动）" : "（手动）")
        : Addon?.Inspection is { Error: null } ? "未识别 / 其他" : "未读取 / 读取失败";

    private VpkAddonInfo? _info = null;

    private readonly ObservableAsPropertyHelper<VpkAddonConflictingDetailsViewModel?> _conflictingDetailsViewModel;

    public VpkAddonViewModel(VpkAddon addon) : base(addon)
    {
        _conflictingDetailsViewModel = this.WhenAnyValue(x => x.Addon)
            .Select(addon => addon is null ? null : new VpkAddonConflictingDetailsViewModel(addon))
            .ToProperty(this, nameof(ConflictingDetailsViewModel), deferSubscription: true);
    }

    public new VpkAddon? Addon => (VpkAddon?)base.Addon;

    public override Type AddonType => typeof(VpkAddon);

    public VpkAddonInfo? Info
    {
        get => _info;
        private set => this.RaiseAndSetIfChanged(ref _info, value);
    }

    public VpkAddonConflictingDetailsViewModel? ConflictingDetailsViewModel => _conflictingDetailsViewModel.Value;

    protected override void OnNewAddon(AddonNode addon, CompositeDisposable disposables)
    {
        base.OnNewAddon(addon, disposables);
        ((VpkAddon)addon).WhenAnyValue(node => node.CatalogTarget, node => node.CatalogTargetId).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(CatalogLabel));
            this.RaisePropertyChanged(nameof(SelectedCatalogTag));
        }).DisposeWith(disposables);
    }

    protected override void OnRefresh(CancellationToken cancellationToken)
    {
        base.OnRefresh(cancellationToken);

        var addon = Addon;

        Info = addon?.RetrieveInfo();
    }

    protected override void OnClearCaches()
    {
        base.OnClearCaches();

        Info = null;
    }
}
