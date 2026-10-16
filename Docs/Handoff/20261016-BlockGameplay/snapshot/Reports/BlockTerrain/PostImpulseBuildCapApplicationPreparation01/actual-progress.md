# 2026-10-16 用户要求交接停止；当前诊断已中断，进程已退出

用户要求上传当前改动到 Git、生成交接文档，再关闭 Goal、停止开发并转至另一台电脑。本次仅归档、提交和推送；上传后暂停 Goal，不标记完成。接力总入口为 `Docs/Handoff/20261016-BlockGameplay/HANDOFF.md`，以后续交接状态覆盖下方历史记录中的“正在运行”和“Goal active”。

Windows37 方块子弹显示修复已完成。唯一完整正式 steady250k 性能结果明确失败；其余三场正式性能未执行。CPU02 在约150秒 checkpoint 后中断，未生成正常终态或完整 CPU sidecar，退出原因未知；不得把中断诊断写成完整验收或已定位根因。4.29GB raw 留在旧机，离线提取器未执行，没有进一步性能补丁。

中断后19键两注册表视图与基线完全一致，未写注册表。仅恢复同项目 Editor78316 作只读核对，确认非Play、非编译、非构建且无脏场景后正常退出，原生 exit0（UTC 2026-10-16T13:22:11.3962710Z）；隐藏 keeper67316 已退出，本项目无Player或测试在运行，其他项目 Editor19136 未操作。停止前核对2944构建输入和359包文件全部匹配，零差异。

剩余四类：四场正式性能与积压修复；100ms/1%弱网移动纠正；六组独立Player联机与恢复；最终同包比赛/HUD/来源/完整报告。32米高塔及两万格级联专项、真实多机和人工观感继续排除。以下内容均为按各自时间、源码和包归属保留的历史记录。

---

# 2026-10-16 Windows37 首场性能未通过，正在同包 CPU 诊断

更新于21:09新加坡时间。Windows37方块子弹显示修复已交付，原18/18包内主相机显示证据及Windows37原包保持。steady250k正式180.108秒/7404帧/8独立Player/288350至334926格；帧时P95 74.611ms、P99 399.985ms，普通主攻击发布P95 7033.333ms，均未通过。其余三场未执行。GPU P95仅2.889ms，主线程P95 74.592ms；下一步定位随时间增长的主线程长帧及指令积压，不能把单次事务71.552ms当成攻击总响应。

正式测试全部8Player退出，原guard确认19键双视图偏好恢复、rawEqual/branchExistenceEqual全true，窗口审计未观测到Player位于Default。原结果保持，详见[首场Root观察](Windows37AcceptancePreparation01/PerformanceRun01/RootObservation20261016.md)。

当前CPU诊断入口为Windows37CpuDiagnosis02/CpuRun01，实际launcher12656/bootstrap90496/host10980，原180秒规模/八人/1080p/D3D12，开启已有CPU窗口Profiler，因此明确只作诊断，不作性能通过。第一次诊断入口缺少光标环境保护，在Player启动前被拒绝，原件保留。Editor92908及隐藏keeper29736已经重开并验证同一源码程序集身份/idle/隔离桌面；33144与45924已退出，不再复用旧PID。禁系统鼠标、不聚焦、不切用户输入桌面、无WMI/CIM/服务操作、串行、不提交发布保持。

四类验收仍未完成：四场性能、100ms/1%纠正、六组独立Player联机与恢复、最终同包比赛/HUD/来源/完整报告。高塔/两万格级联专项及两项外部验收仍排除。Goal继续active。以下历史记录按时间与包归属阅读。

---

# 2026-10-16 方块子弹显示修复已通过，Windows37 已交付为当前候选包

快照时间 2026-10-16 20:41（新加坡时间）。已优先完成用户反馈的枪械子弹不可见修复，继续使用方块弹丸。新增序列化实例化材质并修正失效缓存相机自动重取；九主武器共用路径，武器配置、弹道、扣费、伤害及建造拆毁未作本轮修改。

实际 Unity 编译绑定为700源码条目；fresh203真实10/10测试通过，Editor九武器双阵营18例GPU检查通过，文件与19项偏好全部恢复。Windows37正式Addressables/Development Player构建成功，359文件/322179584字节、2944构建输入逐项哈希通过；19项API/17项raw Editor偏好恢复。实际包为Builds/Blocks-20261016-block37/InkLan.exe。独立PlayerVisual01在RTX3060/D3D12/1920x1080通过18/18主相机背缓冲显示案例，禁用旧相机后的自动切换通过；Player/harness/bootstrap/launcher均exit0，19键双注册表视图偏好恢复。本轮原图已检查并保留，root-acceptance.json绑定全部证据。

