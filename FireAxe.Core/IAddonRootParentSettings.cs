// Modified for EmberKit, based on ktxiaok/FireAxe 0.7.3; see MODIFICATIONS.md.
using System;

namespace FireAxe;

public interface IAddonRootParentSettings
{
    string GamePath { get; set; }

    KillFeedbackSettings? KillFeedback => null;

    bool IsAutoUpdateWorkshopItem { get; }

    VpkAddonConflictCheckSettings VpkAddonConflictCheckSettings { get; }
}
