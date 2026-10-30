# CS2-PugFlow v1.6.0 — 无限热身 + 开赛投票 + 刀战选边 + .drop 发刀 + 无限加时 + !help 指令表

适用：AWS Lightsail / Ubuntu 24.04、CS2 Dedicated Server、CounterStrikeSharp v1.0.376（.NET 10）。**与 RockTheVote-CSS 独立，不占用其 `!rtv`、`!nominate`、`!votemap`、`!maps` 命令。**

> 这是需要在你的服务器上编译的**源码部署包**，不是已测试过的 DLL。已进行脚本语法检查与配置检查；尚未在你的实际 CS2 服务端上完成编译和多人对局测试。先在没有重要比赛时部署、测试，出现编译错误保留完整输出。

## 一、预期流程

1. 新地图加载 → 默认进入**无限热身**：任何位置免费买枪、热身死亡复活。
2. 任意玩家输入 `!votestart`，开始 25 秒开赛投票。
3. 玩家输入 `!yes` / `!no`；赞成达到 `ceil(当前在线真人 × 50%)`，立即结束投票。
4. **双方各至少一名真人玩家**：服务器执行 `knife.cfg`，重新开始一局**只有刀、不能买枪、不能复活**的刀战。刀战输赢由 CS2 的 `round_end` 事件判断（CT 或 T）。
5. 刀战胜方成员任选一人输入 `!ct` / `!t`（直接说要打哪一边），或 `!stay`（保持当前 CT/T）/ `!swap`（CT/T 交换）；获胜队先输入的有效选择生效。其他玩家不能替获胜队决定。
6. 选边后执行 `live.cfg`，**比分归零、恢复 $800 初始经济、正常买枪规则，MR12 正式比赛开始**。12:12 平局时进入 **MR3 加时**（6 回合、每人 $12500），加时再平就继续加时，**直到分出胜负**，不会以平局结束。
7. 刀战结束后如果获胜队 30 秒内没有选边，默认 `!stay`，自动开赛，避免卡住。
8. 正式比赛结束、出现结算画面 15 秒后，插件会重新加载当前地图，回到无限热身（玩家不会被踢出服务器）。如果 RockTheVote 在这之前已经切换到新地图，就以 RockTheVote 选的地图为准，同样进入无限热身。
9. 投票、刀战、选边或正式比赛进行中，如果真人玩家全部退出，插件会自动回到无限热身，不会卡在比赛状态。

**单人测试例外：** 若在线仅一名真人，或者所有真人都处在同一队，无法进行有胜负的刀战。此时 `!votestart` + `!yes` 会**跳过刀战直接执行 `live.cfg`**，保持原先允许单人测试的规则。需要至少 T、CT 双方各一名真人，才能测试刀战与选边。插件不会自动填充机器人。

**特殊场景：** 刀战平局会重开刀战。刀战结束到选边期间进入暂停热身状态，以防自动开始第二局刀战。

## 二、玩家命令

| 命令 | 用途 | 谁能用 |
|---|---|---|
| `!votestart` | 发起开赛投票 | 所有玩家，至少 1 人 |
| `!yes` / `!startyes` | 赞成开赛 | 所有玩家 |
| `!no` / `!startno` | 反对开赛 | 所有玩家 |
| `!ct` / `!t` | 刀战胜方直接选择打 CT 或 T | 刀战胜方任一成员 |
| `!stay` | 刀战胜方保持原阵营 | 刀战胜方任一成员 |
| `!swap` | 刀战胜方与败方交换 CT/T | 刀战胜方任一成员 |
| `!startstatus` | 当前状态和开票情况 | 所有玩家 |
| `!help` / `!commands` | 列出服务器上的所有聊天指令（含 RockTheVote 的），绿色 = 现在能用，灰色 = 现在不能用并注明原因 | 所有玩家 |
| `.drop` | 在自己脚下掉 5 把自己的刀，给其他玩家捡 | 活着且在 T/CT 的玩家；刀战中不可用。正式比赛每回合限一次，其他时候冷却 30 秒 |
| `!startreset` | 中止投票/刀战/比赛，强制回到无限热身 | 拥有 `@css/generic` 权限的管理员，或服务器控制台 / RCON（`css_startreset`） |