包内探针使用真实武器配置构造合成InkShot，只证明显示，不能外推为付费攻击、权威碰撞、RPC或完整比赛全部通过。Windows36原包和既有比赛/HUD/性能结果保持原归属；四场性能、100ms/1%纠正、六组独立Player联机恢复，以及最终包完整验收/报告仍未完成，整体Goal继续active。高塔/级联专项及两项外部验收继续排除。

本轮初始构建包装器路径失败、Editor35536构建中退出、第一次恢复34028后退出均留原件，不记为成功。中断后raw偏好与基线一致、无注册表写入。最终用独立隐藏启动器45924恢复同项目Editor33144，隔离桌面仍CodexBlockTests20261013a、用户输入桌面Default；实际源码/程序集身份与通过回归的版本一致。后续复用33144，保留启动器；不再把35536记为存活。无WMI/CIM/服务操作，不聚焦、不切桌面、不捕获系统鼠标，不提交发布。

最短证据入口：[本轮验证报告](../CubeProjectileRepair20261016/Validation.md)、[Root实际签收](../CubeProjectileRepair20261016/root-acceptance.json)。以下为历史快照，按各自时间与包归属阅读。

---

# 2026-10-16 优先修复方块子弹显示；Windows36首场性能未通过

用户在Windows36手动验收反馈所有枪械弹道子弹不可见，并明确要求优先修复、子弹使用方块。已新增专用Resources实例化材质并窄修共用渲染器、失效相机自动重取；正在补九主武器双阵营实际像素回归。玩法、武器配置、扣费与弹道保持，Windows36原包和历史证据保留，新修复必须另出包并重新绑定。

原性能session17244已经终结exit1/native5，11:21:30.753084 UTC结束，禁止再记为运行中。首场steady250k实际180.446秒/12863帧/8人/290496至300012格，帧时P95 22.4084ms、P99 48.9536ms，主攻击发布P95 666.667ms，未通过；另外三场未执行。期间用户手动同包Player位于Default桌面，窗口审计记录该事实，因此此轮也不能作为无干扰性能结论。偏好guard为保护用户仍存活Player而未恢复；现在PID38716及InkLan已退出，正在保存用户后来手动保存的现值，处理旧pending，绝不盲覆写旧基线。原失败与原guard未恢复证据保持。

自然180秒比赛12门槛通过并完整恢复；Windows36正常1920×1080中文HUD实际图已检查通过（中文显示范围，不代表性能通过）。原22项关联回归实际覆盖保持历史结果；新显示修复验证、四场性能、弱网纠正、六组网络及新包最终报告仍未完成。专项高塔/级联与两项外部验收继续排除。所有运行串行，隔离桌面、禁系统鼠标、不聚焦、不切桌面、不提交发布、无WMI/CIM/服务操作。

---

# 2026-10-16 自然三分钟比赛通过并恢复；四场性能已派发

快照时间 2026-10-16T11:17:34.907208+00:00。自然比赛原session15578已实际终止exit0/native0，11:14:29.421188 UTC完成；summary的naturalRoundWorkflowObservedPassed=true、12个naturalRoundObservationGates全true。原日志观测首轮180.0270365秒，最终pink3599/blue765、revision2404；双方实际Finished、返回房间重开并进入下一轮，随后各自正常exit0。bootstrap/harness/guard均0，140次窗口归属采样中129次包含Player窗口，未观测到Player位于用户Default输入桌面；19键×两注册表视图restored/rawEqual/branchExistenceEqual均true，ownedPlayers全部退出。Root实际核验并记录NaturalRun01/root-terminal.json，SHA2af23d9773d28ff5ae95c2b5ff72202b04ffd971c267ef74476ed162081da69f。自然lane只证明自然流程；同包联机清空一致性和正常backbuffer中文HUD仍分别由后续网络/性能证据核验，不把原范围false标志改成通过。

Windows36正式包及完整来源绑定保持原样，关联22项已有真实通过覆盖。Root在自然lane完整退出/恢复后只派发一次四场性能，实际session17244仍运行；launcher95124/bootstrap76572、startedUtc11:16:48.536132 UTC，隔离桌面CodexBlockTests20261013a、用户输入桌面Default。PerformanceRun01、performance36.request.json和真实lane-manifest已建立，固定steady250k180秒/combat250k300秒/blast8 120秒/scattered100k180秒，尚无性能终态，不重复派发、不并发Unity测试/构建/网络Player。

后续尚余四类：四场性能、100ms/1%移动纠正、六组独立Player联机恢复及最终交付/HUD/报告；网络仍须补齐回合清空重建一致性。Docs/BlockTuningGuide.md已窄更新到实际Revision02入口，28个本地链接存在；玩法参数与所有冻结证据保持。高塔/两万格级联专项、真实多机与人工观感两项外部验收继续不计本次Goal。后台隔离、禁鼠标捕获、不聚焦、不切输入桌面、不提交发布及无WMI/CIM/服务操作保持。以下历史快照保留。

