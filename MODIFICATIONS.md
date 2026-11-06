# EmberKit 修改说明

基于 ktxiaok / FireAxe 0.7.3（`f8aa1cf1391802c64525412fdf326dbe0ecc98b1`）。
日期：2026-11-03。感谢原作者的开发与开源；完整 Apache-2.0 许可证保留在 `LICENSE.txt`，来源说明见 `NOTICE.txt`。

## 功能改动

- TXT 分享清单的解析、导入、导出、工坊来源核对与失败项重试。
- 持久下载结果、最终路径、失败原因和打开文件位置；支持移除记录及在游戏运行期间清理未安装的失败条目，未安装项不阻挡主菜单应用；沿用原项目下载队列。
- 主列表、详情、搜索和同对象提醒复用已保存的作品标题，保留数字工坊 ID；使用文字开关，分别提示启用选择、待应用和上级分组限制。
- VPK 资源分类、空替换位置、同对象提醒及分类 / 搜索后的选择修正。
- 已知游戏路径的自动推断、复用与保存。
- `modebackup` 独立短编号、SHA-256 去重、`mod-index.txt` 和本地库恢复。
- 游戏顶层短名 VPK 入口统一使用相对符号链接，移除链接方式选项和旧库偏好；同步时转换旧硬链接，失败时保留原入口和本体并报错，不自动改用硬链接。保留启用状态同步与游戏运行时待应用。
- 日常修改改为手动应用：开关 / 优先级只标记待应用，移除延时自动全库同步和游戏退出后的自动提交。下载、导入、裁剪替换不再顺带改写游戏入口；普通检查和重开仅轻量核对，待应用选择可保存恢复。文件监听与清单检查只提示差异，完整扫描 / Hash / 入口写入留给明确的应用、整理、检查同步和助手启动操作。
- 剪贴板工坊链接的确认提示改为打开新“链接下载”页并预填链接，复用下载队列、归档与结果记录，不再创建旧流程的工坊条目。
- 工坊 SHA-1 默认忽略，链接下载、剪贴板链接和 TXT 导入保持一致，可取消勾选启用比对；Steam 标注大小不符只记警告，继续安装有效 VPK；保留 VPK 格式检查和 SHA-256 存储身份计算。失败下载保留已有文件，下载结果支持打开文件位置。
- 手动链接和剪贴板检测默认勾选“下载完成后自动启用”；TXT 导入继续保留清单中明确设置的开关，没有指定时默认启用。
- 同一个下载入口支持 Steam 合集，展开子合集并去重，保留显式单项配置，继续复用已有下载、归档和跳过已安装项目的流程。
- 新增默认关闭、独立于击杀反馈的手枪自动连点；客户端接收表识别当前手枪，使用 Windows 普通鼠标输入重复点击，松手、菜单、切出和过期状态停止；不修改武器射速或游戏按键绑定。
- 实验性主菜单刷新：通过 Steam 启动时开启带随机密码的内置网络控制台，一键断开地图、卸载、更新入口、重新挂载，并通过 `show_addon_load_order` 核对 VPK 挂载；`path` 仅用于安装目录核验。失败或取消时尝试恢复挂载，不迁移或删除本体。连接设置保存在软件自身的 data/GameConsole，不打入分享包。
- 软件便携存储：设置、日志、连接配置和崩溃临时文件统一保存到软件同目录的 data；资源加载不再依赖启动工作目录。Mod 库记录继续随 modebackup 保存。打包使用全新目录和空白 data，可附带 TXT 清单并生成 ZIP。
- 主页面采用分类 / 具体对象两层可独立展开的目录，枪械、AK、手枪、特感、Hunter 等可同时保持打开；折叠父目录、调整开关、改标签、重新识别和从详情返回都保留对应展开状态。左侧工具进一步缩紧，状态说明折叠收纳，主区用于目录及 Mod 简介、可用缩略图、开关和手动标签；支持名称搜索定位、展开分类及全部收起。每个 Mod 仍只有一个浏览分类 / 对象归属，完整资源匹配用于冲突检查；明确点击完整列表或详情才切页，原全库和分组功能保留。
- 目录进一步收紧：宽屏下对象分为两个独立向下排列的列，窄屏单列；Mod 行采用小缩略图、标题、单行有效简介与作者，去除重复路径，标签编辑改为小按钮弹出，分类列表入口并入标题栏。自动隐藏只重复标题的简介，完整信息仍可在详情查看。
- 启动设置新增独立的“联机插值预设（65 ms）”勾选，默认关闭且随便携设置保存；勾选后普通 Steam 启动追加 `+cl_interp_ratio 1 +cl_interp 0.065`，主页面与工坊启动共用，不请求原生模块，不写游戏 cfg。
- 问题列表改为主窗口所属的非模态浮动工具窗，作品名点击可展开、滚动并高亮目录内对应 Mod，保留浮窗并让出右侧开关空间，关闭后恢复主区。读取战役元数据将资源分包归入地图；地图间普通同名资源降为兼容性提示，完整冲突明细保留，同名 BSP / mission 内容不同及非地图资源冲突仍提醒。
- 新增明确的本地 VPK 导入按钮和文件菜单入口，可多选、保留外部源文件、校验归档并定位，重复内容保留现有状态，单项失败不阻挡其他文件。游戏运行中先入库待应用，不更改挂载；位于 addons 的散装源在明确应用并卸载后整理成唯一入口。复用原归档和主菜单事务，批量结束后只刷新一次。
- 工具区底部增加低调的“附加功能”入口，手枪连点移入且设置不变；选中 Mod、明确读取后才显示换弹时长与批量逐项负小数裁尾。新增托管 MDL49/53 内置动画读取 / 裁尾，正确生成分段末帧、按整帧显示实际裁量、处理被裁事件并保留未裁事件的时点与名字指针，生成副本不覆盖原 Mod；支持显式导入副本，其他布局逐项说明不支持。
- 动画工具的可选替换开关改为“处理后停用旧版并启用新版”：先添加启用新版，再停用旧版，迁移手动分类、有效优先级、引用与依赖；始终保留旧文件和节点，不再自动删除或安排补删，应用仍需手动点击。
- 修复详情删除父组与子项等多选时读取失效节点路径引发的崩溃；先规范选择并捕获名称 / 路径，失败进入进度结果。分类行增加独立单项删除按钮，与原删除流程共用确认和错误处理。
- 分类 Mod 行增加“文件位置”按钮，复用已有文件定位操作，在资源管理器选中实际 VPK 本体，悬停显示完整路径；无需先选择或进入详情。
- 增加独立切枪裁尾与换弹 / 切枪分组时长，支持精确到模型和动画编号的裁剪步骤；占位、多混合及跨动作共享的动画逐项跳过。派生包和库元数据保存最初工坊来源、SHA-256 与完整有序操作，TXT v2 附带可选 trim 参数，原版只下载一次后保留并生成多个派生版，恢复各自开关 / 优先级。下载中的原版也可等待后处理，版本不符保留原版并报告；本地来源不冒充工坊原版。
- 分类内增加明确的“选择”复选框与选中高亮，可直接多选并与原完整列表共用选择，独立于游戏启用开关；附加功能在读取前实时列出所有已选名称、允许清空，并说明更改选择后重新读取。目录刷新保留分组内已选 Mod。
- 修复换弹导出的包被游戏拒绝并报 Unknown version 2：ValvePak 默认写 VPK 2，现将输出转换成正确的 VPK 1 头部 / 数据布局，移除 VPK 2 校验尾段，发布前验证版本、头长与全部条目 CRC；动画与素材不因此改变。
- 工作台采用左右分栏：左侧紧凑工具区、右侧全高分类 / Mod 工作区，独立常驻滚动条；分隔条可拖动分配工具与工作区、对象列表与中间 Mod 列表的宽度。
- 主窗口左侧突出显示“从助手启动游戏”；主页面“应用 Mod”一次点击直接复用现有重载事务，游戏运行时返回主菜单重载，未运行时同步本地入口，不再打开中间操作页或二次确认。按钮旁始终说明会离开当前对局，结果直接显示在主页面。
- EmberKit 窗口名称、Windows 打包脚本、使用文档和 373 项独立临时目录自检；击杀反馈另有原生逻辑、解码、ABI 和独立进程装载检查。
- 自产击杀反馈实验版：启动区开关及“所有僵尸 / 仅特感”范围设置，复用普通 Steam 启动和带密码控制台连接。通过认证及路径核验后，使用自产 x86 加载器装载客户端模块，不添加 -insecure / -secure / plugin_load，不修改 Steam 保存的启动选项。包括本地击杀归属、普感 / 特感 / Tank / Witch 过滤、爆头、连杀骷髅和音效，不统计命中伤害。
- 弃用实测失败的 plugin_load / 非 VAC 启动方案。新版本分别核对装载结果、游戏绘制线程初始化、本次会话和 PID，显示死亡事件 / 本人击杀 / 绘制计数；不再凭启动请求或服务器插件列表报告成功。新代码采用 Apache-2.0；参考图片、音效另列来源和授权状态，不使用参考包的程序。具体服务器和 VAC 兼容性仍待实测。
- 修正客户端模块初始化时导致闪退的类表入口错误：按本机游戏二进制与用户崩溃转储核实 VClient016 使用第 7 项，增加对应回归检查；日志分别记录接收表、图片、音效与事件监听的初始化阶段。
- 增加击杀反馈诊断：原始事件字段、击杀拒绝原因及累计计数、绘制条件、订阅状态、音效结果、定时汇总与回调异常现场；详细事件采样限频，保留既有击杀判定规则。
- 按 2026-11-04 实测日志修正普感编号为0时被全部拒绝的问题，并以40条真实事件数据回放检查本人 / 队友归属；按崩溃转储核实网络实体类别接口为第1项，修正错误调用导致的闪退，同时识别 Smoker 等具名特感类别。
- 修正首次绘制时把删除纹理接口当成创建接口的问题：Surface031 创建使用第34项，删除使用第33项；检查覆盖两张反馈纹理的创建、上传和删除，保留游戏已有纹理，并在首次绘制完成时立即保存诊断状态。
- 击杀骷髅最终缩至初版的一半；取消倍率和连杀数字，改为放大落下、缩小落点、扩散圆环和向左入列的动画，旧图标平滑右移，常驻队列保留最近3个并淡出移出的图标。

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
