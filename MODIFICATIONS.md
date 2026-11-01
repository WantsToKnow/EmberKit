# EmberKit 修改说明

基于 ktxiaok / FireAxe 0.7.3（`f8aa1cf1391802c64525412fdf326dbe0ecc98b1`）。
日期：2026-11-01。感谢原作者的开发与开源；完整 Apache-2.0 许可证保留在 `LICENSE.txt`，来源说明见 `NOTICE.txt`。

## 功能改动

- TXT 分享清单的解析、导入、导出、工坊来源核对与失败项重试。
- 持久下载结果、最终路径、失败原因和打开文件位置；支持移除记录及在游戏运行期间清理未安装的失败条目，未安装项不阻挡主菜单应用；沿用原项目下载队列。
- 主列表、详情、搜索和同对象提醒复用已保存的作品标题，保留数字工坊 ID；使用文字开关，分别提示启用选择、待应用和上级分组限制。
- VPK 资源分类、空替换位置、同对象提醒及分类 / 搜索后的选择修正。
- 已知游戏路径的自动推断、复用与保存。
- `modebackup` 独立短编号、SHA-256 去重、`mod-index.txt` 和本地库恢复。
- 游戏顶层短名 VPK 入口、硬链接 / 相对符号链接选择、启用状态同步与游戏运行时待应用。
- 可选忽略工坊 SHA-1（默认关闭），保留大小和 VPK 格式检查；不关闭 SHA-256 存储身份计算。
- 实验性主菜单刷新：通过 Steam 启动时开启带随机密码的内置网络控制台，一键断开地图、卸载、更新入口、重新挂载，并通过 `show_addon_load_order` 核对 VPK 挂载；`path` 仅用于安装目录核验。失败或取消时尝试恢复挂载，不迁移或删除本体。连接设置保存在软件自身的 data/GameConsole，不打入分享包。
- 软件便携存储：设置、日志、连接配置和崩溃临时文件统一保存到软件同目录的 data；资源加载不再依赖启动工作目录。Mod 库记录继续随 modebackup 保存。打包使用全新目录和空白 data，可附带 TXT 清单并生成 ZIP。
- EmberKit 窗口名称、Windows 打包脚本、使用文档和 183 项独立临时目录自检。

这些修改独立于上游维护。原有下载、分组、标签、引用、优先级和冲突检查等基础工作归功于 FireAxe 原作者与贡献者。

## 修改过的上游文件

下列代码文件已添加显著的修改说明，原文件中的版权、协议和归属说明保留。README 已重写并显著标注修改，原始两种语言的 README 另存于 `docs/upstream-README*.md`。`.gitignore` 增加了本地游戏数据与产物排除项。

- `FireAxe.Core/AddonGroup.cs`
- `FireAxe.Core/AddonNode.cs`
- `FireAxe.Core/AddonNodeFileFinder.cs`
- `FireAxe.Core/AddonNodeSearchUtils.cs`
- `FireAxe.Core/AddonRoot.cs`
- `FireAxe.Core/AddonRootSave.cs`
- `FireAxe.Core/FileSystemUtils.cs`
- `FireAxe.Core/GamePathUtils.cs`
- `FireAxe.Core/IAddonRootParentSettings.cs`
- `FireAxe.Core/LocalVpkAddon.cs`
- `FireAxe.Core/PublishedFileDetails.cs`
- `FireAxe.Core/PublishedFileUtils.cs`
- `FireAxe.Core/RefAddonNode.cs`
- `FireAxe.Core/VpkAddon.cs`
- `FireAxe.Core/VpkAddonSave.cs`
- `FireAxe.Core/WorkshopVpkAddon.cs`
- `FireAxe.GUI/AppSettings.cs`
- `FireAxe.GUI/Utils.cs`
- `FireAxe.GUI/ViewModels/AddonNodeExplorerViewModel.cs`
- `FireAxe.GUI/ViewModels/AddonNodeListItemViewModel.cs`
- `FireAxe.GUI/ViewModels/AddonNodeSimpleViewModel.cs`
- `FireAxe.GUI/ViewModels/MainWindowViewModel.cs`
- `FireAxe.GUI/ViewModels/NewWorkshopCollectionViewModel.cs`
- `FireAxe.GUI/ViewModels/WorkshopVpkFinderViewModel.cs`
- `FireAxe.GUI/Views/AddonNodeEnableButton.axaml`
- `FireAxe.GUI/Views/AddonNodeEnableButton.axaml.cs`
- `FireAxe.GUI/Views/AddonNodeExplorerView.axaml`
- `FireAxe.GUI/Views/AddonNodeExplorerView.axaml.cs`
- `FireAxe.GUI/Views/AddonNodeGridRowView.axaml`
- `FireAxe.GUI/Views/AddonNodeTileView.axaml`
- `FireAxe.GUI/Views/AddonNodeView.axaml`
- `FireAxe.GUI/Views/AppSettingsWindow.axaml`
- `FireAxe.GUI/Views/AppSettingsWindow.axaml.cs`
- `FireAxe.GUI/Views/CommonMessageBoxes.cs`
- `FireAxe.GUI/Views/MainWindow.axaml`
- `FireAxe.GUI/Views/VpkAddonSectionView.axaml`
- `FireAxe.GUI/Views/WorkshopVpkAddonSectionView.axaml`

## 新增文件

`FireAxe.Checks/`、本地存储 / 工坊清单 / 结果记录等新核心文件、分类与下载界面、`docs/usage.zh-Hans.md`、`scripts/package-windows.ps1`、`NOTICE.txt` 与本说明均为本版新增；完整内容可在本仓库提交记录中比较。