---

# 2026-10-16 Windows36同包绑定通过；自然三分钟比赛验收已派发

快照时间 2026-10-16T11:10:12.087398+00:00。Root逐文件核对Revision02 manifest的10项及61项source pins，随后实际执行纯文件binder，完整gate通过并独占写入actual-binding.json（SHA 06fb7198e248b8fdc02104944420a9545c037f0e07ca944d75b22684992539d6）。四处C#日期序列化只作同一带时区瞬间比较；原Windows36包、构建输入/记录、identity与历史回归均未改写，无重建。此项仅为来源核验通过，不等于比赛/性能/网络验收通过。

Root已实际派发同包自然比赛入口，exec session15578仍运行。原启动输出startedUtc=2026-10-16T11:09:29.036642+00:00，launcherPid6012、bootstrapPid91392，隔离桌面CodexBlockTests20261013a，用户输入桌面Default；请求与NaturalRun01/_desktop-launch已实际建立。尚无比赛终态、Player通过或完整偏好恢复结论，不重复派发、不与测试/构建/其他Player并发。保持禁系统鼠标捕获、不聚焦、不切输入桌面；不进行WMI/CIM或服务操作。

仍有5大类尚未整体关闭：功能类剩自然三分钟比赛和下一回合流程（22条关联回归已有实际通过覆盖）；4场性能；100ms/1%移动纠正；6组独立Player联机恢复；最终交付（正式Windows构建/资源包检查已通过，剩同包验收、中文HUD和最终报告/调参收尾）。高塔/两万格级联专项及真实多机/人工观感两项外部验收均不计本次Goal。以下历史快照保留。

---

# 2026-10-16 Windows36正式构建与包检查已通过；同包验收时间戳读取待修

快照时间2026-10-16T10:56:27Z。Windows36正式Addressables及Player构建均成功，Root已实际inspect完整359文件/322162861字节、资源catalog/bundles、程序集及19项偏好恢复，包身份SHA 9746a895889c39bf09e94e989e24e03b2d4bcdd6bf0367316b32e93eeeeb00cf。输出Builds/Blocks-20261016-block36/InkLan.exe；该包尚未取得比赛/网络/性能验收。

实际同包bind被旧字符串等值检查拒绝，未写actual-binding或启动Player。Root重新实际因果validate并递归比对：build-input与package-identity的partialImpactRegression均完全相等；build-record仅4个历史时间戳由UTC转换成+08:00并去小数尾0，带时区解析后全为同一UTC瞬间。独立只读审查复核一致；证据在Windows36ActualBindingFailure01。新Revision只修这四个明示路径的时间语义读取，其余完整对象/字节/哈希/恢复与玩法门槛保持；既成包和所有旧记录不改、不重建。当前无Unity测试/构建/Player运行。

22条关联回归实际因果覆盖已闭合，功能类仍待自然三分钟比赛/回合流程；四场性能、弱网纠正、六组独立Player及最终交付仍待同包验证。高塔/级联专项与两项外部验收排除；后台隔离、禁系统鼠标、不聚焦、不切输入桌面、不提交发布保持。以下历史快照保留。

---

# 2026-10-16 关联22项因果覆盖实际通过；Windows36正式资源与候选包已开始构建

fresh201唯一击退/空中后坐力叶实际Passed，XML20261016-104421.xml SHA ac9594dd33e4d08cb0aa4d1c9cb3f642db13de02d15a9f2fcc639c2d15323176。原session69714实际终止exit0/runnerExit0，文件及19项API/17项raw偏好全部恢复，wrapperError/restorationError均null；preservation SHA 479a29cea4dcc67de0a6d502ede2d036d9dcb306d27120a18bacf9c6c2fea682。Root实际最终因果validate通过：原15通过+fresh200六通过+fresh201一通过唯一覆盖原22；历史失败保留，不声称22都在最终图重跑。真实终态与覆盖结果在Fresh201TerminalActual01，coverage SHA a91de0577bac28275e3e4db8a46984d6d02a3a2a5dd5214f7df5986842fac68c。

Root核对新构建/验收准备全部61 pins及独审，并于10:48:03取得独立空闲Read。Windows36输入已实际绑定到fe7507编译/0666完整图/真实201恢复，build-input SHA 9dc4c63619dea034e89607743660522e0fb19ca8e85ecde2474c95afdcfec8aa。唯一Queue派发CLI返回0，10:49:49真实build-start已写；正式Addressables及Builds/Blocks-20261016-block36候选包构建进行中。Editor35536通过当前进程查询确认仍live，Root只读观察session87799仍等待；尚无包完成、验收或交付通过结论，不重派构建、不并发Player。

