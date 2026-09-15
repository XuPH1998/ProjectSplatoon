# 非房主预测与游戏往返测量

本次修改保留 Host 权威、60Hz 模拟、30Hz 网络 tick、移动和武器参数。玩家协议从 22 升到 23，内容签名和 LAN 房间兼容检查拒绝旧包混连；墨迹协议仍为 8。

## 运行行为

- 初始/恢复墨迹快照未完整应用时暂停本地预测；输入仍发送，恢复后从最新权威角色状态重放未确认输入。
- 正常对局以已经应用的权威墨迹预测移动。全图墨迹序号暂时落后只登记一次待补齐重放，不阻止潜墨或新输入；补齐时使用最新收到的权威角色快照和确认序号。
- 实际脚下墨色变化仍会修正速度、潜墨资格及资源。死亡和新生命周期立即应用，输入超时清理旧历史并等待新回执。客户端没有新增伤害、涂墨或计分权限。
- 历史重放更新移动胶囊与模拟状态，不烘焙中间弦化姿态。客户端在 LateUpdate 更新最终姿态的命中代理与画面；房主继续在权威弹丸检测前生成命中几何。
- 保留既有纠正平滑、硬纠正阈值和形态切换规则；未加入人为房主延迟、预测墨弹或历史命中补偿。

## 延迟与诊断口径

HUD 每秒两次通过游戏连接发送不可靠回显，客户端使用 `Time.realtimeSinceStartupAsDouble` 记录往返。这个时间包含传输、两端网络更新与主线程调度，不等同于网卡 Ping。重复、过期、未知或乱序的旧回复不刷新显示；三秒没有有效回复显示“暂无有效测量”。房主显示“房主 · 本机裁决”。采样不依赖窗口焦点、鼠标锁定或游戏菜单。

开发版本通过 `-networkProbe <秒> -networkProbeOutput <完整JSON路径>` 启用现有探针。不附加 `-lanSmokeHost/-lanSmokeClient` 时仍由玩家正常操作房间与角色。每个进程独立输出 JSON、100ms 间隔 CSV、错误列表和结束截图；到指定时长自动退出。

|指标|含义|
|---|---|
|rttSamples / rttAvailable|有效游戏回显样本数 / 最后一次观测时是否新鲜；均值和 P95 每个新回显只计一次。无样本时数值 0 不是零延迟结论。|
|inputAckSamples / inputAckMeanMs / inputAckMaxMs|本地输入生成到对应权威确认的耗时，包含等待发送、网络、房主输入队列及快照回程。重放不会重复计数，超时/旧生命周期输入丢弃。|
|initialSyncPauseSeconds / inputTimeoutPauseSeconds|按客户端单调时钟累计的暂停时间；初始同步与超时重合时只计入初始同步。|
|speculativePaintReconciles / paintReplays|墨迹序号未追上时仍执行的普通纠正次数 / 初始同步恢复或依赖补齐后的补充重放次数。|
|totalReplaySteps / maxReplaySteps|重放总步数 / 单次最大重放步数。|
|corrections / maxCorrection / correctionDistanceTotal|超过 1cm 的纠正次数 / 最大纠正距离 / 总纠正距离，单位米。不能仅凭次数判断画面是否可见拉回。|
|frameP95Ms / frameP99Ms|该进程帧耗时；房主和客户端分开比较。|
|paperPoseVersion|CSV 记录的当前英雄轮廓采样版本，切换英雄后可能重置；不等于逐帧 CPU 耗时。|

输入确认、重放和暂停累计值属于当前本地玩家网络对象，重新连接后重新累计。RTT 和帧统计属于探针进程，开始连接后的前三秒不进入均值/P95。CSV RTT 为 -1 代表暂无有效测量。快照年龄继续沿用 NGO 估计时钟，不能解释为实测单向延迟。

Unity Profiler 增加 `Splatoon.Prediction.Reconcile` 和 `Splatoon.Paper.Sample` 标记。用于检查重放中的模拟耗时与实际轮廓采样耗时，开发探针本身有少量采样开销。

## 验证入口

- Unity EditMode：`GameplayLatencyTests`、`ClientPredictionTests`，加原有游泳、空中弦化、落地、穿行、武器与网络测试。
- Unity 中进入真实 Boot/TrainingGround 的 Play Mode：`SwimPlayTests`、`InkImpactHostPlayTests`，确认房主命中网格、跳跃、生命周期和弹丸仍正确。
- 独立进程：正式入口 `Splatoon.Editor.PrototypeBuilder.BuildWindows` 构建后，执行 `python Tools/NetworkOptimization/run_network_cases.py --exe <InkLan.exe完整路径> --output <报告目录> --matrix prediction --seconds 65`。
- prediction 矩阵为 0/50/100ms 标称 RTT；非零档沿用每方向 ±10ms 抖动。驱动在出生点附近进行普通移动、中立地面弦化、己方涂墨潜行、敌方覆盖、死亡与重生，房主同时在远处持续涂墨。测试夹具只在显式 `-inkSmokeCase prediction` 时启用。

真实双机验收要核对整个包版本、固定分辨率和质量，交换房主，分别检查持续地面潜墨、墙游、起跳落地、脚下敌方覆盖、持续喷涂和断线恢复。各端保存独立报告，在同一时间段对齐纠正、帧耗时和墨迹进度。注入档位包含代理调度开销，不能把标称数值当作实际往返时间。编辑器、同机独立进程、两台物理机器的结果必须分开。

## 公平性后续

此次首先减少非房主额外的预测等待与重复轮廓计算。房主仍直接运行权威模拟，因此仍存在时间优势。后续可单独实现可见墨弹预测，再设计适用于飞行弹丸的有界延迟补偿；需处理发射时刻、弹道、历史命中体及躲入掩体后被命中的取舍。本次不承诺完全公平，也不改动命中裁决。

## 本轮验证结果（2026-09-15）

- Unity 142 / 142 用例通过：138 个 EditMode 用例，以及 4 个通过测试入口进入真实 Boot/TrainingGround Play Mode 的房主场景。
- 正式 Windows 构建成功，交付目录为 `Builds/Windows-Prediction/`。343 个输出文件与构建源的 SHA-256 一致；分发时复制整个目录，两端使用新包。新房主接入旧协议 22 客户端的拒连验证通过。
- 同机独立房主/客户端的 0、50、100ms 标称 RTT 场景全部通过，实测游戏往返均值分别为 17.82、77.97、125.56ms；运行错误为 0，墨迹最终序号均为 133 且所有权哈希一致。
- 三档最大模拟纠正距离为 0.0417、0.8372、0.8684m。50ms 档大幅纠正位于重生附近；100ms 档最大事件尚未定位，不能据此宣称所有拉回消除。没有同条件旧包性能对照。

详细报告与原始证据保存于 `Reports/ClientPrediction/README.md` 和 `Reports/ClientPrediction/NetworkFinal/`。这些运行产物按仓库规则仅保存在本地。真实双机手感、交换房主、墙游及断线恢复人工验收仍需单独记录；房主时序优势没有消除。
