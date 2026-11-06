// Modified for EmberKit, based on ktxiaok/FireAxe 0.7.3; see MODIFICATIONS.md.
using Serilog;
using SteamDatabase.ValvePak;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace FireAxe;

public abstract class VpkAddon : AddonNode
{
    private readonly ObservableCollection<string> _conflictIgnoringFiles = new();
    private readonly ReadOnlyObservableCollection<string> _conflictIgnoringFilesReadOnly;
    private readonly HashSet<string> _conflictIgnoringFileSet = new HashSet<string>();

    internal readonly ObservableCollection<(string File, IEnumerable<Guid> AddonIds)> _conflictingFilesWithAddonIds = new();
    private readonly ReadOnlyObservableCollection<(string File, IEnumerable<Guid> AddonIds)> _conflictingFilesWithAddonIdsReadOnly;
    internal readonly ObservableCollection<(Guid AddonId, IEnumerable<string> Files)> _conflictingAddonIdsWithFiles = new();
    private readonly ReadOnlyObservableCollection<(Guid AddonId, IEnumerable<string> Files)> _conflictingAddonIdsWithFilesReadOnly;

    private readonly WeakReference<VpkAddonInfo?> _addonInfo = new(null);

    protected VpkAddon()
    {
        _conflictIgnoringFilesReadOnly = new(_conflictIgnoringFiles);
        _conflictingFilesWithAddonIdsReadOnly = new(_conflictingFilesWithAddonIds);
        _conflictingAddonIdsWithFilesReadOnly = new(_conflictingAddonIdsWithFiles);

        PropertyChanged += OnPropertyChanged;
    }

    public ReadOnlyObservableCollection<string> ConflictIgnoringFiles => _conflictIgnoringFilesReadOnly;

    public ReadOnlyObservableCollection<(string File, IEnumerable<Guid> AddonIds)> ConflictingFilesWithAddonIds => _conflictingFilesWithAddonIdsReadOnly;

    public ReadOnlyObservableCollection<(Guid AddonId, IEnumerable<string> Files)> ConflictingAddonIdsWithFiles => _conflictingAddonIdsWithFilesReadOnly;

    public abstract string? VpkFilePath
    {
        get;
    }

    public string? ContentHash
    {
        get;
        internal set
        {
            if (NotifyAndSetIfChanged(ref field, value))
            {
                NotifyChanged(nameof(VpkFilePath));
                Root.RequestSave = true;
            }
        }
    }

    public string[] OriginalPaths { get; internal set; } = [];
    public WeaponAnimationRecipe? AnimationEdits
    {
        get;
        internal set { if (NotifyAndSetIfChanged(ref field, value)) Root.RequestSave = true; }
    }
    public string? StorageFileName
    {
        get;
        internal set
        {
            if (value is not null && !ModStorage.IsBodyName(value)) throw new ArgumentException("无效的备份短文件名。", nameof(value));
            if (NotifyAndSetIfChanged(ref field, value))
            {
                NotifyChanged(nameof(VpkFilePath));
                Root.RequestSave = true;
            }
        }
    }
    protected string? StoredVpkPath => ContentHash is { } hash ? Path.Combine(Root.Storage.BackupPath, StorageFileName ?? hash + ".vpk") : null;

    public override Type SaveType => typeof(VpkAddonSave);

    public override bool RequireFile => true;

    public override bool CanGetSuggestedName => true;

    public Task? CheckVpkValidityTask { get; private set => NotifyAndSetIfChanged(ref field, value); } = null;

    public ModInspection? Inspection
    {
        get;
        private set { if (NotifyAndSetIfChanged(ref field, value)) NotifyChanged(nameof(CatalogTarget)); }
    }

    public string? CatalogTargetId
    {
        get;
        set
        {
            if (value is not null && !ModTargetClassifier.Targets.Any(target => target.Id == value))
                throw new ArgumentException("无效的 Mod 分类标签。", nameof(value));
            if (NotifyAndSetIfChanged(ref field, value))
            {
                NotifyChanged(nameof(CatalogTarget));
                Root.RequestSave = true;
            }
        }
    }