五类首版尚未整体关闭：功能类仍待自然三分钟比赛和回合流程；四场性能、弱网纠正、六组独立Player及最终交付待同包验证。高塔/级联专项与两项外部验收继续排除；后台隔离、禁系统鼠标、不聚焦、不切输入桌面、不提交发布保持。以下历史快照保留。

---

# 2026-10-16 单项起点修复实际编译通过；fresh201单叶复测已启动

快照时间2026-10-16T10:42:35Z。918d单方法夹具修复已真实编译，status01零错误；首次postread01通信120秒超时原件保留，postread02于10:38:19成功确认原隔离Editor35536空闲、无焦点、输入桌面Default。Runtime/Config完整DLL身份原样，Tests实际新MVID12d38065-78e3-4319-ab1a-95873b007c89。实际compile-binding SHA fe7507fe085bbcececc2d9fe1b0b0e85793c169800b16f1eb20528b237bcd03f，完整699源码/meta图0666b550cc02614e8888221570b6d83fbf3be4f3bfdd6a02d5518cc168f016ec，证据在KnockbackFixtureRepairApplication01。

只复测原击退/空中后坐力失败叶：fresh201实际session69714，10:39:22启动，Root已独立核验真实加载身份、原启动参数、单叶选择及完整图，见Fresh201DispatchActual01/dispatch.json。当前进程live、尚无终态/恢复；不重复测试、编译或并发构建/Player。原fresh200已完整恢复并实际纯审计6P/1精确F；原失败保留。新的最小最终因果审计要求原15P+200六P+201真实一P唯一覆盖原22，实际validate尚未调用，不把静审记为复测通过。

五类首版验收仍未整体关闭；高塔/级联专项与两项外部验收继续排除。后台隔离、禁系统鼠标、不聚焦、不切输入桌面、不提交发布保持。以下历史快照保留。

---

# 2026-10-16 精准复测6通过、1夹具失败且完整恢复；起点窄修已应用，等待唯一编译

快照时间 2026-10-16T10:34:27Z。fresh200尾部Clear与FinishRound均实际Passed，Root核验精确叶、启动关联、completed摘要及raw/canonical XML，见Fresh200Partial01/publication-004-proof.json和publication-005-proof.json。原session67421已实际终止exit1，runnerExit3；10:30:54完整恢复，wrapperError/restorationError均null。Root实际调用新桥prior200纯归档审计通过：7真实执行、6 Passed、唯一095936原夹具断言Failed，preservation SHA b6092005f42f2ae6d0c3bb84c2a264ec605a14aceccfa41452b58b8589417c95，见Fresh200TerminalActual01。旧失败保持Failed，不改写。

Root在原运行恢复后以独立Read确认同一隔离Editor35536空闲、无焦点、输入桌面Default、D3D11及原三DLL身份，再实际应用918d候选：仅将一个Tests方法的水平起点移入地图并增加两个真实地形/完整pose前置断言，全部原行为断言和Timeout保留。699源码/meta已与0666b550cc02614e8888221570b6d83fbf3be4f3bfdd6a02d5518cc168f016ec完整图逐文件复核，仅该源码变化。源和meta已备份，10:32:16仅一次编译已触发。当前status观察session21121仍live，尚无新编译绑定或fresh201测试结果，不重复编译、不构建/启动Player。

单叶fresh201复测桥已独审并按03a6aa00c8f0c2e0f11064715116981635a741b7f7d4b65017bba5c5e7ee2c08原字节安装到KnockbackFixtureRepairApplication01；尚未派发。待实际编译清零且独立postRead身份绑定后只复测失败叶。五大类首版仍未整体关闭；高塔/级联专项与两项外部验收继续排除。后台隔离、禁系统鼠标、不聚焦、不切输入桌面、不提交发布保持。以下历史快照保留。

---

# 2026-10-16 精准复测4通过、1夹具断言失败、2待结果；窄修候选已核

快照时间 2026-10-16T10:16:58.727113+00:00。fresh200新增BudgetedExplosionCompletesItsFullStableAndFallingCandidateSnapshot实际Passed，XML20261016-101334.xml；Root核验精确叶、completed摘要、原启动关联与raw/canonical字节，见Fresh200Partial01/publication-003-proof.json。累计本轮4 Passed、1夹具越界断言Failed、Clear/FinishRound两叶尚待终态。原session67421仍live，尚无整体终态和完整恢复，不重派或并行Unity操作。

