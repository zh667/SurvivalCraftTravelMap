# Survivalcraft Travel Map 烟雾测试记录

计划日期：2026-07-13

自动验证执行日期：2026-07-14

目标：Survivalcraft 2.4.40.6 / NetMod API 1.44 / Windows PC

## 状态规则

- `PASS（自动）`：已执行命令并保存可重复证据。
- `PENDING（未启动游戏）`：当前修复包尚未启动对应游戏内场景，不得视为通过或 release 授权。
- `待人工游戏内验证`：当前修复包尚未在该场景完成可见游戏 GUI 验证；不得视为通过。
- `待第二轮实机复测`：旧候选包已复现问题，代码修复和专项测试已完成，但修复包尚未重新进入同一场景确认。
- `FAIL`：已复现失败，必须记录日志和坐标。

## 自动集成结果（历史 RC）

下表记录的是首次进入世界前的 RC 验证。实机诊断后的修复已使该包失效为历史候选，不能沿用下表的 414/414 或旧包 SHA-256 作为最终结果；当前修复版的自动验证和哈希记录见后面的“当前 release/package/隔离部署证据”。

| 项目 | 状态 | 复现与证据 |
|---|---|---|
| Release 单元/集成测试 | PASS（自动） | `dotnet test SurvivalcraftTravelMap.sln --configuration Release --no-restore`：414/414 通过。 |
| 警告视为错误构建 | PASS（自动） | `dotnet build SurvivalcraftTravelMap.sln --configuration Release --no-restore -warnaserror`：0 warnings / 0 errors。 |
| XDB 单次注入 | PASS（自动） | `PackageStructureTests.Final_xdb_injects_exactly_one_travel_map_component_with_new_guids` 校验 Player 只有一个 `TravelMap` 成员、一个组件模板和全新 GUID。 |
| 封包 allowlist/身份/资源 | PASS（自动） | `Build-NetMod.ps1` 后 `Verify-Package.ps1` 输出 `PACKAGE_OK`；包中精确包含 DLL、manifest、XDB 和 5 个指定资源。禁用 SourceLink 提交输入后，干净 HEAD 连续两次构建的包与 DLL 均逐字节一致；历史候选包 SHA-256：`6AF2115B59AA55EE9844551A7E3C4C2DBE04F7858D81D8D5DB3BDC13681F88FB`；包内 DLL SHA-256：`30EC1BA76D8C2AA7E436FA781D3A6D4FEF60B5964A6B5C79FFA474CD944682B2`。DLL informational version 固定为 `1.0.0`，不含当前或父提交 SHA。 |
| 项目自有地图调色表 | PASS（自动） | `Generate-BlockPalette.ps1` 不读取旧文件，以确定性 HSV 算法和游戏公开 block Index 常用颜色覆盖生成 257 项；字节可复现测试通过。新 SHA-256：`E03B7EC6F4DAE056A1213CD01FED7F4A0CFA845A1FAE63385403BC96077A81C7`，不同于旧文件。 |
| 协议注册 | PASS（自动） | 测试反射程序集并确认只有 IPackage 41/61，无 60；61 冲突时回滚 41。 |
| 34GPSFix 冲突门禁 | PASS（自动） | 启动策略测试只查询 `34GPSFix`，冲突时注册调用为 0；组件 Load 在玩家运行时初始化前再次门禁。 |
| 原始 34GPSFix 字节不变 | PASS（自动） | Build + Verify 前后 SHA-256 均为 `00B49A731CC791014A14A316F25C07A37EAEED23DBC876C9EB50C384042CCD4B`。 |
| Mod 数量上报禁用 | PASS（自动） | 源码审计测试拒绝 `AntiCheatReportPackage`、`ReadOnlyModList*`、`CheckDataBaseValid`、`181215270` 和 ID 60。 |
| 隔离副本启动到主菜单 | PASS（自动） | 仅把历史候选包放入隔离游戏副本，隐藏启动 15 秒；日志记录资源数 8、DLL/XDB 加载及 `Entered screen "MainMenu"`，无 ERROR/EXCEPTION/FATAL。该项不替代下面的游戏内矩阵。 |

## 实机诊断记录