    public ModTarget? CatalogTarget => CatalogTargetId is { } id
        ? ModTargetClassifier.Targets.First(target => target.Id == id) : Inspection?.PrimaryTarget;

    public VpkAddonInfo? RetrieveInfo()
    {
        this.ThrowIfInvalid();

        if (!_addonInfo.TryGetTarget(out var addonInfo))
        {
            if (TryCreatePackage(VpkFilePath, out var pak))
            {
                using (pak)
                {
                    addonInfo = VpkUtils.GetAddonInfo(pak);
                    _addonInfo.SetTarget(addonInfo);
                }
            }
        }
        return addonInfo;
    }

    public bool AddConflictIgnoringFile(string? file)
    {
        this.ThrowIfInvalid();

        if (string.IsNullOrEmpty(file))
        {
            return false;
        }

        if (!_conflictIgnoringFileSet.Add(file))
        {
            return false;
        }
        _conflictIgnoringFiles.Add(file);

        for (int i = 0, len = _conflictingFilesWithAddonIds.Count; i < len; i++)
        {
            if (_conflictingFilesWithAddonIds[i].File == file)
            {
                _conflictingFilesWithAddonIds.RemoveAt(i);
                break;
            }
        }

        Root.RequestSave = true;

        return true;
    }

    public bool RemoveConflictIgnoringFile(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        this.ThrowIfInvalid();

        if (!_conflictIgnoringFileSet.Remove(file))
        {
            return false;
        }
        _conflictIgnoringFiles.Remove(file);

        Root.RequestSave = true;

        return true;
    }

    public void ClearConflictIgnoringFiles()
    {
        this.ThrowIfInvalid();

        _conflictIgnoringFileSet.Clear();
        _conflictIgnoringFiles.Clear();
        Root.RequestSave = true;
    }

    public bool ContainsConflictIgnoringFile(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return _conflictIgnoringFileSet.Contains(file);
    }

    public override Task<string?> TryGetSuggestedNameAsync(object? arg = null, CancellationToken cancellationToken = default)
    {
        this.ThrowIfInvalid();

        if (RetrieveInfo()?.Title is { } title)
        {
            title = SanitizeName(title, out bool empty);
            if (!empty)
            {
                return Task.FromResult<string?>(title);
            }
        }

        return Task.FromResult<string?>(null);
    }