Reports/Fresh200KnockbackFixtureRepairPreparation01已交付待修候选：只把该失败方法origin从(3600,20,3600)改为(0,20,0)，加真实terrain初始化和完整pose合法性两个前置断言，原全部行为断言/Timeout不变，候选+352字节、SHA918dcb0f7cec9825ce76b17c7f14b4c13aa969d9ecafa7d86a22d3d2aee5ce82。Root独立按目标方法精确反变确认全部原字节和699源码/meta仍匹配正在运行的编译，见Fresh200FixtureRootVerification01/root-verification.json；尚未应用或编译。高度20保留，当前地图最高碰撞4.725、角色全高最大1.8，air起点30+1.8<32。

现有要求fresh200全七Passed的Windows36候选因果链当前不可执行；待原运行完整恢复后应用窄修、实际复测与因果覆盖核对再绑定。调参主文档保留，新增TuningGuideSourceReview20261016/README.md补充真实分帧来源，不认定验收通过。五大类首版未整体关闭，后移专项/外部验收继续排除；后台隔离、禁系统鼠标、不聚焦、不切输入桌面、不提交发布保持。以下历史快照保留。

---

# 2026-10-16 精准复测3通过、1夹具断言失败、3待结果

快照时间 2026-10-16T10:08:49.977728+00:00。fresh200新增BuildBacklogAndReadyFallingBothPublishWithoutDroppingCommands实际Passed，XML20261016-100633.xml；Root核验精确叶、completed摘要、原启动关联与raw/canonical字节，见Fresh200Partial01/publication-002-proof.json。此前Safety两项Passed；SpecialKnockback实际断言失败仍保留，旧origin3600越32×64地图触发Blue_1 x=-3安全回退的只读原因明确。

原session67421仍live，尚余BudgetedExplosion/Clear/FinishRound三叶待终态和完整恢复；没有在运行中改Assets或编译，没有构建或启动Player。夹具窄修仅在Reports准备，待运行恢复后应用并实测。原source22 15P/7默认超时Failed仍保持原件，不能执行要求fresh200全七Passed的旧候选因果门。五大类首版未整体关闭，后移专项/外部验收继续排除。以下历史快照保留。

---

# 2026-10-16 精准复测2通过、1真实断言失败；尾四项继续

快照时间 2026-10-16T10:06:31.110584+00:00。fresh200第三叶SpecialKnockbackAndAirRecoilMoveThroughCollisionAndReset实际Failed，XML20261016-095936.xml；Root核验精确叶、启动关联、completed摘要和raw/canonical原字节，见Fresh200ActualFailure01/failure-proof.json。失败是Force causes actual movement断言，Expected >3602而实际-3，不是默认框架超时。前两项Safety空中/墙面均实际Passed。

只读诊断定位旧夹具origin=(3600,20,3600)越当前TrainingGround 32×64米地图；完整pose安全判定触发本队Blue_1出生点(-3,.05,28)回退，与实际值吻合。拟保持真实terrain与全部移动/碰撞/后坐力断言，只把本方法场地移入地图并加起点合法性断言；候选在Reports准备，运行中不应用Assets、不重编译。

原session67421仍live，当前已启动BuildBacklog，尾四publication叶继续串行。当前尚无完整fresh200终态/恢复，不运行要求七项全Passed的因果门、不构建或启动Player。原source22为15P/7默认框架超时Failed且全部已执行/恢复的证据保持，不把原失败改写成通过。五大类首版仍未关闭；高塔/级联专项及两项外部验收不计Goal。隔离桌面、禁系统鼠标捕获、不聚焦、不切输入桌面、不提交发布保持。以下历史快照保留。

---

# 2026-10-16 七项精准复测已有两项通过，尚余五项待结果

快照时间 2026-10-16T09:54:15.776128+00:00。fresh200原同次Safety class两项已真实Passed：TripleInkstrike空中/墙面纸片受力拒绝与重放；终态XML20261016-095218.xml，Root核对精确叶、启动参数、实际completed状态、raw/canonical字节及摘要一致，见Fresh200Partial01/partial-proof.json。原source22仍保留15P/7默认框架超时Failed；新的两项通过为Timeout-only编译后实际复测结果，尚余五项待取得通过，不改写原失败。

实际session67421仍live，串行等待原Editor清理并继续其余五selector；当前不宣称fresh200完整恢复或因果覆盖闭合，不构建或启动Player。五大类首版验收尚未整体关闭，后移高塔/级联专项和两项外部验收仍排除。隔离桌面、禁系统鼠标捕获、不聚焦、不切输入桌面、不提交发布保持。以下历史快照保留。

---

# 2026-10-16 原22项真实闭合15P/7默认超时；七入口已实际编译，fresh200正在复测