- 第一轮隔离世界测试发现玩家组件没有由 `mod.netxdb` 注入，同时联机版单人世界被错误当作 `WorkType.Local`。注入与运行能力判定修复后，日志已出现数据库组件注入和 `workType=Server, main=True, ui=True`，确认该场景是具有本地主玩家 UI 的集成主机。
- 第二轮隔离世界测试中，`M` 已可打开大地图，但右上角小地图不可见；地图只显示初始不透明区域，当前坐标被保存为探索位为真但 `RGBA=0,0,0,0`；地图和坐标点传送未完成。
- 针对第二轮现象已实现修复：以 `GuiWidget.ActualSize` 逻辑坐标定位右上角控件；进入区块后在地形更新之后调度探索，达到 `TerrainChunkState.InvalidPropagatedLight` surface-readable 阈值且 256 个采样均非透明时整块原子写入，重入时刷新旧 partial/透明缓存；集成主机绕开远程 ID 61 的 4 秒期限，并在成功提交后通过 `PositionSet` 同步权威位置。
- entered-chunk Task 5 提交 `e055afe` 的自动门禁证据为：初始 focused 29/29，通过删除 area-only 兼容测试后的 expanded focused 39/39，完整 Release 547/547，以及 `-warnaserror` 构建 0 warnings / 0 errors。
- 439/439、`PACKAGE_OK`、包 SHA-256 `DE63AFAFB98DA7149E858F4ED69A5ADB55C9FFAED460BB73D38E3B6D320BC0CE` 和包内 DLL SHA-256 `A54CB8576EB6ACB6380CFD452CB0DBE9D28B5CADC8EA136DBC842D42C787BFCB` 均属于 Task 5 之前的前一候选，仅供历史追溯，不是 `e055afe` 或当前 HEAD 的产物证据。当前 package/DLL 证据只使用下一节记录的新哈希。

## 当前 release/package/隔离部署证据

- 执行日期：2026-07-14
- 已测试源码 HEAD：`5c3e38ddf777f4520f740a18fda1ccf7376ee856`
- 提交证据：`5c3e38ddf777f4520f740a18fda1ccf7376ee856`，`fix: preserve global map lod coverage`，作者 `zh667 <3257696116@qq.com>`，提交时间 `2026-07-14T13:43:42+09:00`
- 当前文档记录的是该源码 HEAD 的构建产物；后续仅文档提交不改变已测试 DLL/package 的源码输入。

| 项目 | 状态 | 当前证据 |
|---|---|---|
| 完整 Release 测试 | PASS（自动） | `dotnet test SurvivalCraftTravelMap.sln -c Release -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\'`：642/642 通过，0 failed，0 skipped。 |
| warnings-as-errors 构建 | PASS（自动） | `dotnet build SurvivalCraftTravelMap.sln -c Release -p:TreatWarningsAsErrors=true -p:SurvivalcraftDir='E:\game\SurvivalcraftNet2.4\' --no-restore`：0 warnings / 0 errors。 |
| 当前封包结构 | PASS（自动） | 连续两次从同一 clean HEAD 构建，每次紧接运行 `Verify-Package.ps1` 均输出 `PACKAGE_OK`；精确 8-entry allowlist 为 DLL、manifest、XDB、调色表和 4 个 PNG，包中无游戏 DLL/SDK/source/debug 文件，也无 ID 60、`AntiCheatReportPackage`、Mod 数量上报或旧 marker。 |
| 可复现 package | PASS（自动） | 两次 package SHA-256 均为 `492863FB137CB20FEE36FB7E351F5C711C2CB67D6F48079306FB67935DD2EC5D`，大小均为 166181 bytes。 |
| 可复现包内 DLL | PASS（自动） | 两次包内 `SurvivalcraftTravelMap.dll` 与 Release DLL SHA-256 均为 `1D4C36157AD4730A6E51D9C0C64E5ECFE2C55C04B3A75080A82EB1D640869593`，大小均为 374784 bytes，逐字节一致。 |
| 原始 34GPSFix 保护 | PASS（自动） | 所有构建、校验和隔离部署前后只读复核；主游戏 `E:\game\SurvivalcraftNet2.4\NetMods\34GPSFix.netmod` 始终为 `00B49A731CC791014A14A316F25C07A37EAEED23DBC876C9EB50C384042CCD4B`。 |
| 隔离安装 | PASS（自动准备） | artifact 与 `.superpowers/smoke-game/NetMods/SurvivalcraftTravelMap.netmod` SHA-256 均为 `492863FB137CB20FEE36FB7E351F5C711C2CB67D6F48079306FB67935DD2EC5D`；所有目标的绝对解析路径均在 isolated root 内；isolated `NetMods` 不含 `34GPSFix.netmod` 或 `CompassMenu.netmod`，二者仍在 `DisabledNetMods`。 |
| exact cache invalidation | PASS（自动准备） | isolated `ModsCache/SurvivalcraftTravelMap.netmod` 在部署前已不存在，因此本轮无需删除缓存；未删除或重置 World2 和旧 travel-map `.sctm` 数据。 |
| fresh-process 数据前提 | PASS（自动准备） | 部署前匹配的 Survivalcraft/smoke-game 进程为 0；isolated `Worlds/World2` 仍存在（4 files），旧 travel-map cache 仍存在（8 个 `.sctm`）。未删除或重置二者，也未启动 GUI/native game。 |

