# EmberKit v0.1.8 · Windows 便携版

## 更新内容

- **启动备份提醒。** 管理器启动时会说明手动整理如何将 `addons` 和子目录中的有效 VPK 本体集中到 `addons/modebackup`。如果你已经按文件夹分类，可以先备份整个 `addons`。勾选「下次不再提示」可保存这个偏好。
- **本体使用数字文件名。** VPK 从 `0.vpk` 开始编号。`mod-index.txt` 对照编号、可读名称、完整 SHA-256 和原路径，核验内容后复用相同的 VPK。工坊分享使用原作品 ID。
- **手动迁移旧名称。** 备份后退出游戏，再点击「应用 Mod」或本地存储中的整理按钮。旧中文名、旧 `m` 编号和 Hash 文件名会迁移为数字编号。表中会保留旧名称和路径，开关、标签、优先级和裁剪参数随库记录保留。

## 下载与升级

完整解压 `EmberKit-v0.1.8-Windows-x64-Portable.zip` 后运行 `EmberKit.exe`。便携包自带 .NET 运行库。

升级前关闭旧程序并保留自己的 `data`。Mod 库位于游戏的 `addons/modebackup`。便携包中的 `data` 为空，附带此前公开的 45 项 `L4D2-Mod-Share.txt` 清单。

## 验证与鸣谢

394 项检查通过，覆盖数字编号、旧名称迁移、TXT 查重、工坊来源，以及启动提醒的显示和偏好保存。检查在模拟目录中执行。

基于 ktxiaok/FireAxe 0.7.3，保留原作者、贡献者鸣谢及 LICENSE 与 NOTICE。参考图片与音效的来源和许可见 `Feedback/assets/PROVENANCE.txt`。