快照2026-10-16T09:43:56Z。fresh199原最后3项全部真实默认180000ms超时，session1472终止exit1、runnerExit3且完整恢复。Fresh199DispatchActual01/source22-closure.json实际聚合原196+late6+198+199为22唯一叶15P7F，无未启动项，timeoutOnlyRepairEligible=true；原失败/外层观察/回调清理错误保持。

Root已实际应用两份Tests源的七处Timeout(600000)，+119字节，选中属性逐条反变完全还原原字节。唯一实际编译通过；Runtime/Config完整DLL与MVID原样、Tests新MVID473d9210-53cd-436e-bbef-f9bd60b83884，全部meta和方法正文保持。新绑定 DefaultFrameworkTimeoutConsolidationApplication01/compile-binding.json SHA12339c53e62395bbc26ca1f2d173fa6247c9fc012b35ebeb1ee847605b54e99a，aftergraphf098969a278af8559a38109df767a131745ad579d4eeb78b69be9c143fb757ac。

fresh200实际session67421正在原隔离Editor运行，精准7失败叶/6selector，首Safety class已启动，无新终态结果。不要重复派发，不把编译成功记为测试通过；需要同一运行完成及完整恢复后才能因果闭合。五大类Goal保持未完成，范围/隔离/禁鼠标/串行/不提交发布不变。以下历史快照保留。

---

# 2026-10-16 终态更正：fresh198已结束并完整恢复

核验时间 2026-10-16T08:58:44Z。实际session84821已exit1，fresh198为3 Passed、1默认180000ms框架总超时Failed；随后完成回调清理超过原180秒观察界限，最后三项没有提交。恢复证据 Fresh198TerminalActual01/terminal.json：restored=true、pendingRestoration=false；恢复后独立Read确认Editor空闲及原编译身份一致。不得再把该session记为运行中。

原196+同次late6+198累计22项中的15通过、4超时失败、3未启动，共7项待通过；首版五大类验收均尚未整体关闭。下一步只准备原最后三项同源续跑，原三超时/19复用+3复测固定链不适用，不提前应用Timeout候选或重跑通过项。高塔/级联压力整组和两项外部验收按用户要求排除。此更新只记录实际状态，未运行新Unity测试/构建/Player，Goal保持active。以下历史快照保留原归属。

---

# 2026-10-16 新增一项默认框架总超时；累计15通过、4超时、3待终态

快照时间 2026-10-16T08:51:30.897601+00:00。fresh198第四项BuildBacklogAndReadyFallingBothPublishWithoutDroppingCommands取得真实Failed，XML20261016-084904.xml；唯一失败信息为默认180000ms总超时，leaf251.595806秒、空stack，Root已核精确叶、actual status和raw/canonical原字节。前三项均Passed。与原196及同次晚到结果合计原22项现为15 Passed、4默认框架总超时Failed、3项后续待终态，共7项仍需通过结果。

原三属性／19P3F／19复用＋3实跑的准备链已不适用于新真实结果，其固定门应拒绝，不能执行、绕过或改写原失败来推进。待原尾部最后3项真实完成并完整恢复后，只按实际失败清单一次性窄调整测试入口总Timeout并复测对应失败项；现已涉及SpecialWeaponParityPlayTests.cs与BlockTerrainPublicationTests.cs，Runtime/Config、方法正文、等待/断言及玩法/性能门槛必须保持。未预判最后3项结果。

Root继续只观察原fresh198真实session84821，不重派、不取消、不并发Unity/测试/构建/Player，不修改Assets。原196完整恢复与partial纯文件闭合继续有效且原runner1/观察失败保留。五大类Goal仍未整体关闭，后移高塔/级联压力及外部两项保持排除。隔离桌面、禁鼠标捕获、不聚焦、不切用户输入桌面、不提交或发布保持。以下历史记录保留其原归属。

---

# 2026-10-16 发布失败保护通过，累计15项通过

快照时间 2026-10-16T08:45:33.574946+00:00。同图fresh198前3项已实际Passed：默认与兼容重力调度，以及真实退休草案被拒绝时保留已发布逆转屏障。第三项原XML为20261016-084216.xml；Root独立核验精确叶、完成状态、raw/canonical XML一致，见 Status198Partial03/partial-proof.json。原22项累计15 Passed、3项原默认框架总超时Failed、4项后续待终态，共7项尚未取得通过结果。

Root继续同一真实session84821，剩余为BuildBacklog、BudgetedExplosion、Clear、FinishRound四项。原196恢复已完成，原失败/外层观察超时均保持；当前198仍未整体终态与恢复，不执行聚合门、不应用三属性、不并发测试/构建/Player。后续新197/audit/build及五验收入口均已Reports-only冻结与独审，尚未运行。