## 当前批准的 10 项游戏内矩阵

以下 10 项与后续 7 项扩展矩阵共 17 项，全部是 `PENDING（未启动游戏）`，不能冒充 PASS。必须由用户从全新 isolated process 进入保留旧 travel-map cache 的 World2 实测；当前自动证据只证明构建、封包和隔离环境准备完成，不证明游戏内行为或可公开分享/可发布。

| 场景 | 状态 | 最小复现步骤 | 通过标准 |
|---|---|---|---|
| 1. 默认小地图与自适应边距 | PENDING（未启动游戏） | 将 isolated game 窗口分别设为 `1280×720`（16:9）与 `1440×960`（3:2）；每种窗口尺寸都在 UI 缩放 `0.75 / 1.0 / 1.25` 进入 World2，观察默认小地图、邀请按钮、上/右边距和附近 HUD。 | 六种窗口×缩放组合中，小地图视觉体量正确，map/button 均不越界、不互相或与关键 HUD 遮挡；right/top margins 自适应稳定；没有文字传送按钮。 |
| 2. HUD 隐藏与恢复 | PENDING（未启动游戏） | 在 `1280×720` 与 `1440×960` 的每个 `0.75 / 1.0 / 1.25` 缩放组合中，依次打开背包、角色、合成、睡眠、其他模态对话框和大地图，再逐项关闭。 | 小地图和邀请图标同时隐藏；关闭 modal 后按原设置与当前窗口边界恢复，不漂移、不越界、不新增遮挡。 |
| 3. 大地图入口 | PENDING（未启动游戏） | 分别单击小地图和按 `M`。 | 两种方式都打开大地图。 |
| 4. 16×16 区块边界原子揭示 | PENDING（未启动游戏） | 从一个区块跨入相邻 `16×16` 区块，打开地图观察新旧边界和未进入的相邻区块。 | 只立即揭示新进入的完整区块，共 256 像素；不揭示任何相邻未进入区块。 |
| 5. World2 旧 partial cache 修复 | PENDING（未启动游戏） | 使用保留的 World2 和 8 个旧 `.sctm`，离开旧透明/partial 区块后重新进入。 | 无需删除缓存，重入区块时修复全部 256 像素。 |
| 6. 地表右键安全传送 | PENDING（未启动游戏） | 右键已探索普通地表并传送。 | 在安全 Y 成功落地，不坠落、不窒息，并写入成功结果。 |
| 7. 坐标点安全传送 | PENDING（未启动游戏） | 创建/选取坐标点并执行传送。 | 围绕保存的 XYZ 安全成功，结果明确。 |
| 8. 不安全/无位置回退 | PENDING（未启动游戏） | 选择故意不安全或无有效落点的目标。 | 玩家保持未移动，或已移动时完整回退且保持安全静止。 |
| 9. 多人邀请图标与传送 | PENDING（未启动游戏） | 先以单人观察，再添加第二名玩家，打开图标并执行邀请传送。 | 单人隐藏邀请图标；第二名玩家出现后图标位于地图下方，邀请传送仍工作。 |
| 10. 结果日志坐标保护 | PENDING（未启动游戏） | 完成正常地图/坐标点成功和失败操作，检查日志；内部强制失败仅采用自动测试证据。 | 正常结果日志不包含精确 map/waypoint target 坐标；强制内部失败的格式、阶段和脱敏路径由自动测试覆盖，不向 release DLL 注入故障。 |

## 公开分享/release 前后续矩阵