**选边是聊天命令，不是 F1/F2 或图形按钮。** `.drop` 用的是点号开头，直接在聊天框输入 `.drop`，全体聊天和队伍聊天都可以。 RockTheVote 的地图投票功能仍由原插件处理。

**`!ct` 与 CS2MenuManager 的冲突：** CS2MenuManager 默认把 `css_ct` 当作"切换菜单样式"的快捷命令（配置 `configs/plugins/CS2MenuManager_MenuManager/CS2MenuManager_MenuManager.json` 的 `ChangeMenuTypeCommands`）。不改的话，输入 `!ct` 会同时弹出菜单样式选择。需要把其中的 `"css_ct"` 删掉，只保留 `"css_menutype"`，切换菜单样式改用 `!menutype`。

**`!help` 里的 RockTheVote 规则：** `!help` 无法读取 RockTheVote 的内部状态，是按 `RockTheVote.json` 的配置推算的：`!rtv` 热身中不可用、换图后 3 分钟内不可用；`!nominate` 需要 `@css/vip` 或 `@css/nominate`；`!extend` 已关闭；`!mapmenu` 需要 `@css/changemap`。修改 RockTheVote 配置后，同步修改源码里的 `RtvMapStartDelaySeconds`、`NominatePermissions`、`MapMenuPermission` 和 `PlayerCommands()` 列表。

## 三、安装 / 从 v1.0 升级

你如果还没安装上一版 `PugFlow.zip`，直接安装本版即可。如果已安装旧版（v1.0 ~ v1.2.1），`install.sh` 会在**编译成功后**停止 CS2、替换旧 DLL，备份 `server.cfg`、`practice.cfg`、`live.cfg`、`knife.cfg` 和旧插件 DLL，然后重启。部署中途出错时脚本会自动把 CS2 重新拉起来，不会让服务器一直停着。

### 1. 将本 ZIP 上传到 Lightsail

用 Windows PowerShell（**电脑本地**）运行，替换路径和 IP：

```powershell
scp -i "C:\path\to\LightsailDefaultKey-us-east-1.pem" "C:\path\to\CS2-PugFlow.zip" ubuntu@你的静态IP:/home/ubuntu/
```

或者用 WinSCP/SFTP 把 ZIP 传到 `/home/ubuntu/`。

### 2. 服务器上的操作（Lightsail SSH，ubuntu 用户）

```bash
sudo apt update
sudo apt install -y unzip dotnet-sdk-10.0
```

```bash
cd /home/ubuntu
```

```bash
unzip -o CS2-PugFlow.zip
```

```bash
cd /home/ubuntu/PugFlow
```

```bash
bash install.sh
```

安装脚本会从当前 CounterStrikeSharp 目录提取 API DLL 用于编译，成功后才会部署。编译需要对应的 .NET 10 SDK。

注意：如已安装旧版，本包解压会覆盖 `/home/ubuntu/PugFlow/` 的旧**源码**，并在编译成功后更新服务器中的插件 DLL；RockTheVote-CSS 文件不动。

### 3. 检查状态

```bash
sudo systemctl status cs2 --no-pager
```

```bash
sudo journalctl -u cs2 -n 150 --no-pager | grep -Ei 'PugFlow|PugFlow|error|exception'
```

```bash
sudo ls -lh /home/steam/cs2/game/csgo/addons/counterstrikesharp/plugins/PugFlow/PugFlow.dll
```

## 四、实际测试

### 单人

- 进入服务器、确认热身与免费买枪。
- 聊天：`!votestart`，再输入 `!yes`。
- 预期：出现“skipping knife round”提示，马上进入正式配置。这是预期行为，不是刀战失效。

### 两人以上（至少一 CT、一 T）