    public Task CheckVpkValidity()
    {
        this.ThrowIfInvalid();

        if (CheckVpkValidityTask is { IsCompleted: false })
        {
            return CheckVpkValidityTask;
        }

        Task GetTask()
        {
            var vpkPath = VpkFilePath;
            if (vpkPath is null)
            {
                InvalidateProblem<InvalidVpkFileProblem>();
                return Task.CompletedTask;
            }
            var addonTaskCreator = this.GetValidTaskCreator();
            return Task.Run(async () =>
            {
                InvalidVpkFileProblem? problem = null;
                ModInspection inspection = new(null, [], "文件不存在");
                WeaponAnimationRecipe? animationEdits = null;
                bool exist = false;
                try
                {
                    exist = File.Exists(vpkPath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Exception occurred during checking the existence of the file: {FilePath}", vpkPath);
                }
                if (exist)
                {
                    try
                    {
                        using var pak = new Package();
                        pak.Read(vpkPath);
                        animationEdits = WeaponAnimationRecipe.Read(pak);
                        var targets = ModTargetClassifier.Classify(pak.Entries?.Values.SelectMany(entries => entries).Select(entry => entry.GetFullPath()) ?? []);
                        var info = VpkUtils.GetAddonInfo(pak);
                        if (info?.IsMap == true && !targets.Any(match => match.Target.Id == "maps"))
                            targets = [.. targets, new(ModTargetClassifier.Targets.First(target => target.Id == "maps"), ["addoninfo.txt"])];
                        inspection = new(info?.Title, targets);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Exception occurred during reading the VPK file: {VpkPath}", vpkPath);
                        problem = new(this);
                        inspection = new(null, [], "VPK 读取失败");
                    }
                }
                await addonTaskCreator.StartNew(self =>
                {
                    if (self.VpkFilePath != vpkPath) return;
                    self._addonInfo.SetTarget(null);
                    self.Inspection = inspection;
                    if (animationEdits is not null) self.AnimationEdits = animationEdits;
                    self.SetProblem(problem);
                }).ConfigureAwait(false);
            });
        }

        var checkTask = GetTask();
        CheckVpkValidityTask = checkTask;
        checkTask.ContinueWith(_ =>
        {
            if (CheckVpkValidityTask == checkTask)
            {
                CheckVpkValidityTask = null;
            }
        }, Root.TaskScheduler);

        return checkTask;
    }

    protected override void OnPostCheck(Action<Task> taskSubmitter)
    {
        base.OnPostCheck(taskSubmitter);

        taskSubmitter(CheckVpkValidity());
    }

    protected override long? GetFileSize()
    {
        var path = VpkFilePath;
        if (path == null)
        {
            return null;
        }

        try
        {
            if (File.Exists(path))
            {
                return FileSystemUtils.GetContentLength(path);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception occurred during VpkAddon.GetFileSize.");
        }

        return null;
    }

    protected override Task<byte[]?> DoGetImageAsync(CancellationToken cancellationToken)
    {
        string? vpkPath = VpkFilePath;
        if (vpkPath == null)
        {
            return Task.FromResult<byte[]?>(null);
        }
        return Task.Run(() =>
        {
            if (TryCreatePackage(vpkPath, out var pak))
            {
                using (pak)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return VpkUtils.GetAddonImage(pak);
                }
            }
            return null;
        }, cancellationToken);
    }

    protected override void OnClearCaches()
    {
        base.OnClearCaches();

        _addonInfo.SetTarget(null);
        Inspection = null;
    }

    protected override void OnCreateSave(AddonNodeSave save0)
    {
        base.OnCreateSave(save0);

        var save = (VpkAddonSave)save0;
        save.ContentHash = ContentHash;
        save.StorageFileName = StorageFileName;
        save.CatalogTargetId = CatalogTargetId;
        save.AnimationEdits = AnimationEdits;
        save.OriginalPaths = OriginalPaths;
        save.ConflictIgnoringFiles = [.. ConflictIgnoringFiles];
    }

    protected override void OnLoadSave(AddonNodeSave save0)
    {
        base.OnLoadSave(save0);

        var save = (VpkAddonSave)save0;
        if (save.ContentHash is { } hash && ModOrganizer.IsContentName(hash + ".vpk")) ContentHash = hash.ToUpperInvariant();
        if (save.StorageFileName is { } name && ModStorage.IsBodyName(name)) StorageFileName = name;
        CatalogTargetId = ModTargetClassifier.Targets.Any(target => target.Id == save.CatalogTargetId) ? save.CatalogTargetId : null;
        OriginalPaths = save.OriginalPaths ?? [];
        AnimationEdits = save.AnimationEdits?.IsValid == true ? save.AnimationEdits : null;
        ClearConflictIgnoringFiles();
        foreach (var file in save.ConflictIgnoringFiles)
        {
            AddConflictIgnoringFile(file);
        }
    }

    protected override Task OnDestroyAsync()
    {
        var tasks = new List<Task>();

        if (CheckVpkValidityTask is { } task)
        {
            tasks.Add(task);
        }

        var baseTask = base.OnDestroyAsync();
        tasks.Add(baseTask);

        return TaskUtils.WhenAllIgnoreCanceled(tasks);
    }

    private static bool TryCreatePackage(string? path, [NotNullWhen(true)] out Package? pak)
    {
        pak = null;
        if (path == null)
        {
            return false;
        }
        try
        {
            pak = new Package();
            pak.Read(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Exception in VpkAddon.TryCreatePackage.");
            if (pak != null)
            {
                pak.Dispose();
                pak = null;
            }
            return false;
        }
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        if (name == nameof(VpkFilePath))
        {
            _addonInfo.SetTarget(null);
            Inspection = null;
            AnimationEdits = null;
            NotifyImageChanged();
        }
    }
}
