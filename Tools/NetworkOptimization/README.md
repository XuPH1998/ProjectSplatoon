# 网络优化验证工具

玩家协议 23 起，`NetworkOptimizationProbe` 改用每秒两次的游戏不可靠回显，并输出输入确认、预测暂停、重放统计与 CSV。旧报告中的 UTP RTT 仍是历史口径，不能直接与新版游戏往返混算。新增 `--matrix prediction --seconds 65` 覆盖 0/50/100ms 两进程地面弦化/潜墨场景；详细说明见 `Docs/ClientPrediction.md`。

只用于开发构建。生产网络通道、模拟步长和玩法参数不受这些工具影响。

1. 使用 Unity 6000.3.9f1，通过 `Splatoon.Editor.PrototypeBuilder.BuildWindows` 构建开发客户端。
2. 使用 Python 3 运行 `run_network_cases.py --exe <InkLan.exe> --output <报告目录> --matrix <矩阵> --seconds <时长>`。
3. 使用 `summarize_results.py --report <Reports/NetworkOptimization>` 汇总原始 JSON、Unity XML 和测量表。

矩阵：`smoke` 为无注入延迟的两进程；`representative` 为 0/0、50/0、100/1、200/3；`full` 为 50/100/200ms × 0/1/3% 丢包；`eight` 为 8 个独立进程，最后一人须等待房主实际入房 20 秒、已有 7 人在场且产生至少 1500 条墨迹后再补入，建议 90 秒。延迟非零时，每方向附加 ±10ms 抖动。`reconnect` 使用现有离开/重连驱动，建议 70 秒；`combat` 使用现有战斗、自然结束与下一回合驱动，建议 245 秒。

`eight-playing` 在上述 8 人条件上增加正式对局阶段要求；房主通过显式开发参数 `-inkPerfRound` 调用已有 StartRound，最后一个客户端只在日志确认 Playing 后补入。此模式用于正式对局晚加入验收；`eight` 用于练习场持续喷涂压力测试。

UDP 代理每个客户端使用独立上游 socket，在 UTP 可靠重传之前注入延迟与丢包。`--rtt` 表示双向注入量。当前 NGO/UTP 的 `GetCurrentRtt` 读取可靠流水线 `LastRtt`；输入为不可靠发送时，该值可能长期停留在补同步或连接阶段，不能把重复采样的均值/P95 当作持续实测 RTT。`verify_proxy_rtt.py --output <目录>` 使用独立 UDP 回显校准代理实际往返延迟，不添加游戏 RPC。Windows 的 UDP 10054 退出通知会计数，其他代理错误仍导致失败。工具只绑定 loopback。

`inkperf` 驱动由现有 `PrototypeSmoke` 生成输入；它会先按正常规则松开开火，再持续按住。四人以上的现有 `InkPerformanceSmoke` 会补充墨水并渲染 720p 场景，这是明确的测试夹具。探针记录每个玩家的权威 ShotSequence，房间人数和全员实际射击都通过才接受持续射击场景。

测试期间避免同时运行 Unity 编译或其他压力测试。客户端末帧与房主末帧不一定对应同一墨迹序号，持续涂墨时不能直接比较末帧哈希。两台物理设备的网络与视觉验收应单独执行。

`NetworkOptimizationProbe` 只在显式 `-networkProbe <秒>` 时启用，输出连接、初始同步、权威射击次数、输入积压、纠正、帧 P95/P99、GC、UTP RTT 和角色快照年龄。快照年龄使用 NGO 估计的服务器时钟，包含调度和同步滞后，不是独立测得的单向链路延迟。优化包另外输出各类热路径序列化字节、完整快照参考字节及补同步在途字节。

`update_config.py` 维护本次两个 Luban 全局配置源列；生成仍必须使用项目入口 `cmd /c Config\Luban\gen_luban.bat`，不要手改生成的 C# 或 JSON。