- 两人在不同阵营加入，执行 `!votestart`，再投 `!yes`。
- 预期：进入刀战。枪械被移除，只保留刀；正常只打一个回合。
- 刀战赢家所在队的一名玩家输入 `!ct` / `!t`（或 `!stay` / `!swap`）。`!ct` / `!t` 选的是**获胜队要打的那一边**：已经在 CT 的获胜队输入 `!ct` 等于留边，输入 `!t` 等于换边。
- 如果 `!swap`，两队阵营交换（场上的 Bot 也一起换边）；然后比赛重启，比分从 0:0 开始，经济恢复正常。
- 也可不选边，等待 30 秒，默认留边正式开赛。

**切图测试：** RockTheVote-CSS 成功切换到官方地图后，应重新进入无限练习。

## 五、插件源码里可调整的参数

`source/PugFlow.cs`：

- `MinimumPlayers = 1` — 最少允许开票人数
- `ApprovalFraction = 0.50` — 50% 赞成通过
- `StartVoteSeconds = 25f` — 开赛投票倒计时
- `StartVoteCooldownSeconds = 30` — 投票失败后，再次发起投票的冷却时间
- `SidePickSeconds = 30f` — 胜方选边时限，超时默认留边
- `ResetPermission = "@css/generic"` — 使用 `!startreset` 所需的管理员权限
- `DropKnifeCount = 5` — 每次 `.drop` 掉几把刀
- `MatchEndReloadSeconds = 15f` — 比赛结束后多少秒重新加载地图。应大于 RockTheVote 的 `DelayToChangeInTheEnd`（默认 6 秒）
- `DropCooldownSeconds = 30` — 热身、投票、选边阶段 `.drop` 的冷却时间（正式比赛中改为每回合一次）

修改后再运行 `bash install.sh` 重新编译部署。

`configs/`：

- `practice.cfg` — 无限热身、随地免费购买、关闭 Bot（`bot_quota 0`；服务器设置了 `sv_hibernate_when_empty 0` 不再休眠，没人时 Bot 会一直占用 CPU）
- `knife.cfg` — 刀局规则、只给刀、禁止购买和炸弹
- `live.cfg` — 选边后 MR12、比分重置、恢复经济、关闭 Bot；12:12 进入 MR3 加时（`mp_overtime_*`），`mp_overtime_limit 0` 表示加时次数不限

三个 cfg 都默认**关闭友伤**（`mp_friendlyfire 0`）。想在某个阶段开启友伤，就把对应 cfg 里的这一行改成 `mp_friendlyfire 1`。

不要在 `server.cfg` 里写 `exec practice.cfg`、`exec knife.cfg` 或 `exec live.cfg`；插件会自动调用（从 v1.4.0 起 `practice.cfg` 也由插件在换图 3 秒后执行；v1.5.0 起 Rush 地图上也执行，开赛时用 `rush_live.cfg`）。

## 六、备份及回退

安装前配置备份会放在 `/home/ubuntu/cs2-startvote-backup-日期时间/`。如需回退：

```bash
sudo systemctl stop cs2
```

```bash
sudo mv /home/steam/cs2/game/csgo/addons/counterstrikesharp/plugins/PugFlow /home/ubuntu/PugFlow-disabled
```

从备份目录恢复原有 `server.cfg`，并按需恢复 `practice.cfg`、`live.cfg`、`knife.cfg`（要退回旧版插件，就把备份里的 `PugFlow.dll` 放回插件目录），再执行：

```bash
sudo systemctl start cs2
```

## 七、注意事项

- 这是**轻量自定义插件**，不是 MatchZy 那种完整比赛管理系统；不提供自动队伍配置、暂停和录像管理（加时由 CS2 原生 `mp_overtime_*` 处理）。
- 当前由 CS2 原生 `round_end` 判断刀战胜队，并在胜方选边后用 `SwitchTeam()` 交换双方；服务器版本更新后建议回归测试。
- 投票比例按**在线真人玩家数**计算，不把 Bot 算进投票人数。刀战也要求真人玩家分布在两个阵营。
- 一名获胜队员输入的第一个有效选边指令即生效，不要求获胜队内部二次投票。
- 不要将敏感 GSLT Token、服务器管理凭据放进压缩包或发在截图里。

