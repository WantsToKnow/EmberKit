// Modified for EmberKit, based on ktxiaok/FireAxe 0.7.3; see MODIFICATIONS.md.
using System;
using System.Diagnostics.CodeAnalysis;

namespace FireAxe;

public class AddonRootSave
{
    public string[] IgnoredContentHashes { get; set; } = [];
    [AllowNull]
    public AddonNodeSave[] Nodes { get; set => field = value.EliminateNull(); } = [];

    [AllowNull]
    public string[] CustomTags { get; set => field = value.EliminateNull(); } = [];
}