五大类验收保持未整体关闭。高塔/两万格级联压力整组及两项外部验收按用户要求排除；隔离桌面、禁鼠标捕获、不聚焦、不切用户输入桌面、不提交或发布保持。以下旧记录保留。

---

# 2026-10-16 尾部两种重力调度均通过，累计14项通过

快照时间 2026-10-16T08:37:14.667900+00:00。fresh198补跑原未启动七项的前两项已真实Passed：默认ThirtyTwo与CompatibilityEight完整攻击/落定用例，XML分别为20261016-082826.xml及20261016-083527.xml；精确叶和计数由Root独立检查，见 Status198Partial02/partial-proof.json。加上原196与同次晚到六项，本轮22项累计14 Passed、3项原默认框架总超时Failed、5项后续待完成，即8项尚未取得通过结果；五大类Goal仍未整体关闭。

原196已完整恢复且原exit1/观察失败保留；其真实纯文件partial闭合通过。Root继续只观察fresh198真实session84821，不重派通过项、不并发Unity/测试/构建/Player，不应用三属性候选。当前尾部五项及完整恢复未完成，不能执行聚合19P/3F门或开始最终三叶复测。候选的三处属性精确逆变已由Root独立复核，全部方法正文/断言不变；源码尚未应用，不能记录为已修复或复测通过。

后续正式Windows36/六网络/四性能/自然比赛入口按实际partial+晚到+tail来源在新Reports目录窄适配，均未实际构建或运行。用户后移高塔/两万格级联压力及两项外部验收不计Goal。保持隔离桌面、禁鼠标捕获、不聚焦、不切用户输入桌面、不提交或发布。以下旧时点记录保持。

---

# 2026-10-16 已恢复原196；尾部补跑首项通过，累计13项通过

快照时间 2026-10-16T08:29:42.982553+00:00。原196的restore-only实际exit0，10个受保护文件与19项偏好API/raw完全恢复；完整699源码/699metadata与三份DLL继续匹配compile51。原runnerExit1、raw008观察超时和原3项默认框架超时失败全部保留。独立真实恢复证据在 Fresh196RestorationRecovery02/terminal.json；原partial加同次late6的纯文件闭合实际通过，见 Fresh196PartialClosureActual01/closure.json。

Root只补跑原未启动7项，fresh198真实session84821，原compile51/graph57、隔离桌面及无焦点身份已在启动前复核。第一项ActualDefaultThirtyTwoWindowPreservesCompletePublicAttacksAndLanding已实际1/1 Passed，XML 20261016-082826.xml；其余6项尚无终态。本次22项累计13 Passed、3项原默认框架总超时Failed、6项待完成，待通过数为9；没有把原196改写成全完成。尾部原运行及恢复结束前不应用测试属性、不并发测试/构建/Player。

首版5大类仍未整体关闭，用户后移的高塔/两万格级联及两项外部验收继续排除。三属性候选及下一阶段入口只做Reports准备，不代表复测或交付通过。保持禁系统鼠标捕获、不激活窗口、不切用户输入桌面、不提交或发布。以下保留各旧时点记录。

---

# 2026-10-16 本轮累计12项通过、3项框架超时，7项尚未启动

快照时间 2026-10-16T08:19:41.120421+00:00。本轮22项关联回归已有12 Passed、3 Failed、7项尚未启动。新增6项爆破生命周期在同一次原Unity调用中全部Passed（XML 20261016-081039.xml）；原父runner在1200秒外层观察超时后exit1的事实保留，没有重跑原类或把父runner改为成功。晚到XML和实际终态的独立关联见 Fresh196LateTerminalActual01/late-terminal-evidence.json。

3项原失败均记录默认180000ms框架总超时，尚未复测通过。原196恢复尚未完成：普通sandbox首次restore-only未发现Pipeline的原错误保留；同用户环境第二次restore-only已通过preflight，真实session42645正在恢复偏好。恢复完成前不启动任何新测试、Player或构建。后续仅补跑原未启动7项，再应用三处测试Timeout属性并实际复测，不改变方法正文或玩法/性能门槛。

首版Goal仍有5大类未整体关闭：功能与完整比赛、四场性能、弱网纠正、六组联机恢复、最终Windows包/HUD/报告。高塔/两万格级联压力整组及两项外部验收按用户要求排除。保持隔离桌面、禁鼠标捕获、不聚焦、不切用户桌面，不提交或发布。此快照覆盖下方较早进度，旧记录保留。

---

# 2026-10-16 fresh196累计6项通过、3项默认框架总超时；同一原运行继续