## 八、v1.1.1 更新内容（修复）

- **修复编译错误**：源码缺少 `using CounterStrikeSharp.API.Modules.Utils;`，`CsTeam` 无法识别，v1.1 无法编译。
- **刀战开局判定**：`knife.cfg` 里的 `mp_warmup_end` / `mp_restartgame` 会额外触发回合开始 / 结束事件，以前可能被误判为“平局”或刀战已经开始。现在改为冻结时间结束（`round_freeze_end`）且不在热身中时才算刀战开始，并忽略重启产生的 `round_end`（原因 GameCommencing）。
- **`!swap` 带上 Bot**：以前只交换真人，场上的 Bot 留在原队，会出现两队混在一起的情况。
- **玩家全部离开时自动复位**：以前比赛中所有人退出后，插件会一直停在 LIVE 状态，新玩家无法 `!votestart`。
- **新增管理员命令 `!startreset`**：出现异常状态时可手动回到无限热身。
- **投票中有人退出会重新计票**：可能直接通过或直接失败，不用干等倒计时结束。
- **投票冷却改为从投票失败时开始计算**：以前从发起时算，25 秒的投票超时失败后只需再等 5 秒。
- **`!startstatus` 票数**：不再计入已离开玩家的票。
- **cfg**：刀战 / 正式比赛补上 `mp_roundtime`、`mp_roundtime_hostage`（人质图以前回合时长不对）；`practice.cfg` 恢复死亡掉枪 / 掉雷，以及默认近战武器。
- **install.sh**：部署中途失败时自动重启 CS2；额外备份旧插件 DLL；修正 .NET SDK 检测在 `pipefail` 下可能误报的问题。

## 九、v1.2.0 新功能：.drop 发刀

- 聊天输入 `.drop`：服务器给你发 5 把刀，从你脚下上方竖直落地，排成一个小圈，其他玩家可以去捡。你自己手里的刀不受影响。
- 发的是 `weapon_knife`。CS2 通常会换成发起者库存里装备的那把刀（带皮肤），所以别人捡到的是你的刀。**这一点取决于 CS2 本身，请实测确认**；如果捡到的只是默认刀，说明当前版本的 CS2 不会这样替换。
- 捡刀：玩家手里已经有刀时，需要对着地上的刀按 E 交换，或者先按 G 丢掉自己的刀再走过去。为此 `practice.cfg` 和 `live.cfg` 开启了 `mp_drop_knife_enable 1`，`knife.cfg` 中关闭。
- 限制：必须活着并在 T/CT；刀战（等待开始和进行中）不可用；正式比赛每回合每人一次；热身、投票、选边阶段每人冷却 30 秒（热身是一个不会结束的回合，按回合限制会变成整个热身只能用一次）。

## 十、v1.2.1 修复：比赛结束后卡在结算画面

- **原因**：旧版在比赛结束 8 秒后执行 `practice.cfg`。但这个 cfg 无法退出 CS2 的结算（Game Over）状态，右上角会显示 Warmup，画面却一直停在 VICTORY / 比分界面。服务器没有配置地图轮换时，CS2 自己也不会换图。
- **现在**：比赛结束 15 秒后执行 `changelevel 当前地图` 重新加载（创意工坊地图改用 `ds_workshop_changelevel`，要求地图在服务器托管的创意工坊合集里）。换图后插件自动进入无限热身，玩家留在服务器里，**不需要重启服务器进程**。
- **与 RockTheVote 的配合**：如果 RockTheVote 的结束投票选出了下一张图，并在结算后 `DelayToChangeInTheEnd` 秒切过去，插件的重载会自动取消。
- **建议检查 RockTheVote 配置**：它的 `EndOfMapVote.ChangeMapImmediatly` 默认是 `true`，含义是投票一结束就立刻换图，会在正式比赛还剩 `TriggerRoundsBeforEnd` 回合时直接切图。建议改成 `false`，让它在比赛结束后再换图。另外，比赛如果提前分出胜负（例如 13:0），可能还没到投票触发的回合就结束了，这时由本插件负责重载地图。