下表是对当前 10 项矩阵的 release 前扩展覆盖，不替代或缩减上面的 10 项。所有条目同样为 `PENDING（未启动游戏）`；只有逐项保留日志/截图和重启证据后才能改为 PASS。完成自动构建或当前 10 项中的一部分，均不能跳过本节或据此宣称可公开分享/release。

| 扩展场景 | 状态 | 最小复现步骤 | 通过标准 |
|---|---|---|---|
| A. 正/负 chunk 与 64×64 tile 边界 | PENDING（未启动游戏） | 分别验证 `15→16`（chunk `0→1`）、`-1→-17`（chunk `-1→-2`）、`63→64`（tile `0→1`，chunk `3→4`）和 `-64→-65`（tile `-1→-2`，chunk `-4→-5`）；每次只让玩家进入目标 `16×16` 区块。 | 坐标使用向下取整语义；每次只揭示当前进入的完整 `16×16` 区块（256 像素），不揭示相邻区块；跨 `64×64` tile 边界后无镜像、错位、漏写或额外揭示。 |
| B. 昼/暮/夜/黎明亮度 | PENDING（未启动游戏） | 在同一已探索位置依次观察正午、黄昏、深夜和黎明，并切换日夜明暗设置。 | terrain brightness 平滑变化且关闭设置后恢复 `1.0`；frame、玩家/坐标点 marker、文字与其他 UI 不被 terrain brightness 染色。 |
| C. fresh restart 持久化 | PENDING（未启动游戏） | 在当前进程探索新区块、创建/修改 waypoints、保存全局 settings v2，并完成一次 World2 legacy repair；完全退出进程后从同一隔离副本重新启动。 | 探索、完整 XYZ waypoints、settings v2 与 World2 已修复 256 像素均在 fresh restart 后保留；显式 v1 迁移规则可追溯，future `>2` 文件字节不被覆盖。 |
| D. terrain/safe-Y hazard 矩阵 | PENDING（未启动游戏） | 对平地、山坡/悬崖、树叶或固体顶部、洞穴/低顶、水、冰、深水和明确无安全点分别执行 surface 与 waypoint teleport。 | 可用目标在安全 Y 成功且无坠落/窒息；树叶、危险/流体表面或不足两格净空不作为落点；不可用目标不移动或 clean rollback，速度/坠落状态安全清零。 |
| E. runtime/权威/binding/同步矩阵 | PENDING（未启动游戏） | 分别运行 pure single/local、integrated host、dedicated server、remote client + new SCTM server；在服务器级分别启用/禁用 `SurfaceTeleportEnabled` 与 `WaypointTeleportEnabled`；分别发送 legitimate bound sender 和 mismatched/unbound sender 的请求。 | 每种 runtime 只创建其应有 UI/服务；启用模式仅处理与目标玩家正确绑定的 sender，禁用模式或 bad sender 均不移动玩家、不产生成功 position sync，并返回清晰 result；integrated host 与 remote 成功请求只向正确绑定玩家同步权威位置，不重复注册或跨玩家写位置。 |
| F. legacy/unsupported remote 与 ID 41 | PENDING（未启动游戏） | 连接不支持 ID 61 的 legacy/unsupported remote server 请求 surface/waypoint teleport；随后以两名玩家验证旧 ID 41 邀请兼容流程。 | ID 61 不支持时客户端不误写本地位置并收到清晰失败/超时；ID 41 在既定邀请、接受/拒绝/超时兼容范围内工作；单人隐藏邀请 icon，多人时在地图下方显示。 |
| G. fresh process/type-init 回归 | PENDING（未启动游戏） | 确认旧 isolated Survivalcraft 进程完全退出，再启动全新进程并首次执行会触发 `SurvivalcraftPlayerFacade` 的传送；不得以 DLL hot swap 代替重启。 | 旧进程缓存的 type-initialization failure 不被沿用；全新进程首次 surface/waypoint 路径返回明确成功或安全失败，而不是因旧类型初始化缓存产生 `InternalError`。 |

## 人工测试记录模板

每次填写：

```text
日期/测试者：
游戏与 API 版本：
包 SHA-256：
世界副本名：
场景与坐标：
步骤：
实际结果：
状态：PASS / FAIL
日志或截图路径：
恢复操作：
```

本文件当前没有把任何未实际进入游戏验证的项目标为 PASS，也没有声明当前构建可公开分享或可发布。