快照时间 2026-10-16T07:47:30.559541+00:00。本轮22叶/16筛选已有6 Passed、3 Failed、另13叶未完成。新增Passed为PreparedCohort与PreparedLegacy两条实际克隆/证明迁移路径；第三条失败为既有SpecialKnockbackAndAirRecoilMoveThroughCollisionAndReset，原XML 20261016-074359.xml仅记录默认180000ms框架总超时，duration243.249471秒，未推断功能通过。前两条新Impulse同类原失败保持。

Root继续观察原session25936；完整真实终态与现场恢复前不应用测试属性、不派发其他测试/构建/Player。仅Timeout属性的候选将按三条真实失败准备，方法正文、碰撞/轮廓/重放断言与玩法阈值保持；候选尚未应用或复测。五类Goal验收仍未整体关闭，tower32m/cascade20k及两项外部验收保持排除。隔离桌面、禁鼠标捕获、不聚焦、不切用户桌面保持。

---

# 六目标修复的实际进度

最新进展 2026-10-16T07:26:15.753571+00:00：PaidCohortAndLegacyBuildsMatchTheOriginalFullOwnedPath 实际1/1 Passed，XML `../UnityTests/20261016-072438.xml`；完整对照运行472.43秒。fresh196累计4 Passed／2框架总超时Failed／16叶待完成，原运行session25936继续。两属性修复候选已由Root逐字节核验，尚未应用，须等整轮终态与恢复。以下保留较早进展。

最新失败补充：fresh196 的新大招安全组已取得真实 XML `../UnityTests/20261016-071424.xml`，两叶均 Failed，唯一失败消息为测试框架默认180000ms总超时；真实叶分别235.48秒和224.57秒。当前为3 Passed／2 Failed／17叶待完成，不把超时改判为通过。已安排仅该fixture显式生命周期 Timeout 的 Reports 候选，45秒初始化等待、全部功能断言与游戏内响应/性能阈值保持。Root仍观察同一session25936，须等整轮终态和完整恢复后才应用任何源码修复。

实际回归最新进展：fresh196 前三项均完成真实终态，均 Passed：原失败 Wall 叶（XML `../UnityTests/20261016-065317.xml`）、`PaidFloorCapOnAPartiallyDetachedColumnReacquiresTheFinalPieceCertificate`（XML `../UnityTests/20261016-065724.xml`）及 `PaidEpsilonFloorCapKeepsAnAlreadyPublishedSettlingPrefix`（XML `../UnityTests/20261016-070415.xml`）。两个新建造叶真实用时分别244秒和239秒，包含 PlayMode/域重载；terminal summary 的跨重载计时不能当作完整叶耗时。Root保持同一session25936，后续19叶及最终恢复尚未完成；不记22全过。

2026-10-16，Root 实际完成 prepare、隔离空闲 preread01、六文件应用、一次 recompile01、成功 status02、成功 postread02 和 compile bind。首次 status01 的 HTTP400 与首次 postread01 的120秒超时均保留，没有因此重复编译或重启Editor。

- 实际只改变六份现有源码，另外693份源码和全部699份metadata保持。六源码/六metadata共12份现场备份已核验。目标内容是后置大招击退的最终几何检查、建造cap后的真实支撑证明重算、Settling续跑保护、墙面测试可达首附着构造和关联测试；PaperTraversal语义版本7→8，wire版本保持37。
- 实际Unity编译通过，无编译错误。`compile-binding.json` SHA256 为 `51f31bdc8a276371885c3f20fa188de9de38df769c64d9805fa1ecb69fd5a6fd`；完整699/699图canonical为 `57c9149c29e7860c3512da6d89c5081849be720b0a93891b30ad80ff8a1b63bf`。
- 实际Runtime MVID `4544ab51-dad4-472c-b52c-c434979cdf21`，Tests `21935c6c-d800-45f6-8dc1-6a0d7182eaa9`；Config MVID及磁盘字节保持。绑定时Editor35536位于隔离桌面、用户输入桌面Default、application/gameView focus均false，测试/编译/构建均空闲。
- Root已实际派发 `RegressionTriage/LatestSourceFullRegression15/Runs/fresh-fixtures-196`，22真实叶/16 selectors，原生exec session25936。顺序为原失败Wall1、新Cap2、新Impulse2、直接既有Metadata3/Special1、原剩13。写此记录时实际runner仍运行，不计为22通过；同一真实结果结束并完成10文件/19偏好恢复后再判断。
- 已完成的Natural6和ReservationSpatial6保留各自原结果；原图22通过/1失败/13未派发记录保持。没有把旧结果转为新六目标图的实跑通过。

Windows36正式资源构建模板及六组联机入口已准备，均尚未执行。后续必须绑定本次真实回归、新包完整库存与模块身份。Goal仍active，首版范围及高塔/级联压力后移规则不变，不提交或发布。