## 十一、v1.2.2：默认关闭友伤

- `practice.cfg`、`knife.cfg`、`live.cfg` 都加入了 `mp_friendlyfire 0` 和 `mp_teammates_are_enemies 0`。热身、刀战、正式比赛中，打队友（包括手雷、燃烧弹）都不掉血。
- CS2 竞技模式默认开启友伤，每次换图会恢复成开启。不过插件在换图后会执行 `practice.cfg`，所以仍然是关闭的（v1.4.0 之前 `server.cfg` 也会执行一次）。
- 插件代码除版本号外没有改动。只更新 cfg 的话，替换服务器上 `cfg/` 目录里的三个文件即可，不需要重新编译。

## 十二、v1.3.0：!help 指令表、!ct / !t 选边、无限加时

- **`!help` / `!commands`**：在聊天里列出服务器上所有玩家指令，包括本插件和 RockTheVote 的。按**当前阶段和当前玩家**着色：绿色 = 现在能用，灰色 = 现在不能用，并在括号里写明原因（例如"knife winners only""not during warmup""VIP only"）。最后一行列出其他服务器常见、但本服务器没有的指令（`!ready`、`!pause`、`!unpause`、`!tech`、`!gg`）。热身开场提示、`!startstatus`、开赛提示里都会提醒可以输入 `!help`。
- **`!ct` / `!t` 选边**：刀战胜方可以直接说要打哪一边，不用先想清楚自己现在在哪边、该 stay 还是 swap。`!stay` / `!swap` 仍然可用，开赛提示会写明胜方最终打 CT 还是 T。
- **无限加时**：`live.cfg` 开启 `mp_overtime_enable 1`。12:12 后打 MR3 加时（`mp_overtime_maxrounds 6`，每人 `mp_overtime_startmoney 12500`），加时打到一半换边时不暂停计时（`mp_overtime_halftime_pausetimer 0`）。`mp_overtime_limit 0` 表示加时次数不限：加时再平就继续，直到一方赢。
- **需要同时改 CS2MenuManager 配置**：见"二、玩家命令"里关于 `!ct` 冲突的说明。
- 需要重新编译部署（`bash install.sh`）。

## 十三、v1.3.1：创意工坊地图比赛结束后能正常重载

- **问题**：比赛结束后插件重载当前地图。创意工坊地图不能用 `changelevel`，旧版改用 `ds_workshop_changelevel 地图名`，但这个命令只对服务器托管的创意工坊合集（`host_workshop_collection`）有效。本服务器没有托管合集，所以在创意工坊地图上打完会卡在结算画面。
- **现在**：创意工坊地图会去 RockTheVote 的 `maplist.txt` 里找同名条目（格式 `内部地图名:创意工坊ID`），找到就用 `host_workshop_map ID` 重载；找不到才退回旧的 `ds_workshop_changelevel`。
- **往地图池加创意工坊地图时，冒号前要写地图的内部名称**（服务器上 `Server.MapName` 报告的名字，也就是 VPK 里 `maps/xxx.vpk` 的 xxx），不是创意工坊页面上的标题。例如 "Fy Snow"（ID 3592238209）要写成 `fy_snow_legacy:3592238209`。

## 十四、v1.4.0：Rush 地图上停用比赛流程

- **背景**：RockTheVote 地图池加入了 Valve 的 Rush 地图 `rush_001`（3v3，最多 6 人）。Rush 要在 `game_mode 6` 下加载才有 Rush 规则，由单独的 **ModeSwitcher** 插件负责：换到 Rush 地图前设 `game_mode 6`，换到其他地图前设回 `game_mode 1`。
- **本插件在 Rush 地图上**：不执行 `practice.cfg`（否则无限热身、随地买枪会覆盖 Rush 规则），`!votestart` 不可用，`!help` / `!startstatus` 显示 "Rush mode"。离开 Rush 用 `!votemap <地图>` 或 `!rtv`。
- **`server.cfg` 不再执行 `practice.cfg`**：旧版 `install.sh` 会在 `server.cfg` 末尾追加 `exec practice.cfg`，它在 Rush 地图上也会执行并覆盖 Rush 规则。v1.4.0 的 `install.sh` 会把这一行（连同旧注释）从 `server.cfg` 删掉；竞技地图的热身改由插件在换图 3 秒后执行。
- **新增 Rush 地图时**：同时加到本插件的 `RushMaps` 和 ModeSwitcher 的 `RushMaps` 里。

## 十五、v1.5.0：Rush 地图也有无限热身和开赛投票

- **改动**：v1.4.0 在 Rush 上完全停用插件，Rush 只有 Valve 自带的 120 秒热身（人齐后 5 秒就开），而且会继承上一张图 `practice.cfg` 的设置（热身计时器暂停、不发 C4）。现在 Rush 地图和竞技地图一样：换图 3 秒后执行 `practice.cfg` 进入无限自由热身，`!votestart` 投票通过后**不打刀战**（Rush 没有换边），直接执行 `rush_live.cfg` 开打。
- **`rush_live.cfg`**：先 `exec gamemode_rush.cfg` 恢复 Rush 自己的经济、回合时间和死亡不复活规则，再把 `gamemode_rush.cfg` 不管的 `practice.cfg` 设置改回来（发 C4、热身买东西收费、武器数量限制等），最后 `mp_warmup_end` + `mp_restartgame 1`。
- **`gamemode_rush_server.cfg`**：CS2 每次加载 Rush 地图会在 `gamemode_rush.cfg` 之后自动执行它。它在地图一加载就暂停热身计时器，避免插件执行 `practice.cfg` 之前热身就自己结束。
- 比赛结束后和竞技地图一样，15 秒后重载 Rush 地图回到热身；所有人退出也回到热身。
- `install.sh` 会安装并备份这两个 cfg。

## 十六、v1.5.1：空服时自动重载地图，防止手部动作慢动作

- **问题**：关掉休眠后，地图在空服状态下连续运行了约 9 小时。之后进服的玩家走路正常，但手上的动作（切枪、换弹、检视）变成慢动作。换一次图就恢复正常，确认原因是同一张图跑太久：游戏时间只在换图时归零，数值太大后浮点精度不够，动画就会变慢。
- **现在**：插件每 60 秒检查一次。服务器没有真人玩家，并且当前地图已经运行超过 1 小时（`StaleMapSeconds`），就重载当前地图（创意工坊地图也能重载，Rush 地图保持 Rush 模式）。日志里会出现 `[PugFlow] Server empty and <地图> has run X.Xh: reloading it`。
- **有人在线时不会重载**。如果大家在同一张图上连续玩了很多个小时，手部动作又开始变慢，换一次图即可（`!votemap`，或管理员 `/rcon changelevel <地图>`）。

## 十七、v1.6.0：改名为 CS2-PugFlow

- 插件原名 **WarmupStartVote**（仓库 CS2-WarmupStart-knife-round）。功能早已不止热身投票和刀战，现在管理整场朋友局（PUG）的流程，所以改名为 **CS2-PugFlow**。
- 改动：插件名 `PugFlow`，DLL 和插件目录 `plugins/PugFlow/`，聊天和日志前缀 `[StartVote]` / `[WarmupStartVote]` → `[PugFlow]`。
- **玩家指令全部不变**（`!votestart`、`!yes`、`!ct`、`!startstatus`、`!startreset` 等）。
- 从旧版升级：`install.sh` 会把旧的 `plugins/WarmupStartVote/` 移到本次备份目录里，防止新旧两个插件同时加载、每条指令执行两次。
