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

最短证据入口：[本轮验证报告](CubeProjectileRepair20261016/Validation.md)、[Root实际签收](CubeProjectileRepair20261016/root-acceptance.json)。以下为历史快照，按各自时间与包归属阅读。

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

# 2026-10-16 原22项已全部执行；七项默认超时入口已实际编译，正在精准复测

快照时间 2026-10-16T09:43:56Z。原最后三项已由fresh199真实执行，全部为精确默认180000ms框架总超时；实际session1472已终止exit1，原runnerExit3，09:27:24.908320Z完整恢复。原196、同次late6、198及199的22个唯一叶已通过实际纯文件聚合审计：15 Passed、7默认超时Failed、无未启动项。完整恢复、原失败和观察/清理错误保持，见 Fresh199DispatchActual01/source22-closure.json（SHA fcad0c475dcc7b8be34ee6dac4d62c1f87384ced438ce30c5b3ddd81caed7caf）。

Root已按真实七失败清单在两份Tests源码仅增加七处Timeout(600000)，合计119字节；全部源/meta先备份，所选属性逐条反变与原文件字节一致，方法正文、内部等待、断言、其他源码和699 metadata保持。唯一实际编译成功，最终独立Read09:39:18确认同一隔离Editor空闲、Runtime/Config DLL和MVID原样、Tests实际重建MVID473d9210-53cd-436e-bbef-f9bd60b83884。新compile-binding SHA12339c53e62395bbc26ca1f2d173fa6247c9fc012b35ebeb1ee847605b54e99a，after graphf098969a278af8559a38109df767a131745ad579d4eeb78b69be9c143fb757ac，证据位于 DefaultFrameworkTimeoutConsolidationApplication01。第一次状态通信400和首次postread120秒超时均保留，未第二次触发编译。

只复测上述7失败叶：fresh200实际session67421正在运行，六个selector（原Safety class两失败合为一次，其余五个独立方法），首selector已真实启动，09:42:37新鲜加载观察符合新编译、原Editor和隔离要求。尚无新复测终态，不把入口修复或编译成功记为测试通过；后续继续观察同一session，不重派。精准复测及完整恢复通过后才可计入15同源Passed+7新Passed，之后正式构建同版本Windows包与首版验收。

Goal仍active，五类验收尚未整体关闭；用户后移的高塔/级联压力整组及两项外部验收继续排除。隔离桌面、禁系统鼠标、不聚焦、不切用户输入桌面、串行测试/构建/Player、不提交或发布保持。以下历史快照保留原归属。

---

# 2026-10-16 最新核验：五类待关闭；关联回归15通过、4超时、3尚未启动

快照时间 2026-10-16T08:58:44Z。fresh198实际session84821已终止，exit1；前三项Passed、第四项默认180000ms框架总超时Failed。随后完成回调清理超过原180秒观察界限，最后三项没有提交。本次原文件与偏好完整恢复，恢复后独立Read确认Editor空闲、原编译身份一致、用户输入桌面仍为Default。证据为 Fresh198TerminalActual01/terminal.json 与 fresh-idle-verification.json。不得继续把84821记为运行中。

与原196及其同次晚到六项合计，本轮22项关联回归为15 Passed、4默认框架总超时Failed、3尚未启动，共7项仍需取得通过结果。五类首版Goal仍为：功能与完整比赛、四场性能、弱网移动纠正、六组独立Player联机恢复、最终Windows包/中文HUD/报告。子项与类别存在包含关系，不把7条回归或网络纠正重复累加成总类别数。用户后移的32米高塔/两万格级联压力整组及真实多机/人工观感两项外部验收继续排除。

下一步仅准备原最后三项的同源续跑，完成真实结果与恢复后再按实际超时清单一次性调整测试入口Timeout并复测；不改玩法、断言或首版性能门槛。此前三超时/19复用+3复测候选链已不适用，原失败及观察/清理错误保留。没有运行新测试、构建或Player；Goal保持active，串行隔离与禁系统鼠标捕获约束保持。以下历史快照保留原归属。

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

# 2026-10-16 fresh196累计4项通过、2项框架总超时失败；原运行继续

快照时间 2026-10-16T07:26:15.753571+00:00。当前22叶关联回归已有4 Passed、2 Failed、另16叶未完成。新增通过为优化建造与原始完整路径的实际发布结果一致性对照（XML 20261016-072438.xml）。两失败均是新大招安全用例的默认180000ms框架总超时，原XML/失败保留；仅两处Timeout属性的修复候选已准备并核验，尚未应用，不宣称功能通过。

Root继续同一session25936的原串行回归，完成并恢复现场后再应用必要修复。Windows36构建/四场性能/自然比赛/六组联机入口及纯文件来源验证已准备并独立静审，新包尚未构建或验收。首版仍有五类未整体关闭；高塔/级联整组及两项外部验收不计本次Goal。后台隔离、禁系统鼠标、不聚焦和不切输入桌面保持。以下历史快照保留各自时点及归属。

---

# 2026-10-16 最新修复已编译；fresh196前三项实际通过，后续仍在运行

快照时间 2026-10-16T07:07:13.013860+00:00。同一 Goal 保持 active，首版仍有五类验收未整体关闭。此节覆盖下方旧时点的下一步说明，旧包与旧失败保持原归属。

六处源码修复已实际应用并通过 Unity 编译，完整699源码/699元数据图为 `57c9149c29e7860c3512da6d89c5081849be720b0a93891b30ad80ff8a1b63bf`。本轮关联回归22叶/16筛选，前三项真实XML均Passed：原墙面首次附着回退/重放、部分失支撑列建造cap后的支撑证明、已开始落定前段的epsilon封顶续跑。另19叶尚未取得完整终态，现场恢复尚未完成；不宣称全22通过。Root继续观察同一session25936，不重复派发。

Windows36正式资源/Player构建入口、六组网络模板及纯文件包验证已准备；四场性能与自然比赛的入口正在派生。新包尚未构建，旧35/33/32/31包的结果不转记为36通过。下一步等待本轮真实终态和完整恢复，再正式构建并用同一个包验证普通下落排空、六组联机、弱网纠正、四场性能、自然三分钟比赛与中文HUD。

五类仍为：功能稳定性与完整比赛；四场性能；弱网纠正；六组联机与恢复；最终Windows包/HUD/报告。32米高塔和两万格级联压力整组用户后移，真实多机与人工观感两项外部验收不计Goal；普通重力、32米玩法顶界、守恒/扣费/碰撞仍为必需。

所有运行继续保持隔离桌面、禁系统鼠标捕获、不聚焦、不切用户输入桌面，测试/构建/Player串行。D3D12已成功启动，条件性WMI/vmms恢复未触发，未操作服务，未提交或发布。前三项XML关联及当前源码/meta核验见 `Status196Partial01/partial-proof.json`；本次前缀更新保留旧全文字节。

---

# 2026-10-16 Windows33已构建；原无CPU双人首例仍未通过

**快照时间 2026-10-16T02:57:54.739885+00:00。同一Goal保持active，按用户修订范围仍有五类必需验收未整体关闭。**

当前699源码/699元数据图上17个变更关联用例实际通过；这是部分影响回归，未宣称完整程序集通过。fresh191原外层失败与随后独立restore-only成功分别保留，没有重跑或改写原测试结果。

Windows33正式Addressables及Windows64 Development候选构建已成功，包为Builds/Blocks-20261016-diagnostic33；完整359文件库存及程序集身份已检查。原inspector的最终JSON文本比较失败保留，CanonicalIdentityVerification01独立补充确认仅四处对象键序不同，其他字段/类型/数组严格等值。身份核查不等于Player验收通过。

同包原无CPU开关的2人/0ms/0% D3D12首例已实际运行并结束，networkPassed=false。主机终态revision1432、557/557命令完成、pending=0、9472存活格；仍有1段16格下落、1列unstable、failedTransactions=0，未满足原排空条件。后续frozen/reconnect/clear/rebuild没有进入，摘要中的缺失条件不是多项独立已执行失败。两Player自然退出，StandalonePreferences已恢复。没有把残留下落宣称为已修复或永久卡死。

剩余五类为：功能稳定性及最终同包完整比赛；steady250k180s/combat250k300s/blast8 120s/scattered100k180s四性能；100ms/1%弱网纠正；2/8人乘三档网络的六组联机恢复；最终Windows包/中文HUD/报告/调参说明。32米高塔与两万格级联整组用户后移；真实多机与人工观感不计Goal。32米玩法顶界与普通重力仍保留。

下一步复用Windows33包的既有CPU观察开关取得发布段状态与末快照，保持2人/0/0、35秒移动/45秒等待/420秒总限；该采样有Profiler扰动，只用于诊断，不能替代原无CPU正式验收。未因D3D12启动失败触发任何WMI/vmms恢复，因为本包已成功启动。

测试/构建/Player由Root串行；隔离桌面、禁系统鼠标捕获、不聚焦、不切桌面，保护已有工作，不提交或发布。以下历史全文原字节保留。

---

# 2026-10-16 fresh191 精确14项通过，随后独立恢复完成；Windows33待实际构建

**快照时间 2026-10-16T02:36:51.738286+00:00。Goal保持active，五类必需验收仍未整体关闭。**

fresh191五组选定用例实际全部Passed：7+2+2+2+1，共14个不同XML叶；逐名与prepared-plan一致，raw/canonical字节相等。最后两份XML为20261016-021704.xml与20261016-022231.xml。原测试runnerExitCode=0；先前fresh190三项公平性通过保留其自身归属。本图已有3+14=17个实际通过用例，这是变更关联部分回归，不代表整个程序集通过。

原fresh191外层returncode=4、impactPassed=false继续保留。首次恢复前的空闲Read失败，原restorationError为`RuntimeError('Restoration idle probe failed or returned invalid JSON; restoration refused')`。Root没有重跑测试，仅调用原wrapper的--restore-from；Fresh191RestorationRecovery01/terminal.json实际returncode=0、restored=true、pendingRestoration=false，10个保护文件和19项逻辑偏好raw/API均恢复，原outer与14项结果逐字节未变，原错误没有清除。恢复后preservation SHA为d6d12e5be2850b06f8b92448105d0e657c1f5886331213efdb2eb607b3bf74a4。

恢复后独立postread06在2026-10-16T02:34:04.7591186Z确认Editor35536处于CodexBlockTests20261013a，用户输入桌面Default；六项空闲标记与两个焦点标记全部false。Runtime/Tests/Config MVID保持本次编译身份，699源码/699元数据图未改。旧session2923已真实终止，不能继续视为运行中。

Windows33尚未bind、构建、inspect或运行Player。构建文件校验正在增加仅针对本次原runner0/outer4后独立restore-only0的补充证明；原失败与恢复证据分开保留，fresh190及原14项玩法判定不变。实际新包完成后先用原2人/0ms/0% D3D12场景复测普通下落，再推进其余验收。Windows32原drain失败不转记为修复。

剩余仍为功能稳定性/同包完整比赛、四场性能、弱网纠正、六组联机恢复、最终Windows包/HUD/报告。高塔与两万格级联整组用户后移，真实多机/人工观感不计Goal。保持串行调度、隔离桌面、禁鼠标捕获、不聚焦、不切桌面；未操作服务，未提交或发布。以下历史全文按原字节保留。

---

# 2026-10-16 fresh191 已实际通过9/14；后续Windows33入口静态准备完成

**快照时间 2026-10-16T02:01:32.489660+00:00。Goal保持active，五类必需验收仍未整体关闭。**

Root仍只观察原session2923，未重启或取消测试。fresh191前两组真实XML已核：BindingFutureMotion七项全部Passed（20261016-015011.xml）；Fresh117/118两项全部Passed（20261016-015858.xml），raw/canonical字节及精确叶名称一致。余下DefaultWindow两项、NaturalWindow两项、OwnLandingActivation一项尚无本轮最终结果。整轮尚无外层终态和完整恢复证据，不能称14项全过或已恢复。先前fresh190三项通过且完整恢复保持原归属。

`Windows33Preparation01`正式资源/Addressables/Windows构建候选与`Windows33AcceptancePreparation01`原首例/矩阵/四性能入口已完成Reports-only静态准备及独立审查；尚未bind、构建、inspect或启动任何新Player。构建门要求本图真实3+14=17个不同用例通过并全恢复，明确是partialImpactRegression而非全程序集。首例保留原2人/0ms/0%、D3D12、35秒移动、45秒阶段等待和420秒总限，不改变oracle。验收校验器路径合同已修正为prepare-windows33.candidate.py并要求实际config pin匹配。

未来如新包仍未落定，`GravityVisitDiagnosticPreparation01`提供只在显式双flag下启用的有界私有队列/索引诊断候选，尚未应用、编译或执行。当前699源码/699元数据及已编译图未改；不预判原Windows32排空已修复。

剩余仍为功能稳定性/最终比赛、四性能、弱网纠正、六网络恢复、最终Windows包/HUD/报告。高塔/两万格级联整组按用户后移，真实多机/人工观感不计Goal。保持隔离桌面、Default输入桌面、禁系统鼠标捕获/聚焦/切桌面；未操作服务，未提交或发布。以下历史全文保留原归属。

---

# 2026-10-16 fresh190 三项通过并全恢复；fresh191 十四项影响回归正在后台运行

**快照时间 2026-10-16T01:27:34.398788+00:00。Goal 保持 active，五类必需验收仍未整体闭合。**

浅链额度修正的三项 `BlockGravityMotionBudgetFairnessTests` 已实际全部 Passed，XML `20261016-012028.xml`；Root 的 fresh190 外层和内层均 exit0，10个保护文件和19项偏好恢复，raw/API相等，源码/meta不变。保全记录 SHA256 `581ae5876e85bf9c588731804439a30de2aacc76c4cd9c30608169b9de33be80`。这不等于 Player 原排空已修复。

Root 当前独占 **unified exec session2923**：`ShallowOffsetBudgetPreparation01/dispatch-impact191.candidate.py --fresh-read-label postread04`，fresh-fixtures-191，5筛选14项，首筛选 `BlockGravityBindingFutureMotionTests` 已进入 RUNNING。其余为 Fresh117/118 原1024边界、DefaultWindow两项、NaturalWindow两项及真实命令负载的 OwnLandingActivation一项。只轮询此handle与本次runner文件；在真实终态与完整恢复前，不运行其它Unity/CLI/native/测试/构建/Player，不修改Assets。保持隔离Editor35536、Default输入桌面、不聚焦、不捕系统鼠标。

同图纯托管 cohort144 诊断已经实际运行：4096和1024预算均以77 checks得到Ready/private request，真实Replacement.Step/Clone与Work.Copy检查通过，完整wire未变；未初始化native资源、未进入场景或物理。输入是Windows32已发布快照的新建私有诊断状态，不能冒充原队列续跑或网络/性能/碰撞通过。结果在 `Cohort144ManagedRegroupPreparation01/managed-regroup.actual.json`。原私有索引/访问原因仍未直接观察。

当前编译图仍为 `d15bce02c4f8e269c9b8c171fe3f87be9d558b65f2bb14762636f8275c8dc4c9`；Runtime `d63c56d3-0b1b-417c-bf6e-0ea243f790eb`、Tests `0ad2504b-ed30-48e7-ba6e-79cab3d324e6`，Config未变。未创建新Windows包。fresh191后如实审查结果，实际通过才准备新包及原firstcase排空复测。Windows32原失败不覆盖、不转记。

剩余仍为功能稳定性/完整比赛、四性能、弱网纠正、六网络恢复、最终Windows包/HUD/报告。高塔/两万格级联整组用户后移及外部真实多机/人工观感不计本Goal。D3D12成功启动，条件性vmms/WMI恢复未触发，未操作服务，未提交或发布。以下历史快照保持完整。

---

# 2026-10-16 Windows32 CPU诊断排空未通过；浅链额度修复已应用并实际编译，回归待完成

**本快照截至 2026-10-16T01:09:34.210043Z 的实际编译绑定：Goal 保持 active，五类必需验收仍未整体闭合。** 本节覆盖下方“CPU诊断尚未运行”“浅链候选尚未应用”等旧时间状态；原始失败、旧包归属及下方旧全文逐字节保留。

**Windows32 同包 CPU 诊断已关闭，普通重力排空仍未通过。** 这是独立诊断运行 `Windows32GravityCpuDiagnostic01/Run01/p2-rtt0-loss0/cc43a1c5`，不是下方无CPU首组 `2e1f7ec1`，不得混用两组命令数和末态。诊断观察器 `closed=true`、`observationComplete=true`，8153 次采样，丢行、丢事件、丢绑定及观察错误均为 0；原 `networkPassed=false` 和完整drain超时保留。首个命令全部完成样本至最后drain样本间，527/527 命令保持不变，模拟时间 59.673346→94.334225 秒、revision 618→1844，持续 34.660879 秒；实际发布 1226 个地形事务，failed/aborted 均为 0。falling 为 135→峰值4585→末尾2407，unstable 为1638→982，普通重力未排空；末尾仍有6440稳定格及2407下落格。不能把该区间概括成地形从未发布、falling单调增长或所有工作永久停住。

该诊断区间的1576次权威stage共计6,368,941个重力checks，平均4041.206/4096（98.66%）。完整已发布快照的实际Offset遍历深度分布为1级559、2级666、3级167、4级1。它们支持检查保守收费的吞吐影响，但checks不是CPU占比，稀疏的stage耗时也不是帧时P95/P99；现有数据没有逐调用点归因，不能证明两处Offset收费是唯一根因。cohort144的已发布 `NeedsRegroup` 投影连续71条记录、约69.868秒相同；私有regroup游标未采集，不能据此断言私有工作完全没有进度。该对象先进入regroup路线，下面的两处Offset额度修改不直接修复它。[封闭观察分析](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows32GravityCpuDiagnostic01/Analysis01/closed-observer-analysis.json)和[checks尾段分析](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows32GravityCpuDiagnostic01/Analysis01/checks-tail-analysis.json)保留完整依据。

**两次真实地图物理回放均止于 stage 0，属于诊断入口受限，不能记为重力通过或玩法失败。** `gravity-physical-replay.actual.json` 的normal/check-only两种模式均 `diagnostic_error`、`stages=0`：当前未保存Untitled场景使 `EditorSceneManager.NewScene(Additive)` 被拒绝。`gravity-physical-replay02.actual.json` 的两种模式同样 `stages=0`：`SceneManager.CreateScene` 被明确限制为仅Play Mode可用。两次均未进入重力模拟；报告中的场景数量均1→1，`dirtyEqual/activeRestored/currentRestored/frozenInputsUnchanged=true`。这两份报告没有 `cleanupErrors` 字段，不将缺字段写成空错误数组。它们不能提供原drain的物理回放根因结论。

**极小 Preview Physics 能力探针已实际完成，证实本次查询隔离并完整清理。** `preview-physics-capability.actual.json` 中Preview PhysicsScene有效且不同于default；原default Raycast未命中，Preview局部Raycast命中唯一自有Box，正控制成立。结论只限本机这次Preview Collider对原default Physics查询不可见，不能把局部命中当作真实地图物理回放已成立。`cleanupErrors=[]`，没有 `sceneStateError`；场景数量1→1，dirty、active及地形Current全部恢复。上述三份原件均在[Windows32PhysicalReplayPreparation01](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows32PhysicalReplayPreparation01/preview-physics-capability.actual.json)，且均属于本次新源码应用之前的诊断证据。

**当前新的Runtime＋Tests候选已经 exact 应用。** `ShallowOffsetBudgetPreparation01/application.json` 记录2026-10-16T01:04:03.710339Z–01:04:04.019737Z只写 `BlockTerrain.GravityCohorts.cs` 与 `BlockGravityMotionBudgetFairnessTests.cs`，全部metadata保持，源码图为 `d15bce02c4f8e269c9b8c171fe3f87be9d558b65f2bb14762636f8275c8dc4c9`。Runtime仅将两处Offset调用前收费改为保守O(1)浅链上界，深链继续400；第三处dependent/private owner调用的400+4保护、4096检查总额和原墙钟预算保留。两个公平性测试仍使用真实4096耗尽与生产Stage，第三个真实Atomic发布测试正文未改。静态审查不是执行通过，也不能预告drain已修复。

Root仅实际派发一次 `recompile01`，隐藏CLI、`automaticRetry=false`；原 `status01` HTTP400 与 `postread01` 超时保留，没有二次编译。`status02` 实际返回 `completed/failed=false/compilationFailed=false/errors=[]`，独立 `postread02` 已成功，加载与磁盘身份均已核对。新的[compile-binding.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/ShallowOffsetBudgetPreparation01/compile-binding.json) SHA256为 `cf1f1c40debaf899768776367863017b26cffc812d9d5c1693cdd95e5f075a13`：Runtime `d63c56d3-0b1b-417c-bf6e-0ea243f790eb`、Tests `0ad2504b-ed30-48e7-ba6e-79cab3d324e6` 均更新，Config `f10e0192-24dd-4c09-b871-9bf5db4b0607` 及其DLL字节保持。**这关闭本次实际编译/加载绑定子门，不代表新图回归或Player通过。** 本快照尚无三个fairness用例的新终态；新图针对性回归、重建包和同包Player排空复测仍未通过。Windows32现有359文件包和fresh188基线＋fresh189影响回归继续保留原图/原包归属，不能转记成新Runtime图的通过。

仍待闭合的五类为：**功能稳定性与最终同源包完整比赛；四场1080p正式性能；弱网移动纠正；六组独立Player联机与恢复；最终Windows包验收、中文HUD与报告。** tower32m/cascade20k及对应专属碰撞、压力性能整组继续为用户后移、本次未验收（`user-deferred-not-accepted`），不记Passed、不计当前Goal；普通重力、守恒、费用、真实碰撞及32米玩法顶界仍必需。真实多机与人工观感两项外部验收不计Goal。

D3D12已成功启动，用户有条件授权的vmms/WMI恢复条件未触发，未操作服务。后续Unity/测试/构建/Player由Root串行调度，保持Editor35536隔离桌面、Default用户输入桌面、禁止系统鼠标捕获/激活窗口/切桌面。本次状态更新只读既有报告与源码、写Reports，不执行Unity、CLI、native、测试、构建或服务操作，不提交或发布。三份旧文档完整字节备份、原件SHA、前缀及逐字节后缀核验见[本次状态证据](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/StatusRefresh20261016/Windows32CpuAndShallowOffset01/snapshot-evidence.json)。以下为保留原来源和原结论的历史全文。

---

# 2026-10-16 fresh189三项通过并全恢复；Windows32已构建；首组重力排空未通过

**本次终态快照：Goal保持active，五类必需验收仍未整体闭合。** 以下新事实覆盖旧快照中的“fresh189运行中”和“Windows32尚未构建”，保留所有旧失败及原包归属，不扩称最终图27/27或完整测试程序集回归。

**fresh189的三个helper影响用例实际全部Passed，runner exit0，已完整恢复。** XML分别为20261015-235249、20261015-235744、20261016-000242；`restored=true`、`pendingRestoration=false`，10个保护文件和19项逻辑偏好恢复相等，API/raw相等，源码/meta未变。原fresh188仍为26P/1F，旧失败原件保留；现行证据明确复用未变Runtime/Config的26项基线与新图3项影响重跑，`testsExecutedOnFinalGraph=false`，不把旧失败改写成Passed。

**Windows32正式资源及Addressables构建已成功，Windows x64 Mono Development包已通过实际inspector。** 包位于`Builds/Blocks-20261016-diagnostic32`，完整库存359文件，身份SHA256为`48203179f10235afb6b5ff5527c5ef24f7b300f21404dda94c55de993a973b51`。新包身份不等于玩法、性能或联机验收；Windows31的旧包自然比赛通过不转记到32。

**新包首组2人/0ms/0%无CPU录制的D3D12入口已经实际启动，正常主相机真实1920×1080，但45秒完整drain失败。** Root已亲见session14171终态exit1；原harness/bootstrap/native均exit4，两名Player与proxy均自然exit0，原`networkPassed=false`保留。host服务器时间68.419813秒起534条命令全部完成、队首为空；后续54个drain样本总量恒9174，falling先降到185再增到末尾4325，unstable末尾1691，quiet始终0，revision持续推进。两名参与者的权威ACK均已到达，直接阻塞是普通下落/失支撑地形未落定，内部重力原因仍待定位；不能据泛化失败字段声称ACK一直缺失。

首组尚未进入的frozen、重连、清空、重建阶段为前置drain失败导致的缺证，不记作各阶段已经执行后失败。56次safetyRecoveryEvents全在traverse、服务器时间19.009329–47.252172秒；68秒后及整个drain没有新增恢复，没有重复返回同一已观测精确位置的证据。StandalonePreferences的64/32位视图各19个原始值及分支存在性独立比较完全相等，原记录restored/rawEqual/branchExistenceEqual均true，本轮owned Players均exit0。Root随后fresh Toolhelp确认无InkLan残留；本次文档审计未自行执行进程扫描。

**仍待闭合的五类：功能稳定性与Windows32完整比赛；四场1080p正式性能；弱网移动纠正；六组独立Player联机与恢复；最终Windows包验收、中文HUD与报告。** tower32m/cascade20k及对应专属碰撞、压力性能整组继续为用户后移、本次未验收（user-deferred-not-accepted），不记Passed、不计本次Goal；普通重力、守恒、费用、真实碰撞及32米玩法顶界仍必需。真实多机与人工观感两项外部验收不计Goal。

D3D12启动成功，条件性vmms/WMI管理员恢复未触发、未操作服务。测试/构建/Player继续串行，沿用隔离桌面、禁系统鼠标捕获、不激活窗口、不切用户输入桌面约束。本次只读既有证据、写Reports诊断与状态快照，不运行Unity/native/测试/构建，不提交或发布。逐事件及恢复审计见[Windows32DrainDiagnosis01](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows32DrainDiagnosis01/safety-recovery-preferences.audit.md)，备份和来源SHA见同目录`status-snapshot-evidence.json`。以下旧时间快照逐字节保留。

---

# 2026-10-16 fresh188 终态 26P／1F；helper02 已编译；fresh189 三例运行中

**截至 2026-10-16T07:53:49+08:00，五类必需验收仍未整体闭合，Goal 保持 active，新 Windows32 包尚未构建。** 以下更新覆盖旧快照中的“fresh188 运行中”，原失败、旧包归属和全部历史结论保留。

原五筛选共27项 **fresh188 已终止：26项通过、1项失败，runner exit 3**。失败为 `APopulatedBuildColumnIsInvalidatedBySameWorldMutationBeforeAnyPublication`，原断言为 Expected in range (1,1)，实际2；不改写为通过。10个受保护文件、19项偏好均已恢复，`restored=true`、`pendingRestoration=false`，偏好 API／原始值一致，源码和元数据未变。

**HelperNarrowMask02 已应用，仅调整测试断言辅助逻辑，实际仅派发一次编译且 clean。** Runtime `c44e6992-5d65-4856-b0b2-02b999472fac`／Config `f10e0192-24dd-4c09-b871-9bf5db4b0607` 保持不变，Tests 更新为 `79acc9b2-4db0-4a25-a230-65d5c6058b08`；实际成功 `postread01` 已绑定，原 `postread02` 超时保留。Root 串行会话 **82152** 已启动 **fresh189 三例**，本快照尚无终态，不预记通过，也不替代原27项或包级验收。

仍待闭合：**功能稳定性与新包完整比赛；四场1080p正式性能；弱网移动纠正；六组独立Player联机与恢复；最终Windows包、中文HUD与报告。** `tower32m`／`cascade20k` 整组继续为 `user-deferred-not-accepted`，不记Passed、不计当前Goal；普通重力、守恒、费用、真实碰撞及32米玩法顶界仍必需。真实多机与人工观感两项外部验收不计Goal。

本次仅更新现有状态文档及本目录证据，不运行Unity／native／测试／构建，不聚焦、捕获鼠标或操作服务。对应记录见 [Status188AddedRange01/helper02-snapshot-evidence.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Status188AddedRange01/helper02-snapshot-evidence.json)。以下旧时间快照逐字节保留。

---

# 2026-10-16 Added-range 单文件已编译；原27项关联回归 fresh188 运行中

**截至 2026-10-16T06:44:28+08:00，五类必需验收仍未整体闭合，Goal 保持 active。** 本次只修改 `BlockTerrain.BuildOccupancy.cs`：先收齐本列全部新增格，再按实际新增高度范围准备数组；完整移动结构、支撑链、别名、费用和最终发布校验保留。699 源码成员和全部699元数据未变，原源码/meta已备份。新图 `b9dfab93021526990f8c42fc2f5d23014e75872126f441165f3ab21407d0de42`，实际编译无错误，加载和磁盘 Runtime／Tests／Config 身份已核对。

两组 Reports-only 实际 C# 状态验证通过：407个槽、3列、Phase1切片续跑、完整400格跨度、带洞范围和11个缓存边界，共2139次断言；PreviewScene关闭、原场景dirty和地形Current恢复，未初始化原生地形、未进入Play Mode。这是私有状态验证，不代替真实碰撞、付款或包级验收。Root已串行启动同新图的原五筛选 **12+12+1+1+1，共27项 fresh188**，本快照尚未取得最终结果和恢复终态；不搬用旧fresh186通过。

**CPU提取已恢复完成，覆盖此前失败时间快照。** CpuExtract02保留9993帧完整元数据并精确关联，48个选定帧导出387119条sample，未截断、树校验错误0，输入raw前后SHA相同，配置及空历史恢复完成。严格计时存在3625项interval异常，因此不使用self或相加嵌套耗时。8个所选长帧分别在BlockNetworkProbe.Update内观测506–740ms inclusive；只能定位整个调用，尚不能分摊Hash/Capture/Write或Profiler成本，也不构成正式帧时验收。普通原45秒流程仍有0.5秒Capture/Write；下次保留原35秒移动／45秒drain并关闭CPU录制复测。

Windows31原同包自然比赛12/12通过及原网络drain失败保留原包归属；新Windows32仅准备了入口，尚未构建、运行或验收，实际吞吐收益未测得。D3D12已成功启动，用户有条件授权的vmms/WMI恢复条件未触发，未操作服务。

当前仍须完成：**功能稳定性与新包完整比赛；四场1080p正式性能；弱网移动纠正；六组独立Player联机与恢复；最终Windows包、中文HUD与报告。** 32米高塔／两万格级联及其专属压力/碰撞验收整组继续是用户后移、本次未验收，不记Passed；普通重力、守恒、费用、真实碰撞和32米玩法顶界继续必需。真实多机和人工观感两项外部验收不计Goal。

测试、构建与Player串行；保持隔离Editor35536／CodexBlockTests20261013a、Default输入桌面、禁止系统鼠标捕获、前台激活和切桌面。不提交或发布。证据pins与旧文档逐字节备份见 [Status188AddedRange01/snapshot-evidence.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Status188AddedRange01/snapshot-evidence.json)。以下旧时间快照保留原结论。

---

# 2026-10-16 Windows31 同包自然比赛 12/12 通过；普通排空仍待修复

**截至 2026-10-16T06:14:54+08:00，五类必需验收仍未整体闭合，Goal 保持 active。** Windows31 已实际完成正式 Addressables → Windows 构建并核验 359 文件库存；这替代下方“尚未构建”的旧时间快照。当前 Repair05 关联回归仍为 fresh186 的 5 个筛选、27 条唯一用例全部 Passed，不扩称整个程序集通过。

本次同包 `PackageSmoke01` 自然流程 **12/12 检查全部通过**：双独立正常图形 D3D12 Player，实际首轮观测约 **180.1197 秒**，积木最终总量粉3372／蓝706，回房后再开下一局，双方自然退出码0，无识别错误。包前后完整库存一致，隔离桌面／Default 输入桌面保持，窗口审计未观察到测试 Player 位于输入桌面；受保护偏好 restored/rawEqual/branchExistenceEqual=true。原 `packageSmokePassed=false` 和 HUD／精确清场不由本 harness 验证的字段原样保留：这里只完成完整比赛子门，中文 HUD 和精确回合清空仍分别消费同包正式性能图像、网络证据。

**普通双人网络排空尚未通过。** Windows31 CPU 双人诊断保持原35秒移动／45秒排空：565条命令首次全部完成后只余约0.25秒，仍有普通下落与列扫描积压；56次中止均为 BuildMovingOverlap，未见永久停在一个 revision。计数显示89.23%的 drain 权威stage、81.35%的共用预算checks用于建造预备／重试；这些不是CPU百分比。原同次raw提取CLI先超时、Editor随后完成失败终态：只保留尾2000帧，无法对应9993份完整sidecar，因此没有线程样本或CPU热点结论；finally历史恢复及独立idle已核实。原raw、失败与超时原件保留，不通过延时、丢格或改写join判通过。

当前五类仍为：**功能稳定性与完整比赛（关联回归、同包自然比赛子门已过，普通排空待修复）；四场1080p正式性能；弱网移动纠正；六组独立Player联机与恢复；最终Windows包、中文HUD与报告。** 高塔／两万格级联及其专属压力验收整组继续为用户后移、本次未验收，两项外部验收不计Goal。D3D12已正常启动，条件性vmms/WMI管理员恢复未触发、未执行。

本次只前置Reports状态快照，历史字节完整保留。输入原件SHA、备份与后缀核验见 [Status187Windows31Natural01/snapshot-evidence.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Status187Windows31Natural01/snapshot-evidence.json)。后续继续离线读取诊断和窄修复，测试／构建／Player／提取串行，禁止系统鼠标捕获、前台激活和切用户输入桌面；不提交或发布。

---

# 2026-10-16 fresh186 完整关联回归 27/27 Passed，现场已恢复

**截至 2026-10-16T05:48:17+08:00，本次关联回归门槛已通过；5 类必需验收仍未整体闭合，Goal 保持 active。** fresh186（Root session86976）已正常终态，Root poll exit0，preservation 的 runnerExitCode=0。实际 5 份 XML 共 **27 个唯一用例全部 Passed**，分组为 **12+12+1+1+1**；这是原五筛选的完整关联范围，`fullAssemblyRegression=false`，不扩称整个程序集通过。

现场已完整恢复：`restored=true`、`pendingRestoration=false`、10 文件全部相等、19 项偏好 `restored/apiEqual/rawEqual=true`，source/meta 未变。结果归属实际 Repair05 图 `58640f0ebd134042587b45e400af802fbbba8db868d795b53f4880b126b0529c`、Tests MVID `c9081eaa-9aff-4e8b-9261-a2f6b6db18b6`；原184失败保留历史结论。本次不声称生产性能收益，也不把关联回归通过转记为包级全部验收通过。

本快照的 Windows31 尚未构建；Root 下一步文件绑定后串行正式构建和同包验收。五类仍为：**功能稳定性与完整比赛；四场 1080p 性能；弱网移动纠正；六组独立 Player 联机与恢复；最终 Windows 包、中文 HUD 与报告。** 功能类中本次关联回归已完成，最终同源包的功能/完整比赛仍待闭合。高塔/两万格级联及其专属碰撞、压力验收整组继续为 **用户后移、本次未验收（user-deferred-not-accepted）**，不记 Passed；真实多机与人工操作观感两项外部验收不计 Goal。

原始 XML/preservation、编译身份来源、旧文档备份和逐字节历史后缀 SHA 见 [Status186Terminal01/snapshot-evidence.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Status186Terminal01/snapshot-evidence.json)。本次只修改两份 Reports 状态文档，未操作 Unity/CLI/tests/Player/native，也未修改任何构建准备 pins。以下旧快照保持原时间、原来源与原结论。

---

# 2026-10-16 fresh185 单项通过；fresh186 完整回归运行中

**截至 2026-10-16T04:55:01+08:00，仍有 5 类必需验收未整体闭合，Goal 保持 active。** fresh185 单 Paid 的实际 XML 为 **1/1 Passed**，叶时长 **328.489468 秒**；wrapper/runner 均 exit0，10 文件和 19 项偏好全部精确恢复，source/meta 未变。此结果只属于 Repair05 当前图的单项冒烟，不能代替完整五筛选/27 项，也不关闭任何一类验收或证明生产性能收益。

Root 已启动原完整 5 个筛选、27 项的 fresh186（session86976），本快照尚未取得终态，不记通过。Repair05 实际编译身份及源码图保持；Windows31 尚未构建。功能稳定性与完整比赛、四场 1080p 性能、弱网移动纠正、六组独立 Player 联机与恢复、最终 Windows 包/中文 HUD/报告这五类范围不变。高塔/级联整组仍为用户后移、本次未验收，两项外部验收不计 Goal。

本次只前置报告快照；旧字节后缀、备份及实际 XML/preservation SHA 见 [Status186Preparation01/snapshot-evidence.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Status186Preparation01/snapshot-evidence.json)。以下为保留原来源和原结论的历史记录。

---

# 2026-10-16 fresh184 失败已恢复；Repair05 已编译，fresh185 运行中

**截至 2026-10-16T04:45:17+08:00，仍有 5 类必需验收未整体闭合，Goal 保持 active。** 以下为最新快照，旧章节保留原时间、原源码图及原结果。

- **fresh184 实际失败：**单 Paid XML 为 **0/1 Passed、1 Failed**，叶执行时长 **324.572614 秒**，`RealPaid:138 → AssertRebound:526` 的 FallingCohort 身份断言失败。wrapper exit1、runner exit3 保留；随后已完整恢复：`restored=true`、`pendingRestoration=false`、10 文件全部相等、19 项偏好 `restored/apiEqual/rawEqual=true`，source/meta 未变。恢复成功不改变失败结论。
- **诊断与修复范围：**失败定位为测试对 publication 阶段契约的断言时机：full-owned Ready 时连续性引用仍指向 Draft，实际 Publish 验证才重绑 Next。Repair05 只修正测试对两个阶段的严格校验，并新增真实 World 发布见证；原完整 `AssertRebound`、12 个用例及其 600000ms 时限、预算、付款、碰撞与完整快照断言保留。不据此声称生产根因修复或性能收益。
- **Repair05 实际编译：**一次编译 `completed/failed=false/errors=[]`。源码图 `58640f0ebd134042587b45e400af802fbbba8db868d795b53f4880b126b0529c`；Tests MVID `c9081eaa-9aff-4e8b-9261-a2f6b6db18b6`，Runtime/Config 字节及 MVID 不变。post-read 同 PID35536、隔离桌面、Default 输入桌面、六项 idle、双 focus=false。实际绑定 SHA `4c7b837839cd00913122672553e9df1e5aaa61c6bc824db2eafd0aa14c6c5f69`。
- **后续尚未通过：**Root 已用原 smoke wrapper 启动单 Paid `fresh185`（session49236），本快照尚未取得终态、不记通过；原五筛选/27 项完整 `fresh186` 尚未运行。Windows31 binder Revision05 仅已准备，Windows31 尚未构建；单项冒烟不能替代同图完整回归、恢复或包级验收。

五类仍为：**功能稳定性与完整比赛；四场 1080p 性能；弱网移动纠正；六组独立 Player 联机与恢复；最终 Windows 包、中文 HUD 与报告。** 用户后移的高塔/两万格级联整组继续标记 `user-deferred-not-accepted`，两项外部验收继续不计 Goal。后台隔离、禁系统鼠标捕获、不激活窗口、不切用户输入桌面及测试/构建/Player 串行约束保持。

本次仅前置报告快照，不执行测试、Unity、native、Player 或任何应用/绑定 helper action。XML、preservation、实际编译来源与旧文档备份/原字节后缀 SHA 见 [Status185Preparation01/snapshot-evidence.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Status185Preparation01/snapshot-evidence.json)。

---

# 2026-10-16 fresh183 已恢复但未通过；Repair04 已编译，smoke184 运行中

**当前仍有 5 类必需验收未整体闭合，Goal 保持 active。** 本节是 2026-10-16T04:11:31+08:00 的证据快照；不将旧图通过回调转记为新源码通过，不将编译或单用例冒烟记为完整回归、性能或最终交付通过。

| 项目 | 本次已确认的事实 | 尚未完成的部分 |
|---|---|---|
| fresh-fixtures-183 关联回归 | 原计划 5 个筛选、27 项。按唯一用例首次有效回调，12 项基础类 XML Passed，加 9 项地形类 Passed 回调，共 21 条通过；PaidCohortAndLegacy 为 1 条实际 Failed:Error（碰撞单元 expected `(2,0,2)` / actual `(3,0,2)`）；PreparedLegacyActivation 为 1 条 Cancelled。 | RemoteReplacement 和另 3 项关联筛选未执行；没有地形整套或全部 27 项的完整终态，不记整组通过。迟到取消又重复报告了此前已有结果的 10 项，原通过与实际失败回调全部保留，不用重复取消覆盖原事实。 |
| fresh183 现场恢复 | `runnerExitCode=1` 保留；随后恢复已真实完成：`restored=true`、`pendingRestoration=false`、10 文件全部相等，19 项偏好 `restored/apiEqual/rawEqual=true`，源/meta 未变。 | 恢复成功只代表现场保全，不改判回归失败或未执行项。 |
| BuildMetadataOwnershipTestRepair04 | 测试碰撞探针修正后，实际单次编译 `completed`、`failed=false`、`errors=[]`；699 源/699 meta 图为 `7659b56c925b6af080ad3c7e4fc783b6ba56de70dd85e5de36b54ebf8caec3a9`。Tests MVID 为 `6ac1be9a-9b56-40f8-a675-6fb4057beea5`；Runtime/Config 字节与 MVID 不变，loaded 与 disk 一致；post-read 同 PID35536、六项 idle、双 focus=false、隔离桌面及 Default 输入桌面。 | 仅证明测试源码已编译，不证明运行时代码性能改善。新图完整 27 项关联回归尚未通过。 |
| fresh-fixtures-184 单用例冒烟 | session13406；快照时只有 RunStarted UTC `2026-10-15T20:08:34.1310286Z` 和 PaidCohortAndLegacy TestStarted UTC `2026-10-15T20:08:37.4705347Z`。 | 运行中、无 TestFinished/RunFinished/runner 终态，不记通过；即使随后单项通过，也不能替代同图完整 5 个筛选/27 项及全部恢复。 |
| Windows31 准备 | Reports 中 binder Revision04 与正式性能/六组联机候选入口已静态准备；默认读取 Repair04 的实际编译绑定。 | 本快照尚无 Windows31 实际身份包；只有后续同图完整回归和恢复通过后，才可串行正式资源构建与同包验收。 |

五类未闭合范围仍为：

| 类别 | 必需完成的范围 |
|---|---|
| 功能稳定性与完整比赛 | 当前图关联回归、普通建造/拆毁/重力、守恒、扣费、真实碰撞与恢复，以及最终同源包的完整三分钟比赛和回合闭环。 |
| 四场 1080p 性能 | steady250k 180秒、combat250k 300秒、blast8 120秒、scattered100k 180秒，保留八独立 Player、原规模及正式帧时/普通发布/爆破阈值；短诊断不代替正式采样。 |
| 弱网移动纠正 | 100ms/1% 下真实动态地形移动与己方积木支撑样本，P95≤0.15米、P99≤0.60米。 |
| 六组独立 Player 联机与恢复 | 2/8 人 × 0ms0%、100ms1%、200ms3%，覆盖一致性、迟加入、重连、普通下落、回合清空和重建。 |
| 最终 Windows 包、中文 HUD 与报告 | 新源码的正式资源/最终同源包、来源身份和资源检查、中文 HUD 实际画面、必需验收报告、风险与调参说明。 |

范围以 [GoalScopeDeferTowerCascade20261015.md](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md) 为准：**32米高塔拆底/两万格级联及其专属最终碰撞与压力性能整组为用户后移、本次未验收（user-deferred-not-accepted），不记 Passed。** 普通重力、支撑、守恒、扣费、真实碰撞和32米建造顶界继续必需；真实多机与人工操作观感两项外部验收不计 Goal。

沿用隔离 Editor35536 / CodexBlockTests20261013a、禁系统鼠标捕获、不激活窗口、不切用户输入桌面以及测试/构建/Player 串行约束。D3D12 已成功启动，用户“仅在仍无法启动时允许一次 WMI/vmms 管理员恢复”的条件未触发，未执行服务操作。本次文档脚本只修改 Reports；共享日志瞬时读取后立即关闭，迟到取消原始记录、证据 SHA256、旧文档备份及原字节后缀验证见 [Status184Preparation01/snapshot-evidence.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Status184Preparation01/snapshot-evidence.json)。以下全部旧快照保留原文、原时间和原归属。

---

# 2026-10-16 测试框架修复已编译；fresh183 关联回归进行中

**当前仍有 5 类必需验收未整体闭合，Goal 保持 active。** 本节快照时间为 2026-10-16T03:14:00+08:00。已实际完成的是测试框架修复及其一次干净编译；新源码整组 5 个筛选、27 个用例的 `fresh-fixtures-183` 已启动，尚无完整终态，不记通过，不据此声称 Player 性能改善。

前两次回归的真实结果分别如下，原始失败与恢复记录均保留，不能跨源码图合并成一组通过：

| 回归 | 已确认的结果 | 本次处理与证据边界 |
|---|---|---|
| fresh-fixtures-181 | 5 组共 27 项，14 通过、13 失败。基础类 11/12，400 列用例超过 180000ms；地形集成 12 项均因 SetUp/TearDown 嵌套迭代器中的 Enter/ExitPlayMode 被 UTF 拒绝；另 3 项关联用例通过。 | 完整失败终态保留，恢复完成；不转记后续源码。 |
| fresh-fixtures-182 | 基础类实际 XML 为 12/12 Passed，XML 执行时长 15.904 秒；地形类已观察到至少 8 条 SetUp 第34行 NullReferenceException 的失败回调，尚非完整套件终态。 | 外层观察超时后，由 UTF 正常取消运行，再清理残留收集器并退出 Play Mode；10 个文件和 19 项偏好全部精确恢复。原 outer/runner 失败、部分回调与恢复前状态保留；清理后的 cancelled 不冒充原套件完整终态。其余 3 项关联筛选未在此轮执行。 |
| fresh-fixtures-183 | 当前实际一次运行，包含原定全部 5 个筛选、27 个用例；启动 UTC 为 2026-10-15T19:11:02.382561+00:00。 | 运行中、未验收。须同一源码图取得全部真实终态并完成原样恢复，之后才可推进新 Windows31 包。 |

本次 `BuildMetadataOwnershipTestRepair03` 仅修复 2 份测试源码的跨域迭代器生命周期与快照保留方式：模式切换移到根迭代器，进入 Play Mode 后创建夹具，跨域前后完整快照通过可序列化字段保留。没有缩减用例、数据规模、预算、扣费或精确快照断言。实际单次 Unity 编译已 `completed`、`failed=false`、`errors=[]`；699 份源码/699 份 meta 图为 `6b2422f66046831fafae0926ed2b1258adc4b34cff06de39b123e8fa255b37cf`。Tests MVID 为 `853790ea-2e3d-438c-8b4b-7e6195d3728c`；Runtime `ed591aad-fd84-43c4-8eb6-53396c5c2864` 与 Config `f10e0192-24dd-4c09-b871-9bf5db4b0607` 的字节/MVID 均未因这次测试修复变化，loaded 与 disk 一致。原 ownership 运行时代码的收益尚未测得，Windows30 旧包仍保持原源码/包归属。

| 未闭合类别 | 下一步必需验收 |
|---|---|
| 功能稳定性与完整比赛 | 当前图关联回归，普通建造/拆毁/重力的守恒、扣费、真实碰撞与恢复，以及最终同源包完整比赛闭环；历史 Windows29 自然比赛通过仍只归原包。 |
| 四场 1080p 性能 | steady250k 180秒、combat250k 300秒、blast8 120秒、scattered100k 180秒；现有短诊断不代替正式采样，帧时和普通发布延迟仍未正式达标。 |
| 弱网移动纠正 | 100ms/1% 下真实动态地形移动/己方积木支撑样本，P95≤0.15米、P99≤0.60米。 |
| 六组独立 Player 联机与恢复 | 2/8 人 × 0ms0%、100ms1%、200ms3%，覆盖一致性、迟加入、重连、回合清空和重建；Ready31 同旧包双人诊断仍在排空阶段失败。 |
| 最终 Windows 包、中文 HUD 与报告 | 新源码通过必要回归后正式资源构建、最终包身份与来源核验、中文 HUD 实际画面、必需验收报告、风险与调参说明；此快照尚无 Windows31 实际构建或收益结论。 |

范围继续以 [GoalScopeDeferTowerCascade20261015.md](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md) 为准：**32米高塔拆底/两万格级联及其专属碰撞与压力性能整组，均为用户后移、本次未验收（user-deferred-not-accepted），不记 Passed。** 普通重力、支撑、守恒、扣费、真实碰撞和32米玩法顶界继续必需；真实多机和人工操作观感两项外部验收不计 Goal。

测试继续复用隔离 Editor35536 / CodexBlockTests20261013a，输入桌面保持 Default；沿用禁系统鼠标捕获、不激活窗口、不切用户桌面和测试/构建/Player 串行约束。D3D12 已实际启动，用户“仅在仍无法启动时允许一次 WMI/vmms 管理员恢复”的条件未触发，未执行系统服务操作。本次文档更新仅前置快照；历史原字节完整保留为后缀，备份、SHA256 与实际证据索引见 [Status183Preparation01](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Status183Preparation01/snapshot-evidence.json)。

---

# 2026-10-16 Ready31真实1080p采样恢复；重力排空仍未通过

**最新状态：当前五类验收范围保持不变，尚未全部通过。** Ready31 双人同包测试已解决本次 1080p 采样问题：房主和客户端均以 1920×1080 完成约30秒正常相机采样。原45秒排空仍未通过；双方停火输入 ACK 在68条排空记录中全部满足，后半段射击命令已全部完成，仍有重力／落定工作，最后一条排空记录剩305列不稳定、3924格下落积木，尚未形成静稳状态。汇总“缺ACK”是后续落块验收证据尚未建立的派生提示，不能认定输入丢失。

这次仍使用 **Windows30 包身份**，`Ready31` 仅代表窗口脚本版本，没有产生新的 Windows31 可玩包。包前后身份一致，房主、客户端和代理退出码均为0，受保护的玩家名及换装偏好已精确恢复。该双人30秒采样属于诊断，正式性能和其余验收仍按既定范围继续。

ownership 源码候选已实际应用并通过一次显式编译：699份源码/699份meta图为611af217…，Runtime ed591aad-fd84-43c4-8eb6-53396c5c2864、Tests 2490e0d1-619f-4c70-b634-872a21f575d4，Config保持原身份，loaded与disk一致。首次状态查询HTTP400原件保留；后续同一次编译completed、errors=[]，独立post-read六项闲置、双focus=false、隔离桌面和Default输入桌面已核。证据见BuildMetadataOwnershipApplication01/compile-binding.json。27项相关回归fresh-fixtures-181已开始，尚无终态，不记通过；该源码不在Windows30旧包中，不声称改善已有Player结果。

独立证据见Ready31IndependentReview01/review.md与evidence.json。D3D12已实际启动，用户条件性WMI/vmms恢复未触发、未执行服务操作。高塔/两万格级联整组仍为用户后移、本次未验收；两项外部验收继续不计Goal。以下历史快照保持原字节与原归属。

---

# 2026-10-16 Windows30诊断已完成，五类验收仍未闭合

2026-10-16；以 CPU30、Ready30 与 Extraction30 的已确认终态为界。本节记录已实际终态的诊断，不增加正式通过项。Goal 仍未闭合，剩余为下列 **5 类**，不是 5 个独立小测试。

| 未闭合类别 | 当前证据与尚需完成的范围 |
|---|---|
| 功能稳定性与完整比赛 | 当前变更关联回归、普通建造/拆毁/重力、守恒、扣费、真实碰撞及最终包比赛闭环仍须结合最终源码/包复核。Windows29 自然三分钟结算、返回房间及下一局已观察通过，保留原包归属；Windows30 未运行新的自然比赛或完整功能回归。 |
| 四场 1080p 性能 | steady250k 180秒、combat250k 300秒、blast8 120秒、scattered100k 180秒仍须正式达标。CPU30 仅完成首场20秒诊断，P99与普通攻击发布延迟仍失败，不替代正式场次。 |
| 弱网移动纠正 | 100ms/1% 场景仍需真实移动/己方积木支撑样本，P95≤0.15米、P99≤0.60米。Ready30 只有2人/0ms/0%，其纠正数据不应用弱网门槛、不计弱网通过。 |
| 六组独立 Player 联机与恢复 | 2/8人×0ms0%、100ms1%、200ms3%完整矩阵未通过。Ready30 仅选择首组且 drain 超时；有限下落、冻结、重连、清空、重建未到达，不能分别写成已执行失败或通过。 |
| 最终 Windows 包、中文 HUD 与报告 | Windows30 正式资源构建和诊断包身份核验已完成；最终同源可玩包的全部必需验收、中文HUD画面、来源/资源检查、风险与调参说明仍未整体闭合。 |

当前 Assets 的695个源码/程序集定义文件及695个meta已逐项只读核对，与Windows30冻结图完全一致，canonical SHA256为 `7530aac124b2a65dd9ad24e9666c1bfb1acf0af41c16ce7d4150119018210602`。实际单次编译通过，Editor Runtime MVID为 `cdd7c66d-f168-4b2a-901a-bbe9e1be2cd8`；本图未声称重跑历史184/90/20等测试。编译绑定见 [compile-binding.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/BuildPreparationObservationApplication01/compile-binding.json)。

Windows30正式Addressables→Windows Development构建于UTC `2026-10-15T17:20:49.2310421Z`结束。诊断包为 [InkLan.exe](D:/XPHUNITY/ProjectSplatoon/Builds/Blocks-20261016-diagnostic30/InkLan.exe)，完整359文件、322125157字节，包Runtime MVID `d53bdf6f-a4bf-40e3-b164-4354d45a443f`，Runtime DLL SHA256 `3d46854bdf8df175a97506cfbfa51bf98155e2b298074aa2ad1536a2be2986da`。实际身份文件SHA256为 `ecd80d99fdc9a9d010578a43662b4d2f227b6d1baa4c109641638b41374ea9e0`。原inspector的两次失败保留；修正后的组合inspector已核验实际AutoScope的constrained IDisposable finally链，identity明确区分冻结基础组件与新adapter，没有把旧脚本记为原样通过。见 [build-record.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows30Preparation01/build-record.json) 与 [package-identity.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows30Preparation01/package-identity.json)。

CPU30为8个独立Player、D3D12、实际1920×1080正常主相机，实测采样20.0058113秒/1945帧，最少284426存活格，八人各200发、射击覆盖1.0，事务失败和errors均0。帧时P95=14.3858ms、P99=17.7249ms（门槛15.5/16.67）；普通主攻击发布P95=766.6667ms（门槛200，只有8个完成样本）。建造命令增queued2184/completed2160，残队6→30；不能据短窗口宣称Windows29的180秒严重积压已修复。新counter starts=observedStarts=15、zeroPreparedStarts=14（93.3%潜在资格），不证明metadata writer为空或优化收益。原summary仍为performancePassed=false、requiredWorkloadsComplete=false、fullAcceptancePassed=false。见 [CPU30 summary](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Cpu30Run01/prototype-v1/summary.json)。

Extraction30离线提取实际exit0，1945/1945个四long metadata在后续文件分析中精确对应，27个所选帧/278215个节点，树拓扑错误0，原空Profiler history恢复API成功。无扫描、metadata、thread或sample截断；`seedSelectionCapped=true`保留预定种子选择上限，本次为8普通种子+1长帧种子及邻帧，不代表遍历所有正式帧的调用树。只观察到两次OwnedClone原始inclusive phase0 slice，0.049/0.0484ms；不扩称exclusive clone成本、完整请求延迟或整体热点已排除。原strict timing的2859处异常保留、negativeSelf=0，不使用无效self归因。见 [manifest.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Cpu30Run01/CpuExtract01/manifest.json)（SHA256 `38a520a2148637e6b9f6737f47ca5504fc7ead60101ccb38c226a453d67305bd`）及 [ownedclone-paths30.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Cpu30ExtractionPreparation01/ownedclone-paths30.json)（SHA256 `abc47b8842cf19bee9f93721355b3577d28df69d69468dbef4f3f9e082ec4c15`）。

Ready30同包首组2人/0ms/0%已终态，原45秒drain超时，networkMatrixPassed=false。房主57条drain状态均已确认两人同life的drain输入ACK；弹丸/sub/special pending均0。实际阻塞为pending162→44、建造574条仅完成412→530、fallingCells1548→1626、unstableColumns892→1095、quiet始终0，队首年龄10.7→46.33秒。队列仍推进，现有报告没有phase0/1/2及retry明细，不能判断复制或反复reject是主因。客户端drain无raw/human correction，房主drain无新增safety recovery；最后共同revision762/总量9149及相关hash一致，属于动态共同状态，未达到静止验收。两端实际持续1024×768，normalCameraFrames=0，缺1080p帧证据。旧Windows29 motortrace曾走过drain和后续阶段，仅为非稳定对照；窗口sizing候选尚未执行，不计本草案结果。见 [Ready30 summary](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Ready30Run01/summary.json) 与 [case.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Ready30Run01/p2-rtt0-loss0/b0e7e8a9/case.json)。

CPU30与Ready30均记录完整359文件包前后一致；窄偏好restored/rawEqual/branchExistenceEqual均true，所属Player已有终态。CPU30原harness/native5与客户端收尾exit1保留；Ready30两Player自然exit0、native4保留，退出不等于验收通过。离线提取没有启动Player。D3D12已成功启动，用户条件性WMI/vmms管理员恢复许可没有触发，未据此执行服务操作。后台隔离、禁系统鼠标捕获、不激活窗口、不切用户输入桌面及测试/构建/Player串行要求继续生效。

按 [GoalScopeDeferTowerCascade20261015.md](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/GoalScopeDeferTowerCascade20261015.md)，截图整组的32米高塔拆底、两万格级联下落及各自专属最终碰撞/压力性能验收，继续标记 **user-deferred-not-accepted／用户后移、本次未验收**，不记Passed、不阻挡本次Goal。普通重力、支撑、守恒、扣费、真实碰撞和32米玩法顶界保留；真实多机与人工操作观感两项外部验收继续不计Goal。

另一次同包后台窗口诊断 Ready30SizedRun01 已于UTC2026-10-15T17:47:30.789708Z结束，Root session78687 outer1/native4。两个自有窗口的物理客户区均曾达到1920×1080，但调整早于Unity完整初始化，后续实际Screen仍1024×768、stableFrames=0；物理窗口尺寸不等于渲染尺寸，原帧证据不足失败保留。该次仍在45秒drain超时，后续冻结/重连/清空/重建未覆盖；没有增加通过项。完整359文件包前后一致，窄偏好restored/rawEqual/branchExistenceEqual均true，两归属Player56760和8328自然exit0。原Ready30结果不改写，新的延后窗口调整仅在Reports准备，尚未执行。

以下历史快照保留原文、原源码图和原包归属。

---

# 首版实施状态

## 2026-10-16 带轨迹联机诊断完成；标志缓存已编译验证，Goal仍未闭合

同Windows29包的首组2人/0ms/0%追加motor trace诊断已由Root亲见终态：session43865、外层1/native4、chunk43ddfc，UTC2026-10-15T16:45:12.584682Z。两归属Player自然exit0，359文件完整包前后一致，测试前偏好restored/rawEqual/branchExistenceEqual均true。本次走过drain、fall、frozen、reconnected、cleared、rebuilt，原case失败只列host/client缺normal1080p帧诊断证据；它是带观测开销的单组诊断，不能转记六组联机或性能通过，也不覆盖旧NetworkMatrix01的真实drain超时。实际终态与清理记录见Windows29Preparation01/NetworkMotorReview01/root-diagnostic-terminal01.json。D3D12继续成功启动，用户条件性WMI/vmms管理员恢复未触发，未执行系统服务操作。

本次唯一Assets变更为BlockNetworkProbe.Requested的过程常量命令行标志缓存，保留原LF及meta；695源码/695meta图为a98f36d9e95e9429f14c59d4b180bf0de506b499919d75144d45bddd971e3b47。原隔离Editor35536实际单编译completed、failed=false、errors=[]；新Runtime MVID351a38fd-5062-417d-8892-a032027eaa56、Tests b3ae23f0-7408-47a2-9b55-db77f1cc4de4、Config不变，均与磁盘一致。实际getter10000次读取结果全等于本Editor真实flag=false，线程分配0；原表达式对照10000次分配也为0，不声称减少分配或提升Player帧时。证据在RequestedFlagCacheApplication01/compile-binding.json、getter-validation.json及独立audit-result.json。本次未构建新包，Windows29仍绑定原cb49源码图，原184/90/20等回归保持各自原归属。

最后实际只读确认Editor35536仍为CodexBlockTests20261013a、输入桌面Default、双focus=false、六项闲置，当前没有运行中的Player或Unity测试/构建lane。性能首场的帧时与积压失败、六组正式联机首组失败及后5组未运行、100ms/1%弱网纠正未覆盖均保留。Goal继续active、五类未整体闭合；高塔/级联压力整组为用户后移/本次未验收，外部多机和人工观感不计Goal。以下历史快照原文保留。


## 2026-10-16 Windows29自然三分钟流程通过；联机与性能仍未过

Root亲见自然比赛原native47645终态exit0（chunk5b4e33），UTC2026-10-15T16:31:03.947715Z。Windows29同包双独立Player完成首局自然三分钟、存活积木结算（Pink3339/Blue706/Revision1993）、返回房间、下一局及双方clean leave/自然exit0；原12项自然流程观察gate全为true。完整359文件包前后一致，窄偏好raw/分支精确恢复。证据见Windows29Acceptance01/NaturalMatch01/summary.json和Windows29Preparation01/root-actual-terminals29.json。

本结果限定为naturalRoundWorkflowObservedPassed=true，packageSmokePassed=false保持原补充范围；没有代替六组联机中的回合清空、中文HUD真实背缓冲图像检查或四场性能验收。Windows29首组联机drain及首场steady性能失败保留；后续各组/各场按failfast未运行。三个实际lane均已终态并完成清理，目前没有Player验收lane在运行。D3D12已经启动，用户条件性WMI/vmms恢复未触发，未执行任何服务操作。Goal保持active，五类尚未整体闭合，高塔/级联整组后移及外部两项不计Goal的范围保持。以下历史快照保留原文。

## 2026-10-16 Windows29首场180秒性能未通过；其余三场未执行

Windows29首场steady250k原native21163已实际终态外层1/native5（chunk82572c），偏好守卫收尾于UTC2026-10-15T16:25:13.218319+00:00。本次采样180.0170599秒、8596帧，报告为Direct3D12、1920×1080正常主相机、8个独立Player、最少283758存活格，八人持续射击覆盖1.0。Windows29Acceptance01/Performance01/prototype-v1/summary.json确认仅完成首场，performancePassed=false、requiredWorkloadsComplete=false，交火/八爆/零散后三场未执行；这更新了上一节“尚未启动”的历史快照。

首版仍有3项硬失败：帧时P95=39.44820165634155ms（门槛15.5ms）、P99=64.94239717721939ms（门槛16.67ms）、普通主攻击发布P95=106750.14119983431ms（门槛200ms，2438个实际完成命令样本）。成功pending事务自身墙钟P95=36.7994ms不能代替完整攻击发布延迟；期末建造命令累计queued=16235/completed=5817，peakPending=10414，排队仍严重。完整帧最大3101.8645763397217ms、超过33ms的帧703个，按既定首版标准保留为诊断风险，不额外算硬门槛；现有数据不足以指认单一CPU热点或将全部长帧归于地形。

包前后完整359文件及身份SHA一致，窄偏好restored/rawEqual/branchExistenceEqual均true。8个归属Player已退出：host67736为5，7个client均为1，属于harness收尾，不记为自然成功退出。独立只读失败审计见Windows29Preparation01/PerformanceFailureReview01/findings.md及review.json，原始证据不改写。审计同时保留原字段差异：采样width/height及主相机PNG为1920×1080，normal1080pMainCamera原gate通过；预采样sidecar也记录实际1920×1080但formalSamples=0，终态screen/display字段为1024×768，不能用预采样单点替代全程逐帧证明，也未据此改写原gate或增造失败项。

Goal仍active、5类未整体闭合：功能与完整比赛、四场1080p性能、弱网移动纠正、六组联机恢复、最终Windows包和报告。Windows29首组网络失败及后5组未执行保持上一节结论；旧包自然比赛与回归归属不转记新包。高塔/级联整组继续user-deferred-not-accepted，外部多机/人工观感不计Goal。D3D12已成功启动，条件性WMI/vmms恢复未触发；后台隔离、禁系统鼠标捕获与测试/构建/Player串行约束保持。以下历史记录保持原文。

## 2026-10-16 Windows29正式构建与包核验通过；首组网络在移动后排空阶段失败

本节按UTC+8本地2026-10-16记录，快照截至Windows29首组网络收尾（UTC2026-10-15T16:16:17.498265+00:00）。原正式Addressables→Windows Development构建native17164已实际终态exit0（chunk7c834e），独立inspector实际exit0（chunk87b35a）；Windows29Preparation01/build-record.json记录finishedUtc为2026-10-15T16:12:49.2386033Z，candidateBuilt、productionBuildReturned及formalAddressablesBuildSucceeded均true。包位于Builds/Blocks-20261015-acceptance29/InkLan.exe，完整359文件、322120825字节，库存SHA为25a39323c45f867e1262f4df3e58f84844521e6d5789f35d472a9c88733d1ce4；package-identity.json实际SHA为7cd75ef4b383b692de8bd743c6d5c1395443ceb1ac9c3a46a2c097ec6b59bc92。构建与包身份核验通过不等于最终交付验收通过，goalAcceptancePassed仍为false。

原六组网络native42195已终态外层1/native4（chunk1130f6）。Windows29Acceptance01/NetworkMatrix01仅实际执行首组2 Players/0ms/0%，在完成遍历后等待“traversal input acknowledgements and intact queue/projectile/falling drain”超时，progress记录107.1秒。case.json的networkPassed=false，failureReasons同时包含未取得clean drain及参与者权威输入ACK，现有证据不足以把根因单独归于重力或ACK。冻结、重连、清空、重建checkpoint均为null，属于后续阶段未覆盖，不分别算作已实际执行失败。summary.json为requiredCaseCount=6、completedCaseCount=1、networkMatrixPassed=false；六组中0组通过，后5组未执行。1080p帧诊断不足也保留，但本组不能据此形成独立性能结论，100ms/1%弱网纠正仍待实际覆盖。

本次package-binding29.json确认前后完整359文件库存一致、身份SHA相同，samePackageVerifiedBeforeAndAfter=true、acceptance=false。StandalonePreferences/verification.json确认restored=true、rawEqual=true、branchExistenceEqual=true，原bootstrap及guard均exit4；本次归属Player18520和47544均自然exit0，proxy也exit0。D3D12已实际启动，用户授权的条件性管理员WMI/vmms恢复条件不成立，未执行服务恢复。

fresh-fixtures-141的六完整类20/20 Passed仍严格归于695源/695meta重力图89222e2654e2cb96dd0e36c961070bc4215ddb0329b8a6e294ad85cbc76c0f69；最终构建图为cb49fc7f16fca7cf7a810c3dc7c811d52d864e17605c59d81084e3f0c4d1d97b，其UI进度环补充像素验证单列，impactCasesExecutedOnFinalGraph=false。旧Windows27自然比赛流程和Windows28性能/网络结果保留原包归属，均不转记Windows29。

Goal保持active，仍有5类必需门槛未整体闭合：功能与完整比赛、四场1080p性能、弱网移动纠正、六组联机恢复、最终Windows包和报告。本节快照时Windows29四场正式性能仅已准备、尚未启动、无新结果；采样时长继续为steady180/交火300/八爆120/零散180秒。以GoalScopeDeferTowerCascade20261015.md为准，32米高塔/两万格级联及其专属碰撞与压力性能整组保持user-deferred-not-accepted，普通重力、守恒、扣费、真实碰撞与32米玩法顶界保留；外部真实多机和人工操作观感不计Goal。后续测试/构建/Player继续串行，复用隔离Editor35536及CodexBlockTests20261013a，输入桌面保持Default，沿用SPLATOON_DISABLE_SYSTEM_CURSOR=1与Player -noSystemCursor，不激活窗口、不切换用户桌面、不捕获系统鼠标。以下历史记录保持原文与原归属。

## 2026-10-16 重力关联20项回归及UI像素验证通过；Windows29正式构建进行中

本节按UTC+8本地2026-10-16记录（实际收据为UTC2026-10-15 16时段）。fresh-fixtures-141原native90814已真实终态exit0（chunk024dc8），完整六类3+7+2+4+1+3共20/20 Passed；原始及归档XML、完整唯一fullname与summary一致，domain reload后仅保留的Status尾记录未被当作全量执行数。19逻辑/17物理偏好及10文件精确恢复，restored=true、pendingRestoration=false。实际独立文件审计exit0，MotionBudgetFairnessApplication01/Audit14101/current-runtime141-audit.json的SHA为812b540605bda4cafcb9afc7dc87248e1429e093bbe693f201726bca19e5dd4a；46个证据pin经追加只读复核均一致。该20例严格归于695源/695meta重力图89222e2654e2cb96dd0e36c961070bc4215ddb0329b8a6e294ad85cbc76c0f69，不迁移为后续UI图上重跑。

在141恢复完成后，仅应用已审查的PrototypeApp.UiTheme.cs进度环缓存候选，源码SHA为e26b628ed024b3b8adeafc6aef518f2fcbafb8d9b6b6162e6b7c173768e9d3c0。原隔离Editor单次实际编译通过，RingCacheApplication01/compile-binding.json绑定最终695源/695meta图cb49fc7f16fca7cf7a810c3dc7c811d52d864e17605c59d81084e3f0c4d1d97b，Runtime MVID为a6a33c2a-bc7f-4bc9-a255-9bf9f7f5c59b。

实际Unity ring-pixel01已退出0，文件oracle验证通过；101个进度值各比较16384像素，累计1654784个Color及1654784个RGBA32纹理像素零差异，32768个几何float零差异，另204次数组复用及405次合成事件门控检查通过。结果见RingCacheApplication01/pixel-validation.json及Cpu28Extraction03/RingCacheCandidate01/IndependentReview01/ring-pixel01.stdout。实际执行范围为EnsureUiProgressGeometry与精确提取的纯更新prefix，使用合成Repaint；未执行完整OnGUI，不代表完整画面或Player性能验收。

Windows29Preparation01实际bind已退出0（chunk4d4d45），已生成build-input.json与actual-current-build-binding29.json；其中impactCasesExecutedOnFinalGraph=false，明确保留20例重力图与最终UI补充图的归属。Root随后单次dispatch正式Addressables→Windows构建，原native17164正在进行，build-start.json记录UTC2026-10-15T16:09:00.6444753Z；当前尚无构建成功或包核验通过结论。Windows28旧包的性能/联机失败保留，不能据本次Editor回归与像素验证宣称Windows29联机、性能或完整比赛通过。

Goal保持active，仍有5类必需门槛未整体闭合：功能与完整比赛、四场1080p性能、弱网移动纠正、六组联机恢复、最终Windows包和报告。四场正式采样仍分别为steady180/交火300/八爆120/零散180秒。以GoalScopeDeferTowerCascade20261015.md为准，32米高塔/两万格级联及其专属碰撞和压力性能整组为user-deferred-not-accepted；普通重力、守恒、扣费、真实碰撞及32米玩法顶界保留。外部真实多机与人工操作观感两项不计Goal。D3D12已实际启动，条件性管理员WMI恢复未触发、未操作。测试/构建/Player继续串行，Editor35536保持CodexBlockTests20261013a、输入桌面Default、SPLATOON_DISABLE_SYSTEM_CURSOR=1及Player -noSystemCursor；不激活窗口、不切换用户桌面、不捕获系统鼠标。以下历史记录保持原文与原归属。

## 2026-10-15 重力预算饥饿已实际复现并修复，20项关联回归进行中

仅新增测试、保留原runtime的fresh-fixtures-140实际准确复现：前序3949 checks与冻结快照检查通过后，MotionCursor期望9、实际0。原native83024终态exit1、runner3，原失败XML与归档/Status一致，19/17偏好及10文件全部恢复；独立审计见MotionBudgetFairnessApplication01/Audit14001/original-runtime140-audit.json。该失败是修复前故障证据，不记验收通过。

随后仅应用已独立审查的GravityCohorts调度修复：当前695源/695meta图89222e2654e2cb96dd0e36c961070bc4215ddb0329b8a6e294ad85cbc76c0f69，实际单次编译成功，绑定见MotionBudgetFairnessApplication01/runtime-compile-binding.json。完整六类20项fresh-fixtures-141正在运行；首个Fairness完整类已真实3/3 Passed（XML20261015-151909.xml），包括预算重试、空中继续轮转和两种源顺序的真实碰撞落定。余17项及本轮完整恢复未终态，不能记20/20或联机排空已通过。

18帧CPU诊断已完成；进度环缓存只在Reports准备并经独立审查，尚未应用。准备的实际Unity像素验证与Windows29正式构建尚未执行。Windows28仍是旧137包；其性能及联机失败保留。Goal保持active、5类未整体闭合，高塔/级联整组后移、外部两项不计Goal，条件性WMI恢复未触发。测试/构建/Player继续串行，原隔离桌面、禁系统鼠标捕获、不激活窗口约束保持。以下保留历史记录。

## 2026-10-15 保护区遍历和诊断分辨率修复已编译，完整查询回归90项通过

本次应用4个已审查源文件：垂直保护区prefix跳过、压缩checkpoint回放、关联查询测试，以及仅对显式frameProbe生效的窗口分辨率设置。未改预算、积木规模、付款、FIFO或验收门槛。首轮应用在第4文件写入时失败，前三文件与候选相同、第4文件保持原状；随后以窄patch完成第4文件并逐项匹配候选，原失败记录保留。原隔离Editor单次编译明确completed且无错误；重载后的首次HTTP读取失败，独立后读成功，未再次编译。实际编译绑定见ProtectedPrefixApplication01/compile-binding.json，694源/694meta图为96e8eb1b1d3303cb77755095b50a54c3464518f8afa1d19b409f538d0257b5c9。

fresh-fixtures-139运行完整BlockBuildQueryTests，真实90/90 Passed，包括18个新增参数例；原runner与外层均0，restored=true、pendingRestoration=false、sourceAndMetadataUnchanged=true。XML为20261015-144610.xml。该结果不转记旧22项或旧184项，也不代表新Player性能或联机已通过。Windows28包仍绑定旧137源码，新4文件尚未进入正式包。

同Windows28包的短路径CPU诊断已实际运行8独立Player、D3D12、1920×1080正常主相机、20.009秒，1827个样本；raw/sidecar已稳定保存，包前后一致且偏好精确恢复。原harness保留exit5和diagnosticOnly，不计正式180秒性能。第二份代表帧提取已真实exit0：18帧、204426原始样本，1827/1827四long metadata精确配对、五项cap均false、Profiler history恢复。严格self区间分析1870处异常、负值0原样保留；仅使用原始inclusive父路径。P95/P99选帧与晚期慢帧中可见OnGUI、FixedUpdate及网络开销，晚期部分帧实际有约3ms GC，不能统一归因于GC或把父子时间相加。详细证据见Cpu28Extraction03/CpuHotspotAnalysis01/findings.md；尚不代表正式180秒验收改善。

双人0ms/0%的同包CPU诊断也已结束，原native4、两Player自然退出0、包和偏好恢复一致；停止射击后480/480命令完成但地形仍未排空。保存了退出drain前revision919的完整已发布快照，6832静态格+978下落格。只读分析发现多个已到落点的motion长期保留，同时源码可构造末尾预算饥饿；最小回归及修复仍待实际验证，不能提前宣称根因已闭合。原Windows28正式网络的最后双人drain是1710不稳定列/1459下落格；退出后的current1468属于收尾状态，两者不混用。

Goal继续active，仍有5类必需门槛未整体闭合。截图的32米高塔/两万格级联压力组维持user-deferred-not-accepted，外部多机/人工观感不计Goal。D3D12已经实际启动，条件性管理员WMI恢复未触发、未执行。测试/构建/Player串行，原隔离桌面和系统鼠标保护继续生效。以下保留历史记录。

## 2026-10-15 Windows28正式包完成；首场性能和首组联机仍未通过

Windows28已通过原正式Addressables→Windows Development构建及独立包检查：359文件、322113129字节，完整库存SHA为bd9227258fe1501774a3152d6bf52a0b7648ef236857f62b7f411a8b9bca70f9，身份文件SHA为46734f16a5de698efda9157cdbbc93ab45475ee5269d5c491d0dee6277d9db23。Root亲见构建native66492终态0（chunk10d2f3）、inspector终态0（chunkc5421a）。该包绑定137源码及本次13+9=22项、2完整类实际回归；历史184项与Windows27自然三分钟比赛通过保持旧来源，不转记28。

Windows28 D3D12首场steady250k真实运行八独立Player、1920×1080正常主相机、180.004秒、12455帧，最少292158格、八人射击覆盖100%。原native18557终态外层1/native5（chunk0565ca）。三项硬失败为帧P95=23.494ms（门槛15.5）、P99=30.339ms（门槛16.67）、普通主攻击发布P95=833.333ms（门槛200）；另三场按原失败停止规则未运行。成功pending事务墙钟P95=24.312ms，完成窗普通攻击308样本仅涵盖实际Changed>0的完成命令，不能代表所有入队命令。与Windows27约83.2秒相比已改善，但仍未过。包前后359文件一致、窄偏好精确恢复、归属Player已退出。

Windows28原六组网络native55418已终态外层1/native4（chunke158d6），UTC14:03:09.057320Z。首组双人0ms/0%进入真实比赛并完成35秒移动，停止射击后的原排空阶段超时；末态507/507攻击命令完成，仍有1710不稳定列、1468下落格和1个pending，后续冻结、有限下落、重连、清空、重建均未覆盖。双人两个Player自然退出0，包前后一致、窄偏好raw/分支精确恢复；后五组未执行，不记通过。两端实际帧报告为1024×768，虽启动参数为1920×1080，仍缺原1080p诊断证据，不能放宽分辨率oracle。弱网100ms/1%未运行，不能用本组纠正统计代替。

独立postrun只读于UTC14:06:39.448811Z确认原Editor35536、137三MVID、六idle、两focus=false、隔离桌面CodexBlockTests20261013a和输入桌面Default。D3D12已成功启动，有条件WMI恢复许可未触发，未执行服务恢复。Goal继续active，剩五类未整体闭合；高塔/级联整组user-deferred-not-accepted，外部多机/人工观感不计Goal。以下保留历史原件与旧结果。

## 2026-10-15 建造阻挡见证修复关联22项实际通过；Windows28待正式构建

137轮仅应用已独立审查的 BlockTerrain.BuildOccupancy.cs 及原综合 Shallow 测试两份源码，未增加 meta。原编译状态010明确 completed、failed=false、errors=[]、compilationFailed=false；随后首次 post-read 因 Connection reset by server 失败，沙箱内第二次读无法发现实例。Root未重复应用或编译；沙箱外第三次后台只读已真实退出0，UTC12:45:51.710607Z确认原Editor35536已加载 Runtime ac004b24-5a6c-45f6-8b03-7ccd7df4a880、Tests d3d6abbd-ac1a-4683-a750-ab7a12a8cf10，Config不变、六idle、两focus=false、原隔离桌面/输入桌面Default。

实际 compile-binding137.json 将新694源/694meta完整图 f185d3185456731d6e69b5ff25411ec7e22edcfb74c19c2c6adfc903abca8220、三Library DLL及PE MVID与原编译/新读取收据绑定；原应用driver exit1与失败读取均保留。原native11477因1200秒观察窗口超时，真实终态exit1（chunk c662b3）；Unity原异步测试没有被取消或重复提交。原Shallow13随后自然完成，Root通过固定原oracle核验13个完整方法名与XML/status计数，13/13 Passed；capture实际exit0（chunk f82b7e），原失败及pending恢复快照保留于Observation137Supplement01。原137已通过真实Recovery精确恢复19逻辑/17物理偏好与10文件；独立postrun01于UTC13:25:24确认空闲。随后138只执行完整LateFloor9，实际9/9 Passed，原native54576亲见exit0（chunk60e752）、runner0、restored=true、pendingRestoration=false，未重复13项。完整XML为20261015-134200.xml；694源/694meta图、前后137三MVID及19/17/10恢复一致。独立witness138-postrun01实际exit0（chunke2e917），UTC13:44:02确认原35536、六idle、隔离桌面与两focus=false。本次实际关联22项/2完整类，原137观察器超时失败保持原归属，不扩称旧184条全量重跑。候选正确性通过不代表性能已改善，Windows28正式包及原规模真实性能仍待执行。

Windows27自然比赛流程通过和132图47项定向结果均保留原归属，不转记137通过。D3D12已成功实跑，WMI恢复许可条件不成立，未重启服务。Goal继续active，剩五类未整体闭合；高塔/级联整组仍user-deferred-not-accepted，外部多机/人工观感不计Goal。以下保留历史快照。

## 2026-10-15 自然三分钟比赛流程实际通过；性能排队修复待编译验证

原D3D12自然比赛第二次native5155已终态0，UTC12:35:56.309048Z。Windows27同包双独立Player完成首局180秒自然结束、真实积木结算（Pink3856/Blue706/Revision987）、返回房间、下一局及双方clean leave/自然exit0；原12项流程观察gates全部true，包359文件前后一致、窄偏好精确恢复。该结果仅为naturalRoundWorkflowObservedPassed=true；packageSmokePassed=false保持原补充覆盖边界，HUD视觉和联机回合清空等仍待相应证据。详细见 Windows24Preparation01/BuildQueryCacheApplication01/Windows27D3D12NextLanePreparation01/PackageSmoke02/RootTerminalReview20261015.md。

首次D3D12自然观察native78389虽进入Playing，但进度文件替换WinError5而异常收尾；Root实时读文件可能与替换冲突，已改用ReadWrite|Delete共享读，第二次全流程通过，旧失败保留。未执行WMI恢复。当前包的首场八人性能仍失败（普通发布P95约83.2秒），另三场未跑；弱网与六组恢复仍未过。剩五大类未整体闭合，Goal继续active，用户后移与外部验收范围保持。

## 2026-10-15 Windows27 D3D12 已真实采样；首场性能失败，普通重力新增 9 项通过

原性能 native43700 已终态：外层1/native5，UTC12:18:55.235987Z。D3D12成功启动并实际1920×1080正常主相机渲染；steady250k实跑八独立Player、180.007秒、14405帧、最少285259格、八人持续射击覆盖100%。帧时P95=20.302ms/P99=34.445ms及普通主攻击发布P95=83219.617ms未过首版门槛；队列从约374增至9969，继续定位排队。完整包359文件前后一致，窄偏好精确恢复、八归属Player退出。详情见 Windows24Preparation01/BuildQueryCacheApplication01/Windows27Candidate01/Performance01/RootTerminalReview20261015.md。

同132源码第136轮两个完整普通重力类实际9/9通过，原native9344终态0；独立文件审计auditPassed=true，694源码/694meta、三MVID、10文件及19逻辑/17物理偏好一致。当前加此前38项共47项定向回归，不扩称历史184项本轮全量重跑。构建时包identity保持原始记录，不回填新的测试数量。

用户条件允许一次WMI恢复，仅限D3D12仍无法启动。本次已成功启动，条件不成立，未执行恢复脚本或重启服务。Goal仍active、剩五类；截图高塔/级联整组仍user-deferred-not-accepted，普通重力/守恒/扣费/碰撞/32米玩法顶界保留，外部多机/人工观感不计Goal。以下为历史快照。

## 2026-10-15 截图整组后移已生效；Windows27自然比赛观察止于启动

用户截图四项（32米高塔拆底、两万格级联下落、两场专属最终碰撞核验、压力性能记录）已整体排除本轮必过范围，以GoalScopeDeferTowerCascade20261015.md为准，记user-deferred-not-accepted。普通重力、守恒、扣费、真实碰撞和32米建造顶界保留；同一Goal保持active，剩五大类，外部多机与人工观感仍不计Goal。

Windows27原自然比赛观察native18032已真实终态exit1，UTC11:47:02.221131Z。原420秒时限内只有Host93164，没有Client/host.log/比赛正文，原summary记录Natural package smoke timeout。尚未实际检验自然三分钟比赛、结算和下一局，不能把这些各自计成已运行的玩法失败。正式构建与完整包来源检查继续有效；适配器收尾package-binding27.json确认前后359文件一致，原summary的packageAfter=null保持原件不改。偏好raw/分支精确恢复、ownedHost已退出，209次窗口采样未见已识别Player进入输入桌面，但有不可分类的其他窗口，不能夸大为完整焦点证明。

本次在原Host上取得的CreateProcess初始线程身份和COM-aware WCT实际证明：Thread93164/86304 Blocked → COM Owned → PID3352；Root服务映射确认当前winmgmt为3352。具体WMI query/provider和内部原因仍未知；未重启或修改服务，未启动新的Player。详细原终态、证据边界与恢复见Windows24Preparation01/BuildQueryCacheApplication01/Windows27Candidate01/PackageSmoke01/RootTerminalReview20261015.md。六组网络、弱网纠正和四场性能仍待实际通过，当前38条受影响回归不扩称历史184条新源码全量重跑。以下历史快照保留。

## 2026-10-15 Windows27正式构建与包核验通过；双人诊断止于Player原生启动

有效范围仍为 GoalScopeDeferTowerCascade20261015.md：截图内32米高塔、两万格级联下落及两场专属最终碰撞/压力性能整组后移，记 user-deferred-not-accepted。普通重力、守恒、扣费、真实碰撞、32米玩法顶界及联机恢复保留；仍剩五类，外部多机/人工观感不计Goal。

Windows27已实际调用原BlockGameplayBuilder正式Addressables+Windows Development入口成功。Root亲见原native33867终态exit0（chunk6be5cb），build-record finishedUTC11:24:12.5743287Z；359文件、322111601字节完整库存匹配。独立inspector实际exit0（chunk95e25c），新identity SHA9003b719842bedc2354a04ee9b7c1ee0318f847774ba0119996b57581c09816a，Runtime DLL SHA50a3d0ed27a48b062ea959876e6d170766a65e2e727d338d6c7d55fa65445aa3。根目录证据位于Windows24Preparation01/BuildQueryCacheApplication01/MinimumLandingSupportRepair01/Windows27Candidate01。Root独立postbuild Read于UTC11:25:05.836Z确认原35536、132三MVID、六idle、两focus=false、输入Default；694源码/694meta保持132图，当前实际回归仍为133两完整类38/38，历史184不扩称本次重跑。

随后必要p2 100ms/1% CPU诊断原native53611已终态失败（Root chunkd88369，外层1/原harness4）。Host46640约45秒未产初始报告，Client未启动；本次实际没有host.log/blocks/frames/cpu.raw，尚未进入玩法取样。约57秒含原12秒清理等待，exit1不能据此称为自然崩溃。原包前后完全一致；独立WCT helper实际exit0，但8条都只有单Thread节点、主线程未证明、根因unknown，不能指认WMI。诊断、原case及恢复原件位于同上层Windows27Candidate01/NetworkDiagnostic01；窄偏好raw/分支精确恢复、归属Player已退出，29次窗口审计未见Player进入输入桌面。

最低落点修复现已进入新正式Player包，但此次启动超时没有检验普通重力排空，联机问题仍待复测。下一步先对下一次必要Player启动补充最小只读原生线程/模块身份观察，避免盲目重复六组相同启动失败。五类仍为功能与自然三分钟比赛、四场1080p性能、弱网移动纠正、六组联机恢复、最终Windows交付报告。测试/构建/Player串行、隔离桌面和禁系统鼠标捕获继续，不提交或发布。以下历史记录保持原归属。

## 2026-10-15 截图极限压力整组后移；最低落点修复后两完整类38/38通过

本次Goal仍按 GoalScopeDeferTowerCascade20261015.md 执行：截图四行（32米高塔拆底、两万格级联下落、两场专属最终碰撞核验和压力性能记录）整体后移，不计本次必过项、不记Passed。普通重力、守恒、扣费、真实碰撞、32米玩法顶界和联机恢复保留；剩五大类，真实多机/人工观感继续不计Goal。

最低落点支撑缺口已最窄修复并完成132源码编译。原相同生产探针复测supportedBefore=true、detached=false。fresh-fixtures-133实际执行完整BlockWorldTests37/37及BlockStaticSupportQuantizationTests1/1，含新增8条负坐标/非整格底界/敌我计分/快照回归。Root亲见原native78702终态exit0，runner0、restored=true、pendingRestoration=false；694源码/694meta前后相同、原始及归档XML一致、10文件与19逻辑/17物理偏好精确恢复。独立minimum-support133-postrun01于UTC11:10:19确认原35536、新132三MVID、六idle、两focus=false和输入桌面Default。当前38条真实通过，不扩称旧184条全部在新源码上重跑。

首次fresh-fixtures-132未提交测试：新进程预检的全项目exe目录扫描造成30秒观察超时的可信候选，原件保留，未记功能测试失败或恢复失败。133去掉这项不必要目录遍历，仍以只读Win32映像与命令行查询保留原Player/runner阻挡规则、30秒检查上限、原隔离与恢复链；两完整类随后均实际通过。WCT只读诊断入口已准备但尚未对新Player执行，不据此宣称启动故障根因。

Windows26包核验通过与六组0/6失败仍归旧126源码；最低落点修复尚未进入新的正式Player，不能声称网络排空问题已解决。自然180秒比赛仍未采到正文；四场正式1080p性能尚未启动。后续按五类继续：功能与自然比赛；常规/交火/零散/八爆性能；弱网移动纠正；六组联机恢复；最终Windows包与报告。原Goal保持active，串行测试/构建/Player，不提交或发布，继续隔离桌面和禁系统鼠标捕获。以下为历史快照。

## 2026-10-15 Windows26六组网络未通过；132底界支撑修复已编译、窄回归待执行

有效范围为 GoalScopeDeferTowerCascade20261015.md：截图内32米高塔、两万格级联下落及两场专属最终碰撞/压力性能整组后移，标记 user-deferred-not-accepted。普通重力、守恒、扣费、正常碰撞、32米玩法顶界及联机恢复保留；仍剩五大类，外部真实多机与人工观感不计 Goal。

Windows26正式包和359文件完整性核验通过，但原native60225已终态失败，六组只完成尝试、0/6通过。三组双人均已消费停止输入ACK并完成攻击命令，普通下落/不稳列仍未排空；后续冻结、重连、回合清空重建未覆盖。八人0/0未完成第八人准入，100/1和200/3未达Host初始同步。原日志不支持将所有失败归因于ACK或WMI。详细证据为 Windows26Preparation01/Network26FailureAnalysis01.md。

自然180秒比赛smoke原native34250已失败：420秒内Host无日志/无窗口，未启动Client，尚未采到比赛正文。CPU诊断原native52248也未达Host同步，但其Host日志已走过Mono、D3D11、PhysX与输入初始化，尚无LAN Ready；这两次启动停点不同，具体根因未证实。两次偏好均精确恢复，归属Player均已退出，窗口审计未见Player进入用户输入桌面。

Root在加载的生产BlockWorld探针中独立复现最低合法格缺少支撑、反复原址Detach/Settle且计分守恒。现仅补充合法最低落点的支撑分支，保留原锚固、下方格和地图回调；新增八条参数化行为回归覆盖负坐标、非整格底界、敌我计分及快照。132编译真实完成且无错误：Runtime MVID 6e9aa53d-c682-4e18-b740-981c683607a6，Tests a322c82b-6461-42e1-a309-47da86fdd6a6；同一探针复测首次supportedBefore=true、detached=false。它确认独立缺口已修复，尚不证明网络排空失败已解决。184/184只属于旧126源码图，不能记为132通过；132完整受影响回归及新正式包待完成。

剩余：功能回归/普通重力与自然比赛；四场1080p性能（尚未启动正式lane）；弱网移动纠正；六组联机恢复；最终Windows包和报告。继续同一active Goal，测试/构建/Player串行、原隔离Editor35536、禁系统鼠标捕获、不激活窗口、不切用户桌面、不提交或发布。以下保持历史快照原归属。

## 2026-10-15 Windows26正式包与完整包复核通过；六组联机实际运行中

Windows26正式Addressables和Player构建成功；Root已读取实际build-record，并独立Read确认原Editor35536、原三MVID、六idle、两focus=false、输入桌面Default。完整包359文件、322111609字节库存一致；原inspector和VerifyOnly均实际退出0，包身份SHA为8f407c58695a177d016b1c65fbd7719ca4e0b3db6477ca0ab33181c7dc268480。首次PS5检查因UTF8中文目录解码失败而非包变动；仅调用进程指定UTF8后原脚本通过，原失败保留。证据为RootReview12601/root-build26-completed01.json及windows26-postbuild01.stdout。

六组网络已在隔离桌面串行运行，原native60225尚未终态。首组2人0ms/0%真实失败：最后drain阶段命令538/538、总格9033保持，但约1905不稳定列和千余下落格未能排空，未执行后续恢复检查；普通重力原因正在只读定位。该失败不属于用户后移的高塔/级联极限场景，不能取消或记Passed。

当前功能184/184闭合，仍剩五类验收；正式包通过不代表玩法、网络、性能或Goal通过。仅执行批准四场性能，高塔/两万格级联整组仍为user-deferred-not-accepted。继续禁鼠标捕获、不激活窗口、不切用户桌面，不提交或发布。以下为历史快照。

## 2026-10-15 184条完整回归闭合；Windows26正式构建已接受排队

Root已亲见正式构建入口原native78270退出0（chunk092a89），原CLI返回Queued，原Editor35536随后实查存活；这只是排队成功，不是包构建通过。正式build-record/包身份、六组网络、四场性能及自然180秒比赛仍待实际结果，Goal保持active、剩五大类。当前范围仍为GoalScopeDeferTowerCascade20261015.md，高塔与级联整组后移。

构建使用原prepare/recorder/C#builder/inspector。原actual-core12601完整收据不变；Reports-only构建证据格式由normalize-core-build-evidence26.py适配为actual-core12601.build-input.json：保留全部原字段值/结果/门槛，8665报告/文档引用保留并增加原Core精确引用，22个Assets引用原样保留到compiledSourceEvidence且逐条满足实际源码图与当前SHA。所有原证据顺序可重建，全量694源码/694meta仍由原构建入口校验。无修改任何游戏源码或测试，无新增测试通过结论。input-binding26.json实际SHAfe1d3d2d045cb319671db02d7bf3e91b1a15914328817ad34a9586b82d49988b，Root记录RootReview12601/root-build26-queued01.json。

以下历史原文及失败保持原归属。继续隔离桌面、禁系统鼠标捕获、不激活窗口、不切用户输入桌面，测试/构建/Player串行，不提交或发布。

## 2026-10-15 当前源码19完整类184条全部核验通过；实际Core已生成，Windows26准备中

当前有效范围继续为GoalScopeDeferTowerCascade20261015.md：本次剩五类验收，高塔/两万格级联整组后移且不记Passed；普通重力、守恒、付款、碰撞及32米玩法顶界保持。

fresh130六完整类26/26实际通过，Root亲见原native81901退出0（chunk b4e338），原recorder child0、runner0，finishedUtc09:52:52.831638。Root与独立审计核验六份原始/归档XML、完整唯一成员、694源码/694meta、三DLL、10文件及19逻辑/17物理偏好精确恢复；独立postRead于UTC09:53:48.048101Z确认原Editor35536、三身份、六idle、两focus=false及Default。没有额外补造业务marker或断言遥测。

最终126图19完整类184/184已核验；原128仍107/106/1且native1/child3/runner3，105只来自已签收的七完整类，失败整类由同源码131完整2项替换。实际Core为SupportCoreDynamicPreparation01/Partial128ReuseCandidate01/actual-core12601.json，SHA032d0ac7d37a80f109cf29194219f45b303a02e2a1a02d67e45aa187cd29d633；其352关联项明确包含184当前实跑及168历史有限复用，不能称352当前实跑。

原Core首次生成因历史10/12范围文档顶部导航改变SHA而失败；已保存导航版并恢复历史固定原字节，10/15用户新范围继续独立生效，无修改生成器/历史收据/验收条件。实际Core后续成功生成。Windows26准备入口又暴露Reports-only证据接口不接受精确编译图内Assets引用，正在保持所有源证据和原收据的兼容处理；正式包尚未构建。证据见StatusRefresh20261015/ScopeHistoryRestore01及RootReview12601的真实终态/完成映射；独立130审计为SupportExtra26RegressionPreparation01/actual130-terminal-audit01.json。

功能回归通过尚不关闭功能与完整比赛这一大类；新包六组联机、弱网纠正、四场1080p性能、自然180秒比赛与最终交付报告仍待通过。Goal保持active，不提交或发布；隔离桌面、禁系统鼠标捕获、不激活窗口、不切用户桌面继续执行。以下历史原文保留。

## 2026-10-15 已按用户后移高塔/级联整组；当前158/184，最后26项运行中

本次Goal剩五类，按GoalScopeDeferTowerCascade20261015.md执行；32米高塔和两万格级联极限压力及其碰撞/性能整组不再阻挡交付。普通重力、守恒、真实碰撞与32米玩法顶界保留。

fresh129完整BlockDependentEditTerrainTests已实际11/11 Passed，XML20261015-090947，运行813.6689秒；Root亲见原native85871退出0（chunkdfa89c），原recorder child0/runner0、finishedUTC09:10:41.401456。Root核对完整类/原XML归档/694源与694meta/三DLL/10文件及19逻辑17物理偏好恢复，独立postRead于UTC09:12:26.8268163Z确认原Editor35536、六idle、两focus=false、输入桌面Default。原首次bootstrap失败与旧网络性能结果保持。

当前最终126图已核验158/184条，余extra26六完整类26条。Root已串行实际启动fresh130，native81901，本段记录时原句柄仍live，首类BlockGravityBarrierReleaseTests运行中；未计未来Passed。Core/新Windows26包/六组网络/四场性能/自然180秒比赛仍待完成。禁系统鼠标捕获、不激活窗口、不切用户桌面，无提交或发布。

证据：BuildQueryCacheApplication01/RootReview12601/root-terminal12901.json；fresh129原XML/归档及恢复；ShallowReplayApplication01/support129-postrun01.stdout；fresh130真实preflight、source-before、loaded-observation和runner。以下历史原字节保留。


## 2026-10-15 用户将高塔/级联整组后移；剩余五类验收

用户针对详细剩余列表第3组截图明确要求“截图内整体可以先不做”。32米高塔、两万格级联下落、两场对应的极限最终碰撞核验及压力性能记录整体后移，不再阻挡本次Goal。GoalScopeDeferTowerCascade20261015.md记录本次修订，GoalScopeFirstPlayable20261012.md顶部已指向最新生效范围；原实现、历史结果与原标准正文保留，未测不记Passed。

剩余五类为：功能稳定性与完整比赛；常规/交火/零散/八爆性能；弱网移动纠正；六组联机与恢复；最终Windows包与报告。性能正式执行集合由六场改为steady250k180s、combat250k300s、blast8 120s、scattered100k180s四场，原规模、八独立Player、真实1080p及首版阈值保持。普通重力、守恒、付款、真实碰撞、联机恢复和32米玩法顶界继续保留。

当前功能147/184已核验，剩37中的dependent11/fresh129仍在原native85871运行，extra26/fresh130待串行启动；这部分普通功能验证不被本次极限压力后移取消。Windows26包及正式网络/四场性能/自然180秒比赛仍待实际通过。测试隔离桌面、禁系统鼠标捕获、不激活窗口、不切用户桌面，无提交或发布。以下历史范围和结果原字节保留。


## 2026-10-15 原失败完整类两项同源码复测通过；当前147/184，剩余37项中的11项运行中

fresh131完整BlockBuildRejectionAccumulationTests两项已实际2/2 Passed，XML20261015-085353。Root亲见native8123真实退出0（chunk982260），原recorder child0、runner0，finishedUtc08:54:33.769625+00:00。Root及独立file-only审计核对原始与归档XML、完整源成员、694源码/694meta、三DLL、10保护文件及19逻辑/17物理偏好恢复；后置独立Read于UTC08:55:37.7067869Z确认原Editor35536、原三MVID、六idle、两focus=false、输入桌面Default。

源码、原45秒bootstrap准入和业务断言均未修改。两叶含字体预热完成与LAN Ready日志；没有业务逐值成功marker，不能额外声称独立记录了每个retry/payment/collider数值。此次实际完整通过支持同源码未削弱断言通过，但不证明原128首次45秒等待的具体根因，原128的107/106/1及native1/child3/runner3全部保留。

连同126完整10、127完整30、128已签收七完整类105，当前最终126图已有147/184条完整类验证；剩37为dependent11和extra26。Root在恢复后已串行启动fresh129完整BlockDependentEditTerrainTests11项，原生session85871，本段写入时尚无终态；extra26/fresh130未启动。后续必须观察原句柄完成、精确恢复及独立Read，不因观察等待而重启。

最终Core、新Windows26正式包、六组网络与恢复、1080p六场性能及自然180秒比赛仍待完成，六类Goal验收未关闭。Windows26后继输入组装器已仅作源审和纯读取兼容核对，尚未组装未来输入、生成Core或执行构建。外部真实多机与人工观感不计Goal。隔离桌面、禁系统鼠标捕获、不激活窗口、不切用户桌面，无提交或发布。

证据：BuildQueryCacheApplication01/RootReview12601/root-terminal13101.json；SupportRelatedRegressionPreparation01/Independent131Audit01/actual131-terminal-audit01.json；fresh131原XML/归档及恢复；ShallowReplayApplication01/support131-postrun01.stdout。以下历史原字节保留。


## 2026-10-15 原107项结束106通过1初始化失败；完整105项已签收，原完整两项隔离诊断已启动

fresh128八个完整类107项已实际终态，原XML为106 Passed/1 Failed。Root已亲见原native43944外层退出1，原recorder child退出3、preservation runner退出3，三个层级分别保存，不要求相等。原失败在BlockBuildRejectionAccumulationTests的45秒真实bootstrap Setup，尚未进入该条积木/扣费正文；随后Teardown配置Reset与仍在进行的初始化产生NullReferenceException。首次45秒究竟卡在哪个加载阶段仍未证实，没有改45秒、Ready/Error或业务断言，没有改Assets。

Root独立验证全部130引用SHA、694源码和694meta当前图、原八份XML及归档、完整唯一成员和原结果后，签收后七个完整通过类105项；原失败类完整2条及其中Passed子叶全部排除。加上fresh126完整10项、fresh127完整30项，当前最终图已有145/184项完整类结果通过验证。余39项为原失败完整2项、dependent11及extra26。partial128.root-reviewed01.json记录此次具体提取，原128整体仍失败，不改判为成功，不生成伪零退出run。

Root随后串行启动fresh131同源码完整BlockBuildRejectionAccumulationTests两项诊断，原生session8123；本段写入时该句柄实查仍live、runner显示RUNNING，尚无终态或通过结果。编译绑定仍为actual126，原源码/45秒/断言与有限范围保持。dependent11/fresh129和extra26/fresh130尚未运行。必须等131真实终态和恢复后独立Read，再开始下一段。

六类首版验收仍未关闭：功能与完整比赛；常规/交火/零散/八爆性能；高塔/级联；弱网移动纠正；六组联机与恢复；最终Windows包及报告。Windows26构建/性能/网络/smoke入口已经准备与源审，实际新包及其正式结果尚无。旧Windows24六组0/6和Windows25双人drain失败保持原归属。真实多机/人工观感不计Goal。测试继续隔离桌面、禁系统鼠标捕获、不激活窗口、不切用户桌面，Goal保持active，无提交或发布。

证据：BuildQueryCacheApplication01/RootReview12601/root-terminal12801.json、partial128.root-reviewed01.json；SupportRelatedRegressionPreparation01/Independent128Audit01/actual128-failed-terminal-audit01.json；fresh-fixtures-128原完整结果和恢复、support128-postrun01独立Read；fresh-fixtures-131真实preflight/source/loaded-observation/runner。以下历史原文完整保留。


## 2026-10-15 当前126编译下完整恢复30项全部通过；战斗107项已启动

fresh127的HistoricalRecovery14、AuthorityLateFloor9、GravityBindingFutureMotion7三个完整类已实际30/30 Passed。Root亲见原生session44225退出0，原recorder.finishedUtc为2026-10-15T07:59:10.320475+00:00；三份原始XML分别为20261015-074454、075248、075841，完整唯一叶与原源码展开成员一致。与此前fresh126完整10/10合计，当前同一最终图已确认实际40/184项；不将历史168项记作本次实跑。

本批已restored=true、pendingRestoration=false、runnerExitCode=0；694source与694meta前后等于compile126，19逻辑偏好/17物理值的API与raw恢复精确，10保护文件及完整清单相等。Root与独立审计均核验原结果及恢复；后置独立support127-postrun01 Read于UTC07:59:52.2272477Z确认原Editor35536、三身份exact126、六idle、两focus=false、inputDesktop=Default。

Root随后串行实际启动fresh128 combat107，原生session43944处于运行中，本段记录时未取得整批终态。该批包含建造拒绝与额度、爆破付款/生命周期、BuildQuery72、地形发布与碰撞八个完整类；之后仍需dependent11及extra26。没有以准备文件、旧153项、Passed计数或有限历史复用代替最终图19类184项完整执行。

完整Windows25至126生产差异及历史8类168项有限复用范围已由Root独立读核批准，见RootReview12601两份root-reviewed JSON；该审批是范围决定，不是Core通过收据。正式Windows26候选、六组网络/恢复、1080p性能、高塔/级联及自然180秒完整比赛仍待实际通过。Goal保持active，剩六类首版验收；真实多机与人工观感两项不计Goal。

证据：BuildQueryCacheApplication01/RootReview12601/root-terminal12701.json；SupportRelatedRegressionPreparation01/Independent127Audit01/actual127-terminal-audit01.json与actual127-postread-audit01.json；fresh-fixtures-127原结果、source前后图和preservation；ShallowReplayApplication01/support127-postrun01.stdout及原command。原先126及所有历史失败正文保留。继续隔离桌面、禁系统鼠标捕获、不激活窗口、不切用户桌面，无提交或发布。


## 2026-10-15 支撑与恢复修复后原完整十项真实通过；关联恢复30项已启动

fresh126 原完整 BlockBuildShallowColumnReplayCandidateTests 为 10/10 Passed，XML20261015-073243。Root亲见原生session19866退出0，UTC07:33:03.868510；随后独立support126-postrun01 Read确认原隔离Editor35536空闲、未聚焦、输入桌面Default，加载Runtime cb8a1bd7-380e-4fcb-aba7-b490405c9b09、Tests 3dc3af61-e685-4a64-8f89-95faf2e8f23a和原Config。不能将此完整十项结果扩称为全部功能或整库通过。

实际生产修复保留了首版规则和原断言：分裂前先完成正常支撑绑定及其连续性发布；已有真实无效几何的恢复事务中，使用有界持久游标寻找与普通流程相同的最近源前驱，将安全位置与合法实时支撑一并发布，并在真实发布时间重验下层位置与cap。原own landing、严格wire、碰撞、历史无容量及重试保护均保留，未以调时间或调度顺序绕过问题。

十项均有完整正文见证：旧八项具有3条受控回放、3条缓存学习和3条边界完成记录；两项新增场景各有绑定和最终落定记录。无额外下层场景44帧、带合法下层场景53帧，最终pending/falling/unstable均0，存活4/5格及原生Collider格数相等，严格codec和已发布源保护成立。先前125的9/1及654次反复恢复、124与120等原始失败均保留原归属，不改判历史失败。

本轮restored=true、pendingRestoration=false、runnerExitCode=0；19逻辑偏好/17物理值API及raw精确恢复，10保护文件精确恢复，694源码与694meta前后图完全相等。独立审计与Root核验均确认完整body contract，不使用缺正文的Passed叶子代替执行。

已在同一实际126编译绑定下串行启动fresh127恢复关联30项（HistoricalRecovery14、AuthorityLateFloor9、GravityBindingFutureMotion7），Root native44225；本段写入时尚无整批终态。最终图仍需完成19个完整类/184项的实际关联回归，历史8类/168项只可经完整生产差异影响审查有限复用，不记为最终图实跑。新正式Windows包、六组联机及恢复、1080p性能和完整180秒比赛仍待通过，Goal保持active、剩六类验收；外部多机及人工观感两项不计Goal。

证据：StatusRefresh20261015/Support126Passed01/root-terminal12601.json及其逐文件SHA；UnityTests/20261015-073243.xml；SupportLinkApplication01/compile-binding12601.json；fresh-fixtures-126的body-witnesses、source前后图与preservation；ShallowReplayApplication01/support126-postrun01.stdout。测试继续保持隔离桌面、禁系统鼠标捕获、不激活窗口、不切用户桌面，无提交或发布。以下历史原文保留。

## 2026-10-15 关联153项已通过；新增缓存专项通过，完整八项暴露支撑链接异常

同一生产修复下，fresh114完整5项、fresh115完整30项、fresh116完整107项、fresh117完整11项均已实际通过，合计153项；Root已分别观察原生终态0并核对完整XML及精确恢复。117原生27107实际退出0，11项持续建拆完整回归通过。此计数属于当时完整源码图及Tests身份，不自动迁移为后续源码图全量通过。

随后仅追加三项缓存失效测试，原五项源码字节保持，生产Runtime和Config DLL字节不变；真实编译零错，Tests实际换为defa6d56-963e-4967-a1e6-ad07624da290。fresh118完整八项原生13810实际退出1，XML20261015-060849为7Passed/1Failed，所有旧结果保留。新增三项均有自己的学习和完成记录：queryWork413、真实overlapAborts1、callbacks1/1/1、最终pink3/blue1，pending/falling/unstable均0；同World mutation在发布前实际使旧缓存失效。

完整八项未通过：录制build-first在普通Tick内调用TryBindLowerSupport，再到BlockWorld.SetCohortSupport时抛出Invalid cohort support link；不是快照解码或安装失败。紧接着recorded destroy-first虽XML为Passed，却缺少其正文无条件末尾记录，关键闭环未证实。不能将整批视为通过，也未把6个审计失败检查解释为6个新缺陷。测试框架跨用例恢复目前只是分析方向，不代替已证实的生产支撑异常。

118已精确恢复19逻辑偏好、17物理注册表值和10保护文件；运行后独立Read确认原隔离Editor35536 idle/unfocused、输入桌面Default、同生产Runtime。后续先用公开接口构造最小合法复现，修正支撑候选筛选后复测；正式新Windows包、六组联机、性能及180秒完整比赛仍待通过。Goal保持active，剩六类验收，外部多机与人工观感不计Goal。

证据：BuildQueryCacheApplication01/CorePreparation01/root-native114to117-ledger01.json；related115/116/117-terminal-audit01.json；SourceInvalidationTestsApplication01/compile-binding11801.json及sourcecache118-fresh-fixtures-118-run01.command.json；Independent118Audit01/；UnityTests/20261015-060849.xml；ShallowReplayApplication01/sourcecache118-postrun01.stdout。原文完整备份与插入核验见StatusRefresh20261015/SourceCache11801/。


## 2026-10-15 浅列反复拒建已复现并修复，原完整五项回归真实5/5通过

生产查询缓存修复已应用于BlockTerrain.BuildOccupancy.cs、BlockTerrain.GravityCohorts.cs及BlockTerrain.cs。原命令查询仅保留最新一批已完成列，模拟时间推进时会忘记另一列，导致两列交替拒绝并使后续命令无法完成。新实现按完整已发布源绑定保留已完成列的纯查询数据，随时间重新计算位置；成功发布、源变更、重置及销毁释放缓存。私人草稿、实际付款、碰撞提交和首版验收标准未放宽。

修复前fresh113实际有两个带完整诊断的有限命令失败见证；旧XML全部保留。其中recorded-build-first的Passed缺少正文诊断，另一个Two用例在初始安装断言失败，两者与Unity Test Framework跨case协程恢复路径高度吻合，但未记录当时PC，不能声称已动态证实框架根因，也不能将缺诊断Passed计作覆盖。

同一次生产编译已实际零错完成。观察期间一次CLI连接被服务端重置，Root只续查原编译、未重新提交。独立postRead确认原隔离Editor35536载入Runtime 1afee0d0-96d1-4aaf-bbd8-d91f3ae49994、Tests 736ebb29-c862-4864-bd12-6d8bc989f6b1，六idle、两focus=false，用户输入桌面Default。

fresh114原完整五项已真实5/5 Passed，Root原生session2240实际退出0，runnerExit=0、restored=true、pendingRestoration=false。三个受控回放均恰好有一条实际正文诊断：synthetic在54帧内callbacks1/1；recorded build-first179帧、destroy-first188帧，均callbacks1/1/1；三者最终pending、unstable、falling全部为0。原守恒、付款、FIFO、严格wire及实际碰撞尾段均执行通过。独立36项核验全部成立；694源及694meta保持，19逻辑偏好/API/raw和10受保护文件精确恢复。此结果证明受控场景的修复效果，不代表独立Player联机与性能已通过。

此前Windows25原raw完整读回也已完成：StreamingCandidate02实际扫描8528通知、8528唯一有效帧、8528精确四long关联，无缺失/截断/回调错误；最早13处依赖等待现已覆盖。21选中帧完整树96526样本支持所选区间FixedUpdate与BlockTerrain更新仍执行，不能由此推出特定私有调度分支或后续drain根因。旧2000帧窗口不足的结果保留，不能代替本次完整读回；本次读回也不是正式性能验收。

后续仍需当前生产修复关联的恢复/付款/碰撞等完整类，以及已学缓存后的源失效专项；再正式构建包含修复的Windows候选并执行六组网络、1080p压力和180秒比赛。Goal仍active，仍剩六类验收；外部多机与人工观感不计Goal。隔离桌面、禁系统鼠标捕获、不聚焦、不切用户桌面，无提交或发布。

证据：Windows24Preparation01/BuildQueryCacheApplication01/root-application01.json、compile-binding01.json、candidate114.command.json、Independent114Audit01/；UnityTests/20261015-053235.xml；RegressionTriage/LatestSourceFullRegression15/Runs/fresh-fixtures-114/；NetworkRuntimeCpuExtractionPreparation01/HistoryCapacityPreparation01/StreamingCandidate02/Client1StreamingTreeAnalysis01/。原文备份及更新哈希见StatusRefresh20261015/BuildQueryCache11401/。


## 2026-10-15 Windows25诊断候选已构建，新增31项回归通过；双人诊断与原生读回暴露具体后续方向

当前实际源码已在原隔离Editor 35536编译零错；三份报告相关完整类31项全部真实通过，10文件及19逻辑偏好精确恢复。正式Addressables和Windows25候选已构建并完成完整包身份核验（359文件、8份aa资源）；它是默认关闭、显式开启CPU观察的新诊断候选，不是最终验收包。生产玩法和已批准首版门槛均未改变。

该包双人0/0运行已实际终态：Root native53864外层退出1、native/bootstrap/guard均4，两名owned Player自然退出0，偏好精确恢复。本次仍在drain超时，冻结/重连/清空/重建尚未到达；原失败保留。新诊断观察到主机369次BuildMovingOverlap事务撤回，地形版本曾在一秒采样中约36秒不推进；这是下落结构与新增格重叠分类，不是玩家胶囊拒建分类，尚未证明私有调度根因。

两端drain边界公开snapshot字节完全相同，round1/rev148、26189字节，静态2900加下落60共2960格，双方计数1471/1489。20个Falling结构已到落点且速度为零；41个Detaching结构的178格仍留在静态中。快照只有公开wire状态，不含私有队列、原始checkpoint、落点证明或PhysX；这些事实用于准备受控复现，不能当作恢复原现场或修复完成。

Root native25229离线读取已真实退出0，Editor前后idle/unfocused、输入桌面Default、原raw及输入未改变；但完整四long关联失败：原生加载收到8528帧通知，可查询历史仅保留最后2000帧，恰好关联2000个payload，前6528个缺失。最早依赖等待区间未覆盖，不能写成原生全生命周期通过。原ScanJob01/ScanAnalysis01保留，后续优先在加载通知时读取同一raw，不重新运行Player，也不改正式统计。

Goal继续active，仍剩六类首版验收：功能稳定性、常规/交火/零散性能、高塔/级联、弱网纠正、六组联机与恢复、最终包与报告。外部真实多机与人工观感两项不计Goal。保持隔离桌面、禁止系统鼠标捕获、不激活窗口、不切用户桌面；没有提交或发布。

证据：Windows24Preparation01/NetworkExactCpuApplication01/；NetworkExactCpuPlayerPreparation01/Windows25BuildCandidate01/、NetworkP2RuntimeCpuRun01/、NetworkP2RuntimeCpuStateReadonly01/；NetworkRuntimeCpuExtractionPreparation01/Client1RuntimeBinding01/。Root实核与原文备份见StatusRefresh20261015/Windows25RuntimeCpu01/。以下全部历史原文保留。



## 2026-10-15 Windows24六组联机已终态，四个实际问题方向仍待修复

Windows24首版六组联机已有真实终态，覆盖6/6、通过0/6，取代下方“仍在运行”的历史快照。当前直接观察到的问题应分为以下四个定位方向；现有证据没有证明它们来自同一根因。

| 方向 | 本轮实际结果 |
|---|---|
| 地形命令队列排空 | 双人0/0、双人100ms/1%、八人0/0及八人100ms/1%四组，在约45秒drain等待期后仍有待处理命令／不稳定列。部分归档明确显示等待期间仍有命令完成，不能直接写成永久死锁。 |
| 残留下落未静止 | 双人200ms/3%已完成534/534条命令、pending=0，但最后仍有12个下落格和4个不稳定列；尚未证明积木丢失、永久不落地或具体根因。 |
| 八人弱网纠正长尾 | 八人100ms/1%的七个客户端P95均达标，但原始P99为0.857359～3.683536米，全部超过首版0.60米门槛，属于已经实际复现的数字门槛失败。 |
| 八人较差网络下连接超时 | 八人200ms/3%的client-2约20秒仍在Joining，随后记录IntentionalShutdown／TransportShutdown并以exit=3退出；是明确的连接超时，现有证据不支持称为native崩溃。 |

前五组每个参与者的实际drain输入均已被权威ACK消费；原case通用“缺少ACK”提示来自尚未执行的fall fixture默认空字段，不能另列为ACK丢失故障。六组均未运行到fall／finish／frozen／reconnect／clear／restart／rebuilt：下落专项、结束冻结同步、重连、回合清空与重建仍是缺测，不能写成这些阶段已经运行失败。前五组已有同版本数量、阵营和四类哈希一致的共同样本，但这不等于排空后的最终同步通过。

功能回归方面，本次修复关联五个完整类的44个方法均已有真实通过记录：FutureMotion 7、HistoricalRecovery 14、OwnLanding 3、DependentEdit 11、AuthorityLateFloor 9，跨不同实际Tests身份执行，并非全部当前套件或同轮44/44。候选构建门另有限复用15个历史完整类182项，合计20类226条关联证据；不能写成当前最终源码226/226实跑。三种历史偶发风险——权威切换未终态、应有125格实124格、移动付费建造支撑上限未推进——对应方法虽已回归通过，原现场根因仍未逐一直接证明；既有失败证据保持。

同包八Player／1080p的20秒CPU诊断及raw离线分析均已终态，仅用于定位。窗口内命令入队约80.98条/秒、完成54.94条/秒、积压增加521；普通攻击端到端P95约10.83秒与已创建事务提交P95约34.16毫秒属于不同样本边界，不能相减作精确排队时间。raw只展开35个选中完整树，35个Main区间归因有效、17个worker组保留区间异常，Profiler写盘亦有明显观察开销，不能代替正式性能验收或证明网络drain根因。后续网络startup raw覆盖不足，其实际scan仍待完成；不得写成已覆盖全生命周期。

Windows24仍是候选包，尚非最终验收包。Goal保持active，继续按已批准首版范围执行；当前包正式1080p性能、完整180秒比赛smoke以及尚未到达的联机恢复流程仍待通过。外部真实多机与人工操作观感两项不计入Goal。本次仅更新Reports状态，不运行Unity／native／构建／Player，不修改Assets，也不调整任何首版门槛。

证据：`Windows24Preparation01/NetworkMatrixReadonlyReview01.md`、`IndependentHistorical15ReuseReview01.md`、`CpuQueueStageAnalysis01/findings.md`及`CpuHotspotAnalysis01/findings.md`。两份原文已逐字节备份到`StatusRefresh20261015/Windows24TerminalAndCpu01/Before/`，before／after SHA与插入位置核验见同目录`update-manifest.json`。以下全部历史正文保持原字节。


## 2026-10-15 修复后的Windows24候选已构建并核验，六组联机已启动

Windows24当前源码候选已真实构建成功：UTC03:02:55.1777212Z至03:06:07.5309474Z，正式Addressables构建成功；实际包为Builds/Blocks-20261015-acceptance24/InkLan.exe。V2包检查已通过：完整359文件与8个aa资源文件，包身份SHA38e61f4ce921e5d17789f54d6d86d156c63aeb0273ccccdee9cb544ac74dfd3a，实际Player Runtime MVID为0e8c20ba-593d-47f0-8e14-35946c941d92。它是包含本次下落接触精度修复的新候选，仍不是最终全部验收通过包。Root实际queue native96321及build observer2591均退出0，build-record实际candidateBuilt=true，不能仅以queue成功推断构建。独立postBuild Read确认原隔离Editor35536、当前三MVID、六idle、两focus=false、用户输入桌面Default。

本次core构建门已实际校验生成：当前修复关联五完整类44项均通过，另有限复用15个旧完整类182项，共20类226条关联证据；226不是全部当前源码实跑。原7996条证据、fresh108/110的内部见证失败与三种旧偶发根因未证的限制保持；goalAcceptancePassed=false。V2包检查只补与builder/recorder一致的固定玩法文档路径和SHA例外，V1/config03/core/prerequisites均保全原字节，新identity记录实际V2和V1继承链。

UTC03:08:04.204136Z，Root实际启动同机六组双/八Player、0/0、100ms/1%、200ms/3%联机，native session67618、launcher3976、bootstrap41308仍live；此处是已启动快照，未取得整批终态与偏好恢复，不计任何一组通过。全矩阵35秒真实移动、原timeout/一致性/恢复/原始纠正分位要求保留。网络完成及恢复后才可串行运行当前包1080p六场性能与180秒比赛smoke。测试继续隔离桌面、禁系统鼠标捕获、不聚焦、不切用户桌面；Goal保持active，外部多机与人工观感不计Goal。

证据：Windows24Preparation01/root-candidate-package24-01.json（SHA27ec1e5a470dd75754087a23362c98ac10465d4e4ebc8b49dcb95b2a4100406e）、build-record.json、package-identity.json、formal-build24-postread01.stdout、core-test-receipt.json（SHA05a58ed5dc8bc069667a5fa547363a42bffea1bd1d8424670df1a42fd8a0bdc5）、build-prerequisites.json（SHAb1c8e21335ce1005843bc53b03b73d8356b37478e47bfb728c2e79ab8508d732），以及IsolatedAcceptance/PrototypeV1/Network/_desktop-launch/。以下历史时点完整保留。


## 2026-10-15 关联五类44项均已有通过记录，新增完整付费场景已通过

fresh-fixtures-112 的完整 BlockAuthorityLateFloorTests 已真实终态 9/9 Passed，95.374752 秒；Root 实际 native session 33295 退出0，runnerExitCode=0、restored=true、pendingRestoration=false。新增 PaidFloorAtRoundedContactPreservesWirePaymentsAndFinalColliders 已完整执行严格 snapshot／累计与连续 delta、原始源不可变、有限建造上限、精确发布位置、只付款一次、阵营实格守恒、全部下落完成及原生 Collider 断言。输出明确 same-pending overlap NOT OBSERVED；本次只通过实际完整功能结果，不宣称已运行覆盖特定双 writer 内部交叠。fresh108／110 的旧失败与 XML 保持原样。

同一生产 Runtime 6e5eb0ff-1683-461d-93d7-fd0df9af276f 的修复关联五个完整类已有44个方法通过：fresh107 FutureMotion 7，fresh109 HistoricalRecovery 14 与 OwnLanding 3，fresh111 DependentEdit 11，fresh112 AuthorityLateFloor 9。它们跨不同实际 Tests 身份运行，不能写成同轮或全部在最终 Tests MVID 下执行44/44；最终03 Tests身份为3a9d6caa-8f64-4de9-a2f4-9266af512b1c。修复后最后一个新增付费场景现已通过，不再列为尚未执行。

历史三种偶发风险仍保留：权威切换时未到终态、持续建拆／下落时125格实124格、移动付费建造的支撑上限未推进。它们的原方法在fresh111完整11/11中均通过，但这些旧现场的根因尚未被直接证据逐一证明，不能宣称已完全解释或保证不再复现。当前功能结果汇总与包含此修复的新Windows Player验证仍待收口；旧Windows23包早于本次生产修复，其六组0/6联机、弱网纠正和性能结果不得转为新源码通过。当前源码新包、六组联机／恢复、1080p性能和180秒比赛smoke仍未通过，Goal保持active，外部多机与人工观感不计Goal。

Root 独立核对完整XML及归档字节、九个唯一方法及源码成员、1384项source/meta、10个现场文件和19逻辑／17物理偏好精确恢复。fresh112-postrun-read01再次确认原隔离Editor35536、当前三MVID、六idle、Editor/GameView两focus=false、用户输入桌面Default。测试禁用系统鼠标捕获，不激活窗口、不切换用户桌面。证据：Windows24Preparation01/root-native-terminal112.json（SHA256 7e9bc2b95bd9304880d4d69b7e460082fd7332b2b3752d00e0ca644b4733ee0a）、UnityTests/20261015-024918.xml、fresh-fixtures-112/preservation.json，以及PublicationContactRoundingRepair01/current-overlap-compile03.json与fresh112-postrun-read01.stdout。以下历史时点完整保留。


## 2026-10-15 下落绑定完整七项实际 7/7 Passed，整体稳定性尚未关闭

fresh-fixtures-107 的完整 BlockGravityBindingFutureMotionTests 已真实终态：原五项及新增正／反序舍入回归共七方法全部 Passed，完整 XML 七个唯一执行叶与归档一致，整类运行 69.806 秒。Root 实际观察 native session 10407 退出 0；manifest 的 runnerExitCode=0、restored=true、pendingRestoration=false。独立只读复核 37 项通过，涵盖 XML／终态关联、10 文件、19 逻辑偏好／17 原始注册表值及 1384 项 source/meta 一致性；该 37 项复核仅保留于本会话工具输出，未单独落盘为审计文件。

Root 独立 fresh107-postrun-read01 再确认同一隔离 Editor PID 35536、当前三 MVID、六 idle、两 focus=false，输入桌面 Default。此次只关闭这一完整七方法回归；后续仍有本次修复关联的 4 个完整类、共 36 项待运行，另 1 项新增 overlap 候选尚未应用／运行。包含修复的新 Windows 包、六组联机和性能验收仍待完成，不能称整体功能稳定性通过，Goal 保持未完成。

证据：UnityTests/20261015-021139.xml；RegressionTriage/LatestSourceFullRegression15/Runs/fresh-fixtures-107/preservation.json；Windows23/MinimalReusePreparation01/PublicationContactRoundingRepair01/fresh107-postrun-read01.stdout。本次完整旧字节备份及应用记录见 StatusRefresh20261015/Fresh107Passed01/。以下历史原文完整保留。


## 2026-10-15 已实际编译零错，新隔离实例的完整七项回归运行中，尚未计通过

下落发布精度修复已真实载入新的隔离 Editor PID 35536：D3D11、桌面 CodexBlockTests20261013a，用户输入桌面仍为 Default；启动记录的系统鼠标抑制环境变量为 1。启动日志实际出现 Tundra build success，独立 Read 的 Runtime MVID 为 6e5eb0ff-1683-461d-93d7-fd0df9af276f、Tests 为 33ec4b45-4222-49ad-904c-35c54b5a0f81，Config 保持旧值，磁盘 DLL 与 loaded 身份一致。后续真实 recompile/status 返回 up_to_date、failed=false、errors=[]、compilationFailed=false；真实编译绑定同时保留启动日志的冻结字节及 SHA，故“已编译零错”有实际证据，不来自准备文件。源码仍为两份生产文件及一份测试文件的变更，meta 无改动。

fresh-fixtures-107 已实际提交完整 BlockGravityBindingFutureMotionTests 类（源码七方法，原五项加两项正／反序舍入回归）。真实 run_tests 响应为 running，Root 已收到 native session 10407 并在派发本次更新时确认仍 live；此处是运行中快照，尚未取得实际终态、完整 XML 或退出码，七项目前均不计通过。首次冷启动入口真实 test_status=no_tests 与 IsolatedDesktopProbe.Verify 都已通过，确认同一实际 launch 的 PID／三 MVID、六 idle、两 focus=false 及隔离条件，前后 STATUS／REQUEST 都不存在；没有伪造 status、completed 或测试结果。后续原测试、1200 秒观察、终态 XML 与保全恢复流程保留。

fresh-fixtures-106 的原外层／runner 退出 1 保留：该次在真正测试提交之前，原观察器因新 Editor 尚无 Temp/pipeline_test_status.json 而拒绝，没有 start.argv、XML 或请求提交。这属于首次工具入口问题，不改判为七项测试失败或生产修复失败。Root 随后 --restore-from 实际退出 0；保存 manifest 已 restored=true、pendingRestoration=false，10 个文件全数恢复，19 个逻辑偏好／17 个原始注册表值均精确恢复。

功能稳定性、修复后的同源新 Windows 包、六组联机与性能验收仍未关闭；旧 Windows23 的 0/6 联机失败和弱网纠正失败原样保留，不能用本次已编译或正在运行替代通过证据。外部多机与人工观感继续不计入 Goal。Root 将在真实终态及恢复证据到齐后签收结果。

证据：Windows23/MinimalReusePreparation01/PublicationContactRoundingRepair01/current-rounding-compile01.json（SHA256 8d5849a7af209436f6dc04e87607eb81422c457bdf95bfbac40a9c173da289b0）及同名 startup-editor.log；同目录 IsolatedEditorRecoveryPreparation01/Runs/contact-rounding-editor-recovery02/launch.json、contact-rounding-recompile-status01.stdout、contact-rounding-postcompile-read01.stdout；RegressionTriage/LatestSourceFullRegression15/Runs/fresh-fixtures-107/editor-results.json.raw/000/ 的 cold-start.test-status.stdout、cold-start.desktop-probe.stdout、cold-start.verified.json、start.argv.json、start.stdout；fresh-fixtures-106/preservation.json、runner.stderr 与 Recovery-20261015T020808258747Z/。本次两文件完整旧字节备份及证据 SHA 见 StatusRefresh20261015/CompiledFresh107Running01/。以下历史原文完整保留。


## 2026-10-15 下落发布精度缺陷已复现，修复已写入但尚未编译与执行验证

当前功能稳定性有一项已确认的生产缺陷：合法的上下两组下落积木，在普通 authority Tick 推进、下层落定后，上层发布的 InitialOffset 比 LandingOffset 大 1 ULP（约 5.96×10⁻⁸ 米）。房主原容差允许该值，但严格 snapshot 和 delta 解码器都会拒收该次发布。原合法种子能通过严格解码，前几次发布也通过，故不是人为构造非法输入；诊断已保存首个被拒绝的真实 snapshot/delta 字节。此缺陷已独立复现，但尚未证明 Windows23 六组联机失败全部由它导致。

生产修复已写入 BlockTerrain.GravityBindings.cs 与 BlockTerrain.BuildOccupancy.cs：发布时统一选择符合真实下层位置及自身落定上限的位置，保留原始样本的越界拒绝检查，严格 codec 不放宽。BlockGravityBindingFutureMotionTests 新增正序和反序身份两项回归，原五项保留，待整类七项执行。三个源文件 SHA-256 与独立源审一致，源审无未解决的阻塞发现；当前仅为“已写入、已源审”，尚未编译、尚未执行回归，也未构建包含此修复的新 Windows 包。

此前 fresh-fixtures-105 的完整 BlockDependentEditTerrainTests 最新实跑为 11/11 Passed，原移动付费建造组合项也已通过；fresh101 的旧失败保留为历史，偶发根因仍未证明。该旧 Runtime 的通过结果不能替代本次两文件生产修复后的验证。下一步必须运行完整七项下落绑定回归，并验证建造上限与连续性重叠、真实越界仍被拒绝，以及相关付款、守恒和碰撞不退化。

Windows23 原候选包六组联机最新仍为 0/6：五组在完整队列／弹丸／下落／输入 ACK drain 超时，一组八人零延迟停在迟加入精确哈希。冻结、重连、回合清空及重建尚未执行到，属于缺少验收证据，不能另计为四个已确认缺陷。弱网纠正原始失败保留；需要修复后的同源新包完整复测，才能判断同步及恢复是否达标。

本次只读进程盘点未枚举到原本项目隔离 Editor PID 70668，仅观察到另一项目的 Editor，未触碰其他项目。未启动替代 Editor，未执行编译或测试；需先安全恢复本项目原隔离桌面 Editor，再按串行顺序继续。测试仍须禁用系统鼠标捕获，不聚焦、不切换用户桌面。Goal 保持未完成，外部多机与人工观感仍不计入 Goal。

证据：Windows23/MinimalReusePreparation01/LegalSupportRoundingDiagnostic01/Runs/legal-support-rounding-01-01/legal-support-rounding-result.json；同上 PublicationContactRoundingRepair01/IndependentRuntimeReview01/review.json 与 NewRegressionReview01/static-preservation.json；UnityTests/20261014-114549.xml；Windows23/NextLaneCorrectedFixtureCandidate04/Network52639TerminalAudit01/terminal-audit.json。本次更新及原始字节备份见 StatusRefresh20261015/PublicationRounding01/application.json 与 Before/。进程盘点为本轮 root 只读观察，未单独落盘，不附无关项目命令行。以下历史正文完整保留。


## 2026-10-14 六组联机已终态：0/6通过，19项偏好恢复核验完成（UTC12:17）

Windows23真实候选包的network52639已结束：原工具外层退出1由Root直接观察，保存的native/harness/bootstrap均退出4，native终态UTC12:17:45.274319Z。六组全部完成并与summary一致，0组通过；不再将此批写成正在执行。

双人三组及八人100ms/1%、200ms/3%均在完整drain超时；八人0ms/0%停在迟加入精确哈希。有限落块、冻结、重连、清空、重建没有完成验收。双人100ms/1%实际moving/friendly样本921/574，原raw P95=0.019218米、P99=0.684424米，P99超过首版0.60米；八人同弱网七客户端P95=0.517427—1.462582米、P99=1.437928—4.270965米，且moving84—102/friendly20—97全部覆盖不足。原失败及完整raw统计保留，不过滤大纠正或放宽门槛。

保存的19项逻辑偏好、64/32两视图before/after-test/after-restored均相等；restored/rawEqual/branchExistenceEqual均true，原退出4保留。30个owned Player及六组记录中的proxy/Player均退出0。reconnectCleanup=false仅说明未执行到重连清理，不代表该验收通过。

窗口审计线程已结束、读取错误0；408个采样中未观察到Player在用户输入桌面，但408个采样均存在未分类窗口所有者。这不证明全程无焦点或前台影响，也不构成渲染或性能通过。继续禁止系统鼠标捕获、不聚焦、不切用户桌面。

本次终态审计不关闭弱网、联机恢复、当前同源包性能及最终交付；当前core关联回归通过也不等于包级全部通过。Goal继续，外部多机与人工观感不计Goal。此前frozen progress原文件不修改，以下原运行中记录标为历史时点。

证据：Windows23/NextLaneCorrectedFixtureCandidate04/Network52639TerminalAudit01/terminal-audit.json及README.md（六case、summary、bootstrap/native、19项恢复与窗口原证据均附SHA-256）；Before保存本文修改前的完整原始字节。


## 历史时点：2026-10-14 当前源码候选包已构建及核验（联机当时运行中，现已终态）

Windows23当前源码候选已真实构建成功：UTC11:58:59.4154586Z至12:01:56.6770624Z，正式Addressables资源构建成功；完整359文件及8个aa资源文件核验通过。包身份SHA964b562d0800a6956c02a22baf7acb3eef49cf4f861282f9a022114f8ffda72a，真实Player Runtime MVID d87087c0-afd9-4d78-8a68-bd6e5c140201，与Editor身份分别保留。UTC12:02:44.407453Z独立postBuild Read确认原隔离Editor70668、六idle、两focus=false、用户桌面Default。包路径Builds/Blocks-20261014-acceptance23/InkLan.exe；这仍是候选包，不是最终全部验收通过包。

六组双/八人0/0、100/1、200/3联机已用该真实包串行启动，native52639（launcher97452、bootstrap43020）仍活跃，整批终态及偏好恢复尚未取得。已结束的前两组双人均未通过：攻击命令可全清，但完整落块/同步drain未在原45秒阶段内结束，后续冻结、重连、清空重建未覆盖；100ms/1%组实际移动ACK921，P95=0.019218米，P99=0.684424米，超过首版0.60米。原失败与全量raw样本保留，不放宽时间或过滤大纠正。

新包六场1080p完整压力的实际manifest已生成，绑定当前完整包359文件及当前1384源码/meta；performance与180秒smoke尚未启动，必须等原network52639真实结束及恢复后串行调度。当前core关联回归门禁通过，不等于联机或最终功能稳定性验收。Goal继续，外部多机及人工观感仍不计Goal。禁止系统鼠标捕获、不聚焦、不切用户桌面，无提交或发布。

证据：Windows23/MinimalReusePreparation01/build-record.json、package-identity.json、current-corrected-fixture-build01-postread.stdout及CurrentCandidateBuildAndNetwork01/progress.json；Windows23/NextLaneCorrectedFixtureCandidate04/IsolatedAcceptance/PrototypeV1/Network/summary.json为整批运行中的部分结果，不能视作完整六组终态。以下历史原文保留。


## 2026-10-14 原完整11项回归通过，当前源码Windows候选构建已提交

fresh-fixtures-105 已真实终态：原完整 BlockDependentEditTerrainTests 11/11 Passed，实际session21219外层0、runner子退出0，restored=true、pendingRestoration=false，wrapper/restoration均无错误。独立51项终态核验通过；UTC11:48:13.4249132Z postRead确认原隔离Editor70668、当前三程序集身份、六idle、Editor及GameView无焦点，用户输入桌面Default。完整原方法的Drain、付款/FIFO、块数守恒及wire断言本轮均执行并通过。运行整类1505.133秒；不以XML重载后根容器局部duration替代全程耗时。

当前关联25项的跨完整类通过记录已齐：fresh100的6项保留原测试程序集与历史来源，fresh103/104/105在当前1384源码/meta图实跑4个完整类19项通过；不能写成同轮25/25或当前图201项实跑。此前fresh101的10P/1F及fresh37原125/124历史完整保留；偶发根因仍未证明，不宣称生产根因已修复，缓存生命周期候选未应用。

当前源码core候选构建门禁已实际生成：7996条证据、13个历史完整类182项有限复用加当前4类19项，关联总201项；goalAcceptancePassed=false，网络、性能、Player仍未验收。实际prerequisites首次校验暴露唯一Docs/BlockGameplay.md不在Reports的工具路径限制。新V2 builder/recorder和Prereq03仅允许该文档的精确路径及原SHA；全部7996条原证据保留并实算哈希，旧18个pins保留，新2个pins新增，独立复核通过，不改变玩法、门槛或启动参数。

已向原隔离Editor提交正式Addressables与Windows23候选构建，当前native session84349；排队/CLI结束不等于构建成功。必须等待真实build-record或build-failure，再检查实际包全部文件、资源与程序集身份，才可运行新包六组联机、六场完整1080p压力和180秒比赛smoke。旧Windows22性能/网络结果保留原归属，当前Goal仍未完成；已知偶发功能风险将随新包验证继续检查。外部多机及人工观感不计Goal。维持串行隔离、禁用系统鼠标捕获、不切用户桌面、无提交或发布。

证据：Windows22/CurrentSourcePreparation02/FrozenMultiFixtureTerminalAuditPreparation01/fresh105-independent-terminal-validation01.json（51检查，SHA658fe49a0ca780ed2ceb5b70417dc18512f9e070d613d7fae2cb4978d67ba326）；UnityTests/20261014-114549.xml；Windows23/MinimalReusePreparation01/core-test-receipt.json（SHA1cad41169515208cd664094f1377b0d012a9d1f3a717ebc3f9f376143c194e8b）；同目录build-prerequisites.json（SHA1beef20cbff8aa8494fa456812d63203ca4871f7a2eee9e25665f333070607ab）；ExactGameplayDocumentEvidenceRepair01/application.json。以下历史原文完整保留。


## 2026-10-14 六项重试回归及恢复核验通过，原完整11项复测中（UTC11:21）

fresh-fixtures-104 两个完整原类 BlockGravitySupportRetryWindowTests、BlockPublicationRetryCapabilityTests 已真实终态：6/6 Passed，原工具session12536外层0、runner子退出0，restored=true、pendingRestoration=false，wrapper/restoration无错误。独立62项终态证据审计通过，核对1384个source/meta、三程序集身份、完整六方法XML、10文件及19逻辑/17物理偏好恢复。UTC11:19:31.2546975Z独立postRead确认原70668隔离Editor、同Runtime a0c143c1-838e-42b0-bb2c-9bf702063592、Tests eaa66c65-c070-4eda-8360-5e135fbd06f8、Config f10e0192-24dd-4c09-b871-9bf5db4b0607，六idle、两focus=false，输入桌面Default。

功能稳定性仍有原fresh101的1个失败未关闭：MovingPaidBuildRetainsOriginalContextAcrossItsRealCloneAndTwoReservationChanges。在下落期间持续付费建造并排入两次拆除，原4000帧后落定高度上限cap仍2、期望1.6；后续Drain及最终付款/守恒/wire检查尚未执行。两个新增针对测试fresh103的2/2以及本次6/6均不能替代这个原组合场景。生产缓存候选仍未应用，未宣称根因修复。

原完整 BlockDependentEditTerrainTests 11项已串行启动fresh-fixtures-105（真实工具session21219，观察预算1800秒），暂无终态。此预算仅覆盖整类运行，不改变测试输入、4000帧、Timeout或断言。不重复新增两项或无关整库。原4类23项加新增2项共25项的跨完整类最新记录为24项有通过记录、1项未关闭；并非同一轮25/25全绿。

整个Goal仍剩六类首版验收：当前源码功能稳定性、常规/交火/零散性能、高塔/级联性能、弱网纠正、六组联机与恢复、最终当前源码Windows包和报告。八爆旧来源结论保留，外部多机与人工观感不计Goal。保持隔离桌面串行、禁用系统鼠标捕获、不切用户桌面，无提交或发布。

证据：Windows22/CurrentSourcePreparation02/FrozenMultiFixtureTerminalAuditPreparation01/fresh104-independent-terminal-validation01.json（62检查，SHA1a687571f1ebbd507a892cc365d0d58b3c81a1f16af53df7363b083f587fe753）；PublicationRepair37Application01/fresh104-postrun-read01.stdout；RegressionTriage/LatestSourceFullRegression15/Runs/fresh-fixtures-104/。以下历史原文保留。


## 2026-10-14 两项针对性回归通过，六项重试回归运行中（UTC11:07）

fresh-fixtures-103 的完整 BlockBuildRejectionAccumulationTests 已真实完成：2/2 Passed，outer/child均0，restored=true、pendingRestoration=false；独立64项终态证据审计通过。测试覆盖多个真实移动占据格下的有限建造重试及后续FIFO付款，以及实际发生拒绝后推进模拟时间、原付款在腾空地址完成建造；真实格数据、稳定/下落碰撞、原source字节、wire及终态付款均验证。

此前fresh102的1 Passed / 1 Failed原样保留。其逐帧80/81失败来自测试把整次命令最终回调才更新的token.Added误当作每次partial已提交量；另80格链的QueryWork3960未越过普通命令实际最大4096。只修正新增测试：84真实格链QueryWork4306、逐帧command.Total与独立stable/falling payload守恒，原最终付款/回调/碰撞断言保留。纠正后实际Editor编译errors=0，Runtime仍a0c143c1-838e-42b0-bb2c-9bf702063592，Tests为eaa66c65-c070-4eda-8360-5e135fbd06f8，1384项source/meta仅该测试源相对上一编译变化。没有应用BuildBlockedCacheLifetimeCandidate01生产候选；2/2通过不证明候选必要，也不关闭原fresh101失败。

剩余功能工作为：原BlockDependentEditTerrainTests完整11项中的移动建造失败仍未关闭；BlockGravitySupportRetryWindowTests与BlockPublicationRetryCapabilityTests两个完整类共6项已串行启动fresh-fixtures-104，尚无终态，不能计通过。随后应跑原完整Dependent11，不重复刚通过的新2项或未受影响整库。最初23项加新2项共25项的历史最新叶记录为18通过、1失败、6运行中；这是功能子范围，不是整个Goal剩余总数。

整个Goal仍剩六类首版验收：当前源码功能稳定性、常规/交火/零散性能、高塔/级联性能、弱网移动纠正、六组联机及恢复、最终当前源码Windows包与报告。外部多机和人工观感不计Goal。继续隔离桌面串行执行，禁止系统鼠标捕获，不切用户桌面；无提交或发布。

证据：Windows22/CurrentSourcePreparation02/Fresh102BuildRejectionTerminalAuditPreparation01/fresh102-build-rejection-independent-terminal-validation02.json；Fresh103CorrectedBuildRejectionTerminalAuditPreparation01/fresh103-build-rejection-independent-terminal-validation01.json（同上CurrentSourcePreparation02下）；UnityTests/20261014-110037.xml；PublicationRepair37Application01/fresh103-postrun-read01.stdout；BuildBlockedCacheLifetimeApplication01/TestOracleCorrection01。以下保留历史原文。


## 2026-10-14 最新功能回归10/11通过，仍剩六类（UTC10:12）

fresh-fixtures-101 的完整 BlockDependentEditTerrainTests 已真实终态：原11个方法全部执行，10 Passed / 1 Failed。唯一失败为 MovingPaidBuildRetainsOriginalContextAcrossItsRealCloneAndTwoReservationChanges：原4000帧循环后根积木的落定高度上限仍为2，期望1.6；此项后续Drain和最终付款/守恒断言未执行，不能计通过。实际根因尚在定位，不将Fair计数全零直接当作排除间接影响的证明，也不放宽原输入、帧数或断言。

本轮关联回归4类23项：调度6/6、DependentEdit10/11，即16项通过、1项失败、两个Retry类共6项尚未测。这7项仅是当前这一轮功能回归内的待闭合测试，不是整个Goal的剩余总数。

按已批准首版范围，总计仍有六类未完成：当前源码功能稳定性；常规/交火/零散结构性能；高塔/级联性能；弱网移动纠正；六组联机及恢复；最终当前源码Windows包与报告。真实多机和人工观感两项外部验收不计Goal。旧包通过记录保留原来源，不能替代当前源码最终交付。

原生工具session40749外层退出1、Python子runner退出3分别保留；restored=true、pendingRestoration=false。独立56项终态证据审计通过，核实1382项source/meta、三程序集身份及10文件/19逻辑17物理偏好恢复；UTC10:04:45.9995464Z独立Read确认隔离Editor70668六idle、两focus=false，输入桌面Default。证据审计通过不代表失败测试通过。

证据：Windows22/CurrentSourcePreparation02/Fresh101TerminalAuditPreparation01/fresh101-independent-terminal-validation01.json；UnityTests/20261014-100223.xml；PublicationRepair37Application01/fresh101-postrun-read01.stdout。以下保留历史原文。


## 2026-10-14 当前源码调度完整回归6/6通过（UTC09:40）

fresh-fixtures-100 的完整 BlockFairSchedulingNaturalQueueTests 已真实终态：六个源码测试方法、六个唯一XML执行叶全部Passed，原native session24355及runner/wrapper退出0，restored=true、pendingRestoration=false。原运行约596.610秒，不能用XML重载后的局部duration代表整类墙钟时长。53项独审核实原XML/archive及来源、1382项source/meta身份、10文件和19逻辑/17物理偏好精确恢复；UTC09:39:35.0693807Z独立postRead确认原70668/current三MVID/六idle/两focus=false/输入桌面Default。

本次关联回归4类23项中6项完成，剩17项：完整DependentEdit11及两个Retry类各3项。fresh-fixtures-101单完整DependentEdit类已串行启动，实际工具session40749，终态尚未取得；外部观察预算1800秒，仅为覆盖完整运行，不修改原测试Timeout、4000帧、输入或断言。Natural05同操作链续行通过结论保留；当前功能总门、性能、联机、最终包仍未关闭，仍剩六类首版验收。不将历史125/124失败改判为已解释。

证据：Windows22/CurrentSourcePreparation02/Fresh100TerminalAuditPreparation01/fresh100-independent-terminal-validation01.json；RegressionTriage/LatestSourceFullRegression15/Runs/fresh-fixtures-100/；PublicationRepair37Application01/fresh100-postrun-read01.stdout。以下保留历史原文。


## 2026-10-14 修复后的同操作链验证通过（UTC09:27）

Natural05 已实际运行并完成原 Task：修复后的同 trigger→D→B 付费操作链，在模拟时间仍为10、原等待期限10.285479366464806尚未到达时完成B。完整有效sleep-only扫描授予同seed与原后台lease的一段普通队首服务；真实原生网格预烘焙及一次非dependency前台原子发布完成，B新增1格且token兑现1格。原付款、FIFO、守恒、wire及清理断言通过；Physics.Raycast/TryContact核实(60,0,-40)为稳定己方原生Collider，旧trigger碰撞已消失。总推进603帧包含清理，不能用模拟期限或总推进数推导Player响应性能。

原生工具session90511真实退出0，owner已Restored且pendingRestoration=false；独立47项终态证据审计通过。原10文件及19逻辑/17物理偏好恢复、1382项source/meta身份保持。UTC09:26:25.4320139Z另一次根独立Read确认原隔离Editor70668、当前Runtime a0c143c1-838e-42b0-bb2c-9bf702063592及既有Tests/Config身份、六项idle、Editor/GameView无焦点，输入桌面Default。

结论仅为本次同输入配对续行验证通过，不解释fresh37的125/124偶发少格，也不代表整体功能稳定性、完整回归、Player性能或联机已通过。当前仍剩六类首版验收。后续本次窄修关联回归范围为4个完整类23项：FairNaturalQueue6、DependentEdit11、GravitySupportRetryWindow3、PublicationRetryCapability3；fresh-fixtures-100完整Fair类已串行启动，终态未取得，其余三类尚未执行。旧fresh98和Windows22通过保留原来源，最终当前源码Windows包尚未构建。

证据：Windows22/CurrentSourcePreparation02/NaturalSleep05TerminalAuditPreparation01/natural-sleep05-independent-terminal-validation01.json；NaturalSleep05Execution01/command.json；PublicationRepair37Application01/natural05-postrun-read01.stdout。以下保留历史时点原文。


## 2026-10-14 自然队首停滞已见证，窄修已编译（UTC09:05）

Natural04 已真实终态：same公开 trigger→D→B 操作中，D实际拆1格且回调一次，B仍为普通付费FIFO队首。完整有效扫描已Finished、HasSleeping=true、HasBlocker=false，模拟时间10小于原到期10.285479366464806；该状态连续4000实际推进帧不变，数量/付款/FIFO守卫均通过。总推进4497帧包含前置与清理。原Task、清理、退出、10文件及19逻辑/17物理偏好恢复、1382源码身份经47项独审核实；两次审计的UTC末尾零文本比较失败原样保留，最终审计只对两个已定位时间字段核对精确100ns值，其余字段严格相等。

这证明冻结模拟时间下可自然进入的队首停滞；不证明真实时间永久卡死，不解释fresh37原125/124少格。基于该见证，已对BlockTerrain.FairScheduling.cs应用单个8行增量分支：完整有效未过期sleep-only扫描且无阻挡时，沿既有EnterFairForeground只服务准确的普通活队首一段，保留后台lease，随后重新后台重放。原HasBlocker/到期/源版本失效、预算、付款、回调和数据操作均不修改。1382项source/meta中仅这1个生产源码变化，meta不变，备份已保存。

实际编译errors=0；UTC09:05:23原Editor70668加载Runtime a0c143c1-838e-42b0-bb2c-9bf702063592，Tests仍6a80528a-fd8b-4109-9189-e1603fd2d346、Config仍f10e0192-24dd-4c09-b871-9bf5db4b0607，六项空闲、两focus=false，用户桌面Default。修复后同操作链验证与相关完整回归尚未执行，不把旧fresh98的11/11迁移到新Runtime。仍剩六类首版验收，最终新Windows包尚未构建。

证据：Windows22/CurrentSourcePreparation02/OccupancyProbeRepair01/IntermittentFunctionalFailuresReadOnly02/SleepOnlyNaturalEntryDiagnosticCandidate04/Runs/natural-sleep-04-01/natural-entry-result.json；NaturalSleep04TerminalAuditPreparation01/natural-sleep04-independent-terminal-validation03.json；SleepOnlyFairContinuationApplication01/application.json及compile-validation.json（均相对Windows22/CurrentSourcePreparation02）。以下各记录保留历史归属。


## 2026-10-14 最新回归与自然时序诊断（UTC08:35）

当前仍有六类首版验收未关闭：当前源码功能稳定性、常规/交火/零散性能、高塔/级联性能、弱网移动纠正、六组联机及恢复、最终同源 Windows 包与报告。真实多机和人工观感两项外部验收不计 Goal。以用户批准的 GoalScopeFirstPlayable20261012.md 为准。

本轮仅对既有 BlockDependentEditTerrainTests.cs 增加失败时取证，原11个测试方法、输入、4000帧及付款/FIFO/守恒断言不变；实际编译 errors=0。当前 Runtime MVID 为 8b35f20e-4d45-40dd-9aa0-8b9514313eb8，Tests 为 6a80528a-fd8b-4109-9189-e1603fd2d346，Config 为 f10e0192-24dd-4c09-b871-9bf5db4b0607。fresh98 完整夹具实际11/11 Passed，独审51项通过；10文件、19逻辑/17物理偏好精确恢复，1382项源码/meta身份一致。复测通过不能证明 fresh37 偶发生产根因已修复。fresh37 的权威切换失败发生在原4000帧 Pump，旧XML不能区分 InstallChain 与最终 Drain；旧文中将其直接写成最终 Drain 失败过于具体，以此处澄清为准。

Natural03 已实际编译并运行，真实 Task 终态及原清理、退出和恢复完成，独审46项通过；实际推进303帧（包含清理）。诊断自身状态仍为 FunctionalOracleOrHostFailure：它在拆除命令完成后读取 Targets，而生产 Complete 在回调前已主动将该数组清空（BlockTerrain.cs）。已观察到真实背景拆分、退入前台种子及拆除一次完成；尚未取得 sleeping-only 见证，未解释原125/124守恒失败，也未证明永久卡死。将准备 Reports-only Natural04，在数组仍有效时保存不可变快照；不改变生产源码或原验收断言。owner退出0只证明安全生命周期收尾，不代表诊断通过。

UTC08:35:21 独立只读确认原隔离 Editor70668、同三程序集、六项空闲且 Editor/GameView 均无焦点，用户输入桌面仍为 Default。测试继续串行、禁止系统鼠标捕获，不切换用户桌面。本轮没有最终新包，不将旧 Windows22 的功能/八爆结论转移到当前源码。

证据：Windows22/CurrentSourcePreparation02/OccupancyProbeRepair01/ColumnFailureDiagnosticApplication02/fresh98-independent-terminal-validation01.json；Windows22/CurrentSourcePreparation02/NaturalSleep03TerminalAuditPreparation01/natural-sleep03-independent-terminal-validation02.json；Windows22/CurrentSourcePreparation02/NaturalSleep03Execution01/root-terminal-review01.json。以下记录保留各自历史时点。


## 2026-10-14 修正版编码诊断已完成（UTC07:13）

Micro02 已在原隔离 Editor70668 实际执行一次，native/wrapper 均退出0；104 次操作的字节等价、独立解码计数、源 World 守卫及严格 saved JSON/CLI report 相等均通过，1382 个 source/meta 身份未变化。局部世界为25万稳定格＋2万下落格。七个相同增量的原路径/批编码中位耗时为192.771/26.871ms，混合快照/增量为199.247/48.084ms，配对耗时比中位数分别7.009和4.155。全部样本保留，不将局部编码改善记为正常 Player 帧率、网络或碰撞发布验收通过。

已知1MiB分配自检实际返回delta=0，证明当前运行时计数器不可靠；104条allocation均为null并带原因，不能声称零分配。before/after managed heap也不代表实际Player峰值内存。Micro01原wrapper精度失败继续保留。UTC07:13:18独立postmicro Read确认同PID及三MVID、六idle与两focus=false，测试未触碰前台桌面。证据：`Windows22/CurrentSourcePreparation02/TransferEncodeBatchApplication01/MicroDiagnosticExecution02/terminal.json`、`root-terminal-validation01.json`和Micro02原结果。仍有六类首版验收未关闭，功能偶发失败仍无已证根因修复。

## 最新记录：2026-10-14 编码回归与微诊断

同次同步调用内批量编码复用已实际应用；fresh96 三个完整夹具 80/80 Passed。fresh97 完整 `BlockDependentEditTerrainTests` 已真实终态 11/11 Passed，原 native session56627 与 runner/wrapper 均退出0，restored=true、pendingRestoration=false。独审核实原XML/archive字节、11个唯一叶及源码完整方法集、当前1382项source/meta、10文件冻结状态与19逻辑/17物理偏好恢复；UTC06:51:47 post97 Read确认原70668、同三MVID、六idle及两focus=false。fresh97 实际wrapper耗时983.666秒；XML顶层14.216秒未覆盖早期叶执行，不能用于全夹具耗时或性能判断。

首版六类仍未闭合：当前源码功能、常规与零散性能、高塔与级联、弱网纠正、联机矩阵、最终交付。fresh37 两项原失败本轮均Passed，但根因仍未定位，未证明生产根因修复，功能稳定性与当前功能门保持未解决。Windows22原来源功能及八爆结论保留原归属；本记录不生成新核心通过回执、不认可新同源包或性能通过。

Micro01 的 native CLI退出0、Pipeline与脚本success，输出104次操作记录；Python wrapper实际退出1，原因是32项摘要double经G15序列化后的精度差异。104项分配计数全0的计数器无效，不能声称零分配，也不能据此验收Player性能。Micro02仅已准备并处于独立审查，尚未执行。下文各旧时点记录完整保留，以本节更新当前状态。

证据：[fresh96独审](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows22/CurrentSourcePreparation02/TransferEncodeBatchApplication01/fresh96-independent-terminal-validation.json)、[fresh97独审](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows22/CurrentSourcePreparation02/TransferEncodeBatchApplication01/fresh97-independent-terminal-validation.json)、[Micro01原始结果](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows22/CurrentSourcePreparation02/TransferEncodeBatchApplication01/MicroDiagnosticExecution01/command.stdout)与[CLI退出记录](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows22/CurrentSourcePreparation02/TransferEncodeBatchApplication01/MicroDiagnosticExecution01/command.json)。


最新编码修复（UTC06:30）：TransferEncodeBatchCandidate01的2生产/3测试文件已实际应用，只在同次同步调用复用chunk片段、WriteRuns、同基线payload与checksum，各peer发送状态独立；1382源码/meta身份中仅5个审查源码变化。实际编译errors=0，新Runtime 8b35f20e-4d45-40dd-9aa0-8b9514313eb8、Tests 9df427dc-5d03-47e8-b19e-99ac9b43e1c9，Config不变。fresh96三个完整fixture共80/80 Passed，原XML/archive字节、源码成员、唯一ID/fullname、当前1382SHA、10文件及19逻辑/17物理偏好恢复均由Root核验；UTC06:30:21独立Read为原70668隔离Editor、同三程序集、六idle及两focus=false。旧fresh39编号碰撞在out.mkdir拒绝，未提交测试，不覆盖历史。随后完整BlockDependentEditTerrainTests 11项fresh97已串行启动（session56627），终态尚未取得；此前偶发失败尚无根因修复，仍剩六类。不把80项编码回归计为性能/网络或核心总门通过，尚未生成最终新包。证据：Windows22/CurrentSourcePreparation02/TransferEncodeBatchApplication01/application.json、compile-validation.json、fresh96-terminal-validation.json。


最新性能取证（UTC06:02）：旧Windows22包的20秒CPU诊断已真实终态，8个owned Player全部退出、19项偏好精确恢复、完整包before/after一致，1507帧/20.011秒/8真实Player/1920x1080，原诊断exit5与性能未通过结论保留。已串行离线提取579515839字节raw，完整扫描1508个native frame summary并取得1507组metadata，展开19帧/236066 samples，全部cap=false、tree/malformed错误0；原Profiler空history恢复成功，独立postextract Read确认原Editor70668、同三程序集、六idle和两focus=false。初步完整主线程树可见多个长帧中TransferEncode重复编码及GC耗时，另有Tick长帧，正在独立分析并准备仅单次同步调用内复用编码片段的候选；尚未应用生产修复、不计当前源码性能通过，仍剩六类。证据：Windows22/CurrentSourcePreparation02/CpuDiagnosticSteady20Run01/terminal-validation01.json 与 CpuExtract01/manifest.json。


最新核验（UTC05:46终态）：fresh38完整 BlockDependentEditTerrainTests 11/11 Passed，两个fresh37失败成员本轮均通过；原XML、唯一成员/ID、前后及当前源码/meta SHA、10文件与19逻辑/17物理偏好恢复均已核对，wrapper/runner实际退出0。只增加失败诊断，尚无生产根因修复，因此原偶发失败不据单次复测改判，功能稳定性仍待闭合，仍剩六类首版验收。恢复Read确认原Editor70668、同程序集、六idle及两focus=false；本记录不替代额外独立post38 Read。证据：Windows22/CurrentSourcePreparation02/OccupancyProbeRepair01/FailureDiagnosticApplication01/fresh38-terminal-validation.json。


UTC05:30最新进展：fresh37九个完整关联夹具真实终态97项、95 Passed / 2 Failed；10个现场文件和19逻辑/17物理偏好精确恢复，UTC05:23:38独立Read确认原Editor70668、同三程序集、六idle及两focus=false。原失败不改判，见 `Windows22/CurrentSourcePreparation02/OccupancyProbeRepair01/fresh37-terminal-validation.json`。随后只应用BlockDependentEditTerrainTests的失败诊断，原输入、4000帧、付款/FIFO/守恒断言不变；实际编译零错误，Runtime/Config不变，Tests MVID为b61397f7-80fe-437b-8d2d-03bed3896326，UTC05:29:45独立Read确认隔离空闲。完整11项fresh38已沿原保全入口串行启动，原生session10247；其终态和恢复未完成。诊断不是生产根因修复，当前仍剩六类，不生成新核心通过回执或新同源包。证据位于 `Windows22/CurrentSourcePreparation02/OccupancyProbeRepair01/FailureDiagnosticApplication01/`。以下旧运行中表述保留为历史时点。

UTC05:20补充：fresh37前六个完整夹具已逐一核对原XML、固定源码成员及唯一执行ID，共34项、32 Passed / 2 Failed；付款额度标量2/2及原子发布4/4新增通过。原session58044仍运行，后续FairScheduling/Safety/Attack、整批终态与恢复尚未完成。见 `Windows22/CurrentSourcePreparation02/OccupancyProbeRepair01/fresh37-six-completed-fixtures-observation01.json`。两项失败的合并诊断候选已完成静态审查：失败时独立数稳定/下落载荷并对比命令及token账本，Pump补充队首与任务状态，诊断异常隔离，原断言及4000帧不变；尚未应用、编译或运行，不是根因修复。当前仍剩六类，不生成新核心通过回执或新包。

最新源码状态（UTC05:02）：**仍有六类未闭合**，新增待闭合项为功能关联回归。fresh37 的 BlockDependentEditTerrainTests 完整11项实际9 Passed / 2 Failed：权威切换后 Drain 未达到终态，以及最终守恒 expected125 / actual124。原XML与断言完整保留，尚无运行根因修复；不得生成新核心通过回执或构建新包。fresh37仍由原session58044串行运行，整批终态及恢复未完成。原Windows22功能与八爆通过仍仅属于原来源。证据：[本次真实失败](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows22/CurrentSourcePreparation02/OccupancyProbeRepair01/fresh37-dependent-edit-actual-failures.json)。

后续修复实际编译已完成（errors=0）：UTC04:24:51独立Read确认原隔离Editor70668、Runtime `60163eb9-e008-4171-b20b-c2980cc17224` / Tests `f6471008-8d0b-4b42-96f5-d9a1c5d36350`、Config不变、六idle及两focus=false。fresh36已真实退出0：报告写入与LateFloor两个完整夹具合计16/16 Passed，10文件与19逻辑/17物理偏好精确恢复；UTC04:35:58独立Read确认同Editor及新三程序集身份、六idle、两focus=false。随后fresh37通过原保全入口串行运行九个关联完整夹具；截至UTC04:46，重力支撑释放4/4已通过，整批仍运行、不得计新来源核心总门或新包通过。完整fresh36证据见 OccupancyProbeRepair01/fresh36-validation.json。证据：[修复及编译记录](Windows22/CurrentSourcePreparation02/OccupancyProbeRepair01/compile-validation.json)。重载期间一次recompile_status HTTP400保留为观察失败，后续真实status及新程序集Read确认完成。

采用用户批准的 [首版范围](GoalScopeFirstPlayable20261012.md)，继续同一 active Goal。真实多机与人工操作观感不计 Goal。

更新时间：2026-10-14（Windows22 V6已真实终态及恢复核验；Windows22原来源功能与八爆两类已关闭；最新源码功能关联回归出现2项失败，**当前六类首版验收未完成**。UTC04:22:05后续修复编译已通过，关联回归仍运行，新包未构建）。

## 最新进展

已通过：Windows22来源核心功能、八次同时爆炸、正式构建与包身份、自然三分钟比赛流程。最新V6六场均有有效1080p八人完整采样，八爆通过全部首版必需场景门；其他五场仍有帧时失败，常规/交火另有普通发布延迟失败。真实多机及人工观感继续不计Goal。

### 当前七类状态（保留Windows22来源；最新源码剩六类）

| 类别 | 当前状态 |
|---|---|
| 功能 | Windows22来源Passed；104完整类/115选择器/747唯一通过叶，原37失败闭合；后续已应用修复的关联回归出现2项失败，当前源码功能门未通过。 |
| 常规与零散性能 | 未通过；steady/combat/scattered均有有效完整采样但帧时超标；前两场普通发布P95也超过200ms。 |
| 八爆性能 | Windows22同包Passed；全部8次直接拆除386.568ms，帧时9.360/11.491ms，整批终态、偏好恢复及同包复核齐全。 |
| 高塔与级联 | 未通过帧时；32米/31900格及20000格守恒、全部落定和原生碰撞通过，实际落定时间及长帧风险保留。 |
| 弱网纠正 | 未通过/覆盖未完成；当前网络四组drain超时，八人100ms/1%未启动，完整纠正与恢复硬门未共同通过。 |
| 联机矩阵 | 未通过；实际4/6执行、0通过，原summary仅3组，末两组未启动；原WinError5与所有失败保留。 |
| 交付 | 部分完成；原来源正式资源构建、359文件包身份和自然比赛Passed，中文HUD已阅图核验相应画面；仍需修复后同源最终包、网络/性能与最终报告。 |

### Windows22 V6六场实际终态

全部为当前电脑、PC画质、Direct3D12、1920×1080正常主相机及八个独立Player；CPU i7-11700K、GPU RTX 3060、内存32493MB。六场均完成指定采样，所有场次errors=0、failedTransactions=0、客户端日志干净；整批只有八爆通过，不能把完整采样写成完整性能通过。

| 场景 | 采样秒 / 帧数 | 帧时P95 / P99（ms） | 当前结论 |
|---|---:|---:|---|
| steady250k | 180.008 / 15605 | 20.633 / 35.708 | 未通过；最少280446格，普通发布P95=145050ms，门槛200ms。 |
| combat250k | 300.004 / 20652 | 25.543 / 40.684 | 未通过；最少288749格，普通发布P95=3133.333ms。 |
| tower32m | 60.029 / 4531 | 41.838 / 68.598 | 未通过帧时；32米、31900格守恒、全部落定及原生碰撞通过。 |
| blast8 | 120.006 / 14132 | 9.360 / 11.491 | Passed；8/8/8次发起/完成/产生删除，全部直接拆除386.568ms≤2000ms，最少259912格。 |
| cascade20k | 120.004 / 13212 | 13.048 / 22.420 | 未通过P99；20000格守恒、全部落定及原生碰撞通过。 |
| scattered100k | 180.031 / 14885 | 22.288 / 38.259 | 未通过帧时；完整100000零散格，数量保持。 |

高塔/级联实际失支撑排空与完整落定分别30192.343/15145.147ms，原500ms门为首版非阻塞诊断，不能据此覆盖帧时失败。六场偶发长帧及原strictPerformancePassed=false完整保留；八爆3个>33.3ms、2个>50ms、最大98.306ms，首版通过不代表原严格无尖峰稳定60帧通过。

### V6终态、恢复与同包核验

原生session79322于UTC03:55:00—04:15:33完成；native/bootstrap/harness/guard均实际exit5，汇总requiredWorkloadsComplete=true、performancePassed=false、fullAcceptancePassed=false。全部48个owned Player真实终态（5个host exit5、八爆host exit0、42个client exit1）；client exit1按harness收尾记录保留，不仅凭该码推定崩溃。全部六个case的clientLogsClean=true。

原19项逻辑偏好restored/rawEqual/branchExistenceEqual均为true；[恢复原件](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows22/CurrentSourcePreparation02/IsolatedAcceptanceV6Dpi/_desktop-launch/StandalonePreferences/verification.json) SHA `d57fc95f468d421361341fb1e6849b13c542a44b097217bdb953f1d5f17fccec`。窗口桌面审计未观察到Player在输入桌面，readErrors=0；保留其采样及未分类窗口所有者的范围限制，不宣称完整前台无干扰验收。

独立[postperformance22-v6-verify01](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows22/CurrentSourcePreparation02/postperformance22-v6-verify01.command.json)实际exit0，完整359文件同包身份保持。随后UTC04:17:51.7129123独立[Editor Read](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/PublicationRepair37Application01/postperformance22-v6-01.stdout)确认原70668、同Runtime/Tests/Config三MVID、六idle、applicationFocused/gameViewFocused=false及非输入桌面。由此八爆在Windows22原来源同包正式闭合。

终态[summary.json](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows22/CurrentSourcePreparation02/IsolatedAcceptanceV6Dpi/prototype-v1/summary.json) SHA `59286b31510a837a496ec57aeb24764a636a150f40887e03ab6ebc039f42ff62`。V5六场零样本和全部原失败继续保留为历史，不能再作为最新V6结论。

当前Windows22网络session60729实际四组执行、零组通过：双人三组及八人0ms/0%均drain超时，第四组后summary原子replace遭遇PermissionError/WinError5，原summary只含前三组；八人100ms/1%和200ms/3%未启动。网络19项逻辑偏好全部恢复、14个Player终态及postnetwork同包VerifyOnly0不代替网络通过。

steady250k同区间build preparation stages457→9536、work373874→7901570、build retries4→235，Fair计数全0，支持先优化移动占据准备；pending529→13977和原145050ms普通发布失败保留。主控实际查看同包main-camera.png，中文队伍/热身/材料/副武器/大招/弦化HUD及积木格边界清晰；只完成该截图范围的显示核验，不代替完整交付。

### 已应用修复与下一轮边界

UTC04:22:05已实际应用[OccupancyProbeRepair01](D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain/Windows22/CurrentSourcePreparation02/OccupancyProbeRepair01/application.json)的8个窄范围文件：空运动列占据证明跳过、对应TestCandidate02、Probe报告流式写入及8例测试、网络summary原子替换重试。summary重试15个离线故障注入已通过；实际Unity编译零错误及新程序集身份已核验，fresh36两个完整夹具16/16 Passed且完成恢复。fresh37九个关联完整夹具的整批回归仍在串行运行；当前源码核心总门、新同源Windows包及包级复测均未通过。

这些修复不改变批准玩法、场景规模或验收门槛；尚不能声称普通发布积压、帧时或网络drain问题已修复。Windows22功能闭合与八爆通过仅属于原来源；不能迁移为修复后源码已通过，也不能交付旧包充当新源码最终包。后续按实际编译、关联回归、正式资源/新包及同包网络/性能串行验证。

## 历史记录：V6运行中与V5终态快照

以下完整保留之前时点的记录；本节“当前”“仍运行”和“剩六类”仅指当时，已由上方V6终态及最新源码六类待闭合状态取代。

更新时间：2026-10-14（核心功能已通过，剩六类包级验收；Windows22正式包及自然三分钟比赛流程已通过相应检查。旧V5六场未取得有效采样。修正后台DPI坐标后的V6于UTC03:55:00实际启动，原生session79322，首场已证明实际主相机1080p，当前仍在运行；不将运行中结果算作通过）。

## 最新进展

V6目前已有四个不可变单场终态：steady250k、combat250k与tower32m未通过；blast8通过首版全部必需场景门。八爆实测8次完整完成，386.568ms（门槛2000ms），120.006秒、14132帧、8个独立Player、主相机1920×1080，帧时P95/P99=9.360/11.491ms，host exit0、客户端日志干净、失败事务0；偶发长帧保留为首版诊断风险。整批session79322仍运行，尚未取得整批偏好恢复与终态收尾，因此按正式闭合口径仍为六类未关闭，其中八爆已取得单场通过、待收尾确认；另五类尚未通过。交火300秒P95/P99=25.543/40.684ms，普通发布P95=3133.333ms；32米塔守恒、完整落定与最终原生碰撞通过，帧时41.838/68.598ms未通过。只读取已完成case，不读取活跃summary。

V6首场`steady250k/31bbac23/case.json`已取得当前包有效完整样本：180.007697秒、15605帧、8个独立Player、1920×1080正常主相机、至少280446存活格；client日志无异常、失败地形事务0。原判失败保留：帧时P95/P99=20.633/35.708ms，普通命中至发布P95=145050ms，pending由529增至13977。相同区间build preparation stages由457增至9536、work373874→7901570、build retries4→235，Fair计数全0，支持优先优化移动占据准备而非先改Fair。全部六场仍由原session79322串行执行，不读取活跃summary。Root已实际查看同包`main-camera.png`：中文队伍/热身/材料/副武器/大招/弦化HUD清晰，积木格边界可见；这只完成该截图范围的中文HUD显示核验，不代替比赛、网络或性能验收。

后台性能窗口DPI修复已通过静态独审并取得原生见证：`FrontendInputProtection33/NextLaneDpiCandidate02`只改窗口尺寸调用线程的DPI上下文与外框换算，原源码、Tools、全部负载、时长、断言和隔离/禁捕获规则不变。新manifest `Windows22/CurrentSourcePreparation02/performance-v6-dpi.actual-lane.json` SHA `375aeab2a03b65df65013b6411dd4ead7acf4c590321a3419ed2c21d43899bff`绑定同359文件Windows22包。UTC03:53:28独立Read为原70668、同三MVID及六idle，包VerifyOnly实际exit0；UTC03:55:00串行启动V6，launcher75948/bootstrap107600/session79322。首场`ResolutionObservation/steady250k.json`真实记录windowDpi144、native与Screen/Camera均1920×1080、4份advancing稳定receipt、调用线程DPI已恢复；七客户端仅在该门通过后启动。仅此分辨率门已证明，未取得完整性能终态，不提升任何性能类别通过。

### 当前七类状态（网络及本轮V5均已终态）

| 类别 | 当前状态 |
|---|---|
| 功能 | Passed；当前核心complete=true，104完整类/115选择器/747唯一叶，原37失败闭合。 |
| 常规与零散性能 | 本轮无有效采样；六场均因1080p后缓冲未证明而拒绝，samples=0，仍待有效完整验收。 |
| 八爆性能 | 本轮在客户端启动前拒绝，samples=0；没有有效爆破性能结果，仍待验收。 |
| 高塔与级联 | 本轮在客户端启动前拒绝，samples=0；完整32米/两万格、守恒、落定及碰撞仍待有效验收。 |
| 弱网纠正 | 未通过/覆盖未完成；当前网络四组全部drain超时，八人100ms/1%未启动，不能认可完整纠正验收。 |
| 联机矩阵 | 未通过；实际4/6执行、0通过，summary仅3组，末两组未启动；原WinError5与所有失败保留。 |
| 交付 | 正式资源构建、359文件包身份及自然180秒流程Passed；中文HUD、网络/性能及最终交付尚未完成。 |

当前Windows22网络session60729已真实exit1：实际四组执行、零组通过；双人三组及八人0ms/0%均在drain排空阶段超时。第四组结束后写summary的原子replace遭遇PermissionError/WinError5而提前终止，原summary仅含前三组；八人100ms/1%和200ms/3%未启动，不能补造六组结果。八人组六个客户端exit1的原因另查，不将该现象直接归因于summary写入失败。

网络19项逻辑偏好restored/rawEqual/branchExistenceEqual均为true，14个ownedPlayers全部真实终态；恢复成功与玩法/网络失败分别保留。独立postnetwork22-verify01实际exit0，当前359文件同包身份保持；UTC03:41:08独立Read为Editor70668、原Runtime/Tests/Config三MVID及六idle。

V5六场性能已真实终态：session3184、launcher91548、bootstrap87708，UTC03:41:44—03:43:48；native/harness/bootstrap/guard均退出5。六场case全部因“Fresh owned normal 1920x1080 backbuffer was not proved within20seconds; refused before clients”在客户端启动前拒绝，全部samples=0且没有截图，本轮没有有效帧时、发布、爆破或坍塌性能测量，不能计性能或中文HUD通过。

性能原19项逻辑偏好restored/rawEqual/branchExistenceEqual=true，6个owned host均真实终态exit1；窗口审计player-on-input-desktop=false、readErrors=0。host观察到Screen2882×1623，Root正在定位DPI/窗口尺寸问题；该观察不是已确定根因。性能后的独立完整包复核仍按真实后续证据签收，不从此前postnetwork核验推定。

manifest SHA仍为9138ac977ef09be6b5637240037117067190eee41de674ccf8369a7c2f115428；本次仅据主控已核实终态更新文档，没有读取任何活跃结果。

以下保留此前时点的原进展，包含其当时“仍运行”“尚未启动”和旧七类表，不作为当前状态。

同包自然比赛流程已通过：`IsolatedAcceptance/PrototypeV1/PackageSmoke/summary.json`中`naturalRoundWorkflowObservedPassed=true`，自然观测180.0039674秒，最终pink3856/blue684/revision1108，回房间及下一局正常；原`packageSmokePassed=false`覆盖边界保留，中文HUD及清空oracle由同包补充验收。原生session28047、native/bootstrap/harness及双端Player均实际exit0，19项逻辑偏好rawEqual/branchExistenceEqual/restored=true、guardExitCode=0。summary前后完整359文件一致，独立`postsmoke22-verify01`再次实际exit0。UTC03:28:42独立Read为70668、当前三MVID及六idle。随后UTC03:30:03同包六组网络通过原launcher实际启动，原生session60729、bootstrap87840，仍在隔离桌面并禁系统光标捕获；运行中不计通过。验证报告与调参说明已更新当前22来源，旧20/21结果明确保留为历史。

正式Windows22构建与包身份核验已完成：`Windows22/CurrentSourcePreparation02/build-record.json`记录UTC03:18:39成功，正式Addressables与递归包清单齐全；包为`Builds/Blocks-20261014-acceptance22/InkLan.exe`，EXE SHA `c7e054edf3cd28b374e29139288d6d3cb0077a75f677ebd0dea5ebdd91c53607`。首次成功包核验`inspect-package22-04`实际exit0，完整359文件一致，`package-identity.json` SHA `9f0292adb449738e818fcff91edd805ad748bbb7080534ec232f6d1b3e8ecd40`。前三次检查分别遇系统脚本策略/PowerShell版本模块冲突，原失败均保留，使用已有PowerShell7后原inspector成功，未修改包或校验标准。UTC03:19:23构建后Read确认同70668、新三MVID及六idle。UTC03:23:12已实际启动同包自然180秒比赛，工具session28047，bootstrap87384，隔离桌面与禁系统光标环境有效；运行中不计通过。V5性能manifest已从真实包身份离线生成，SHA `9138ac977ef09be6b5637240037117067190eee41de674ccf8369a7c2f115428`，仅准备、未运行性能；后续六组联机及六场性能仍串行执行。

fresh35原XML `UnityTests/20261014-030549.xml`完整Aim类1/1Passed，SHA `43876176dd73ed0a5b895b306b047f641ee0e407cd7e31761586e164b885827a`；原runner、wrapper和原生session59296均实际exit0。原preserver完成精确10文件/19逻辑及17物理偏好恢复；UTC03:06:52独立Read为70668、当前三MVID、非输入桌面、两focus=false和六idle。实际绑定103份保留原执行身份的非影响整类证据，并保留新图Aim完整执行；没有将历史结果冒充最终图重跑。核心inspect实际complete=true、pending=[]、coreMissing=[]，104个完整类范围、115选择器、747个默认叶，原37失败及全部历史非通过结果保留并闭合。

核心收据 `Windows22/CurrentSourcePreparation02/core-test-receipt.json` 已真实生成，SHA `fc75454b77784f6058c9e898f475fcb05bd768f94210893b7e2a660fad215ac4`，`coreHardGatesPassed=true`、`goalAcceptancePassed=false`；构建前提SHA `aea3a7e91ebea2101c786eafca719f91043b96b51633e09e906821e08467c240`。原v1汇总器把影响报告中的128个源码/meta引用误作Reports证据而拒绝，原失败保留；v2仅将已登记最终图且SHA同时等于图和磁盘的Assets引用独立校验，全部其他门禁不变，独立审查通过。第一次v2输出遇GBK字符编码失败亦保留，随后UTF-8完整inspect及正式生成均真实exit0。正式构建命令`queue-aim02-firstplayable01`已实际返回CLI0/Queue成功，只有排队提交，不提升包验收。后续同包自然比赛、六组联机和六场性能仍未通过。

Aim02实际编译收据已于UTC03:01:30生成：`AimMovingContactObservationCandidate02/Applied01/compile-validation.json`，SHA `fa7a22f72d5602cb4cad2d5533a63f6c2485fdcd7130e8149c4bbf3e0711caf4`，实际errors=0，Tests MVID为`12f83a4c-aaa9-47d4-9f89-7dfaecdf9f8a`，Runtime/Config不变。UTC03:02:25独立Read确认70668、同三MVID、非输入桌面、两focus=false和六idle。原fresh33目录实际保留10月13日MachineGun历史结果，因此仅将已准备Aim入口的运行目录与wrapper输出身份改为未使用的fresh35，旧33及原入口全部保留；同一完整Aim选择器、原preserver、全部断言/超时/恢复门禁不变。UTC03:04:28实际启动fresh35，loaded-observation确认新编译身份，runner已实际进入`AimBallisticsPlayTests`；尚无终态和恢复，不计通过。当前Windows22核心收据、正式构建前提和新包均未生成。

fresh32终态消费收据为 `PublicationRepair37Application01/MovingBuildDiagnostic01/regression32-terminal-consumption.json`，SHA `36061dd0772aac8c8597e704627dbc2aee7e9e8d0c940fee8084846cb7e62ca5`。Root核对370递归证据引用、全部145叶、42完整类及原Aim失败1048；其余41完整类全部Passed，原Failed XML不改判。10文件、19逻辑/17物理偏好精确恢复，UTC02:51:27独立Read确认原70668及三MVID、非输入桌面、两focusfalse及六idle。Root应用审查收据 SHA `481a8d364f5f05b5df6d9893ece8498aeb7c867f6a5a1d9a1c4e30bbb6d57b48`。

Aim Candidate02已经通过现有应用入口实际写入，完整源码/meta及原文件均保全于 `AimMovingContactObservationCandidate02/Applied01`。新source-after SHA `f6aff3b71027c42ab541cd7952123d9fbddc256f28ce1cbce59bb4270d649cc4`，689源码中仅Aim一份变化，689metadata全部不变；Runtime、共享rig、Tools及工作负载不变。实际recompile已提交；编译状态会话23302仍等待真实返回，不将源码应用算成编译或测试通过。下一步为实际成功编译、新Tests身份的独立Read、完整Aim33复测及恢复，然后精确绑定原通过行并核验core。

本轮沿原session51794多次取得真实live响应；TerrainDependencyObservationV2Supplement17完整4/4及TerrainDependencyObservationV2完整5/5均取得原Passed结果，32还剩TpsOverlap、WeaponDebug、WeaponReferenceMeasurement三完整类9默认项。Root已读 `AimMovingContactObservationCandidate02/NonImpactReview01/bind-actual-single-aim-reuse.py`（SHA `2b69d3b0738c98b5bf8ce6a1b05cf7f70325032d99a4ec1fa947f7307a59098f`），并实际执行其默认描述分支；没有导入native、读取活跃32结果或实际绑定。等实际32消费、Aim02应用/编译、完整Aim33通过及恢复后，可一次性生成保留原身份的103个精确复用proof与新plan，直接交给现有core生成器核验；缺少任一实际门禁仍拒绝。原plan、原失败及Explicit排除保持不变。

Root已核对仅Aim Candidate02增量的94类语义影响报告：646默认源用例、7Explicit，Aim唯一受影响，其他93类具备保留真实旧来源的复用资格。核对了226递归证据引用、完整唯一类集合及初始化/发现/回调边界，准备审查收据为 `AimMovingContactObservationCandidate02/NonImpactReview01/RootSemanticPreparationReview01.json` SHA `c08f04c039d9149bb6e951da518c52cb13c4703775d68dec1f14e23f656d885c`。这不是未来图或新MVID的执行证据；必须待32终态/恢复、实际应用/编译及完整Aim复测后才绑定新来源。Windows22六组联机复用入口也已核清，交接在 `Windows22/CurrentSourcePreparation02/NetworkLanePreparation01/README.md`；实际包和联机结果仍不存在。

本轮实际轮询原session51794，确认fresh32仍运行；没有重启、取消或并行操作Editor。历史恢复14/14、Match4v4 4/4、OwnLanding及CohortPublicationEpoch完整类已经取得原Passed结果，目前整批110项的唯一已知失败仍为Aim。Aim Candidate02的局部落块接触观察器、应用/编译入口及仅完整Aim类的后续复测入口已完成源码准备审查；完整Aim复测入口默认实际执行一次，确认没有导入原生runner、读取活跃32结果或运行测试。尚未应用Assets或启动下一批，原Aim失败继续保留。

fresh32原XML `UnityTests/20261014-013850.xml`保持Failed：AimBallisticsPlayTests.HostRendersAllSixGuidesFiresAndFreezesHotReloadedShots在154行记录到零个被旧rig接受的地形命中。原诊断实际记录hero8的一次contact、付款身份匹配true、layer8/disabled/mapfalse/stablefalse，命中点(11.978959,0.983322,-31.800001)。生产落块射线返回刻意禁用的`_fallProxy`，旧rig只接纳enabled地图/稳定Collider，来源检查指向合法下落接触被观察器排除。正在准备仅该fixture的严格落块事件见证，尚未改Assets/Tools，未把原失败改判。当前仍沿session51794串行完成32；完整终态和恢复之后才允许应用及复测。

Root完成十份精确历史整类来源非影响证明的独立核对与标准化：six repair28的36叶加four historical的65叶，合计101原完整Passed叶；当前689源码及689metadata、100递归pin、完整源差异、原Tests闭包、原XML和冻结reuse校验已核对。收据 `Windows22/CurrentSourcePreparation02/ReuseProofsRootReviewed01/root-review-manifest.json` SHA `0a3ecd365124256f442cb37b25531592b93b843b43cecc28fb5e04e03508bb0d`。只认可原执行身份及精确非影响，不计当前重执行、完整核心、网络、性能或包通过。未修改Assets/Tools，未调用Unity/native或正式生成core收据。

UTC01:25:10，fresh31真实父批次结束，22完整类92默认叶全部Passed，runner/wrapper均0，原10文件、19逻辑/17物理偏好实际精确恢复。UTC01:25:59独立Read确认70668、当前三个MVID、非输入桌面、两项focusfalse与六idle。严格源码展开、唯一ID、原XML/raw/status/time及live恢复消费完成，Root核对全部181递归证据引用；收据 `MovingBuildDiagnostic01/regression31-terminal-consumption.json` SHA `7ccbb4da8ecf480c7c22efd34f6af1c1142184454d74ade4a3874b2ec81a9c73`，保留2Explicit未执行项和原诊断Description。随后同一原保全入口实际串行启动fresh32，42完整类145默认叶；实际session51794继续运行，当前首先重放NetworkOptimization23并覆盖新增三个完整结构类。仅当前批终态/恢复/消费结束后才形成完整核心凭据。

UTC00:55:06，fresh30真实父批次结束，29完整类398默认叶全部Passed，runner/wrapper均0；10文件、19逻辑/17物理偏好由原入口实际恢复。UTC01:14:17完成严格源码成员/参数、唯一执行ID、原始XML/raw副本、实际提交和terminal status/time核对；Root另核对全部229递归证据引用。收据 `MovingBuildDiagnostic01/regression30-terminal-consumption.json` SHA `06222d8c620ce0bea12e6e7a804ceebf8f09f1db456624a8271a2daf26dce5e5`。4个Explicit捕获项未执行，不计通过；保留fresh28时序敏感失败风险。恢复后的独立Read在下一批启动前确认70668、当前三个MVID及六idle；31运行中只核对30存档恢复证据，未重新读取其正在使用的受保护输出或偏好。

UTC00:57:30，fresh31通过同一原保全入口启动22完整类92默认叶。截至UTC01:15:31已观察12类60项通过，包含完整武器整合6项、房主2项、副武器1项、大招1项和大招完整Parity15项；当前进入重力/权威剩余类。父批次终态和恢复尚未完成，不提升整批通过。来源补审发现HistoricalScaleBoundary、PrivateLandingProofCursor各2项确实受变更影响，UnboundedBlastChunk2项的多stage落定链无法排除影响，三类完整补入最后一批；BuildQuery72/BlastOccupied11的稳定格/legacy分支正在精确来源审查，不以类名或共享文件直接推定无影响。NetworkOptimization23将完整重放以取得与其他批次一致的10文件/19/17恢复证据，原bootstrap功能认可与恢复限制保持不变。

UTC00:48:16，fresh29完整DependentEdit11项全部Passed，实际runner/wrapper均0；原XML `UnityTests/20261014-004811.xml`、成员/唯一ID/原status及10文件、19逻辑/17物理偏好精确恢复已核验，恢复后独立Read确认70668和六idle。收据 `MovingBuildDiagnostic01/regression29-terminal-consumption.json` SHA `3b0d65302b0f698265c3c1342b7e63317f5810a78aa6fb27d2f948f471925f39`。此次唯一源码增量是该类私有只读现场消息helper及381断言第三参数；逆去两处新增可逐字恢复原文件，条件/4000帧/持续负载与全部oracle不变。实际Unity编译零错误、Runtime/Config不变，新Tests MVID `eae3ce5a-fff0-47d4-9488-7a2a9c6c39df`，新图 `93666add8112074abca3b99cf5bfded1dc7607b45aea63e6876046b02bc96441`。原fresh28失败及旧Tests身份不改写，不将诊断增量称为修好了运行根因，保留时序敏感性风险。

Root已接受完整NetworkOptimization23当前功能结果，见 `bootstrap23-functional-consumption.json` SHA `9ce7f917eeea41c0f85b73b0873992aa7ed58ab5ddd18231349b8155802a1850`；保留原bootstrap恢复边界与副作用限制，不代表独立Player矩阵通过。当前私有诊断增量不影响另六完整类36项、Network23或此前窄复用闭包，均保持原执行MVID及源码非影响映射。主99/728扣除已闭合repair7/47、已接受Owned/Wire2/52及Network1/23后，剩89完整类606叶；另5类34叶条件历史复用单独记账。fresh30已实际串行启动其中无直接EnterPlayMode标记的29完整类398叶（包含UnityTest/物理/资源，不能称全纯逻辑），其后60类208叶；本批运行不算通过。首次队列检查误将5条件范围混入主队列，在原生import及测试提交前拒绝，未生成run或影响Editor；更正主选择器集合后实际启动。

UTC00:33:51，fresh28七完整类47叶全部取得真实终态：SupportRetry3/3、Fair6/6、RetryCapability3/3、PolicyTerrain4/4、PolicyContract12/12、DependentCohort8/8通过，DependentEdit11项10通过1失败。新失败为 `MovingPaidBuildRetainsOriginalContextAcrossItsRealCloneAndTwoReservationChanges`，原XML `UnityTests/20261014-003344.xml`、id1439、源码381，原Expected True/False保留。精确行号复核确认381是目标GroundCap断言，380的wrapper/clone/pending见证已通过；4000帧结束仍未发布要求的落定高度，初次未编号片段的合并行推断已纠正。实际runner/wrapper均3，10文件、19逻辑/17物理偏好及1378源码/meta完整恢复。UTC00:34:55独立Read确认70668、当前三个MVID及六idle，主线程桌面 `CodexBlockTests20261013a`、输入桌面 `Default`、Application与Game View均未获焦点。终态收据 `PublicationRepair37Application01/repair28-terminal-consumption.json` SHA `ec93c9f86f6dfba23c14b714f5feecbcf759477fdfb1e330c43f90d448c96624`。原fresh95失败保留历史记录，完整核心不提升通过。

fresh99在新Editor缺少首次Pipeline状态文件时、任何测试提交前退出，原结果与基础设施失败保留；通过原保全入口完成恢复，没有伪造状态。随后实际完整 `NetworkOptimizationTests` 23/23通过，原XML `UnityTests/20261014-002554.xml`；独立源码/XML审查确认15方法展开23默认叶、无遗漏或重复、Editor70668及当前MVID。bootstrap原保守标记保持原件，不将其称为19/17偏好及10文件完整恢复验收；该fixture存在Reports输出及网络静态注册/计数副作用。

实际修复图的有限历史复用已完成Root绑定，见 `SupportRetryLeaseRepair02/RootActualLimitedReuseDecision01.json` SHA `3bbf55243ffa6a7bfa5aff8c37a56aa79616b0439277211933ff75fb6c72f22d`。九完整历史范围106叶属于源码非影响复用，不冒称当前重执行；其中Policy12及DependentCohort8优先使用fresh28当前实际通过，不重复计数。整个BlockWorld27继续列入当前复测。

鼠标与前台保护继续使用已应用的保护33：测试模式禁止系统鼠标捕获，图形Player启动前强制核对非输入桌面，Default/Winlogon/当前输入桌面或查询失败时拒绝启动；子进程禁捕获且窗口不主动激活。上述最新Read确认当前Editor与用户工作桌面分离、两项焦点均false。本次没有重复系统鼠标采样、注入输入、激活窗口或切换桌面。

UTC00:10:22，修复后实际编译成功、错误0并完成独立Read：原PID71596、六idle、Runtime `79072e03-058a-421b-b065-fd6bab749747`、Tests `9e26bee4-8202-46e4-b09f-8e8b53ffdf46`、Config `f10e0192-24dd-4c09-b871-9bf5db4b0607`。实际编译收据 `SupportRetryLeaseRepair02/Applied02/compile-validation.json` SHA `0a8641271fe9787721ad9fa8b8d336be8a033e7a61b86f24521826d405a5e21e`。首次postcompile只读请求真实超时120秒、CLI6，原件保留；第二次Read成功，不重发测试或伪改第一次结果。

UTC00:11:20，原自有隔离Editor71596通过已审核Reports入口正常请求 `EditorApplication.Exit(0)`；返回仅证实exitScheduled，不证实进程退出码。随后两次实际OS查询确认71596及原启动器100420均不存在，UTC00:14:12另确认该项目Editor数量0；原日志在请求后完整正常收尾。原launcher没有生成exit.json，真实退出码保持null/unknown，不补写旧exit0，不把OS观察时间冒充精确退出时间。基于真实旧owner已结束、无同项目Editor，UTC00:15:08用原审核隔离launcher启动同桌面替换Editor70668（launcher68296、native session52851）；UTC00:15:45独立Read确认相同修复后三MVID、六idle、非输入桌面，1378来源/meta无漂移。实际重启记录 `EditorNaturalBoundaryRestart01/Actual01/restart.json` SHA `2f78b03b6a9a2a2be3985897ba8f86523e1c0c65e01d7afe9d4e3253b8beccb1`。旧Revision04/Consumer03完整保留，正在将旧退出回执格式改为固定实际request/OS/log且unknown code的窄Revision05/Consumer04；不降低任何玩法、结果或恢复验收。

新Editor实际启动记录域重载3195/10166毫秒、整个项目加载29.136秒；旧会话本次强制重载108907毫秒、刷新114.167秒。二者是不同阶段的诊断，不能据此宣称后续Play重载已提速；修复关联47项尚未执行，仍须实测。

UTC00:02:41，原native session68127实际退出1，父runner退出3；fresh95全部10个完整类47叶原XML已取得，46Passed/1Failed。唯一失败保持SupportRetry原id1518、第100行、原XML与失败堆栈；PolicyTerrain最后4项全通过，原XML `UnityTests/20261014-000027.xml` SHA `b9c0722649200c8b54484f9e833b4647cf33dce563862a0c6cc87c4bdb9ef091`。原10文件和19逻辑/17物理偏好精确恢复，原689源码及689metadata不变；UTC00:03:29独立Read确认原PID71596、原三个MVID与六idle。真实终态收据 `PublicationRepair37Application01/fresh-fixtures-95-terminal-consumption.json` SHA `57ac372cd59c7c39b3e1ecd0560f37044a036d0dac31960cf9f69604c6aaea76`，完整结果与退出码不改判。

UTC00:04:50，Root依据上述实际完整结果应用SupportRetryLeaseRepair02：仅 `BlockTerrain.cs`、`BlockTerrain.FairScheduling.cs` 和新增一项回归的 `BlockGravitySupportRetryWindowTests.cs`；所有原测试字节及metadata保持。原重试窗口保留直到同窗替代Pending或无工作，遇到新付费reservation时交给既有完整最新源Fair阻挡扫描，避免双方死锁；原付款/回调/守恒/碰撞与能力预算断言保留。原件已备份到 `SupportRetryLeaseRepair02/Applied02/Originals`；实际application SHA `b55f971931f901bc8c3f32432d7d741de905100f5182c6a0ef786475a49f997d`、新完整源码图 SHA `27ba4199d8ae518ddf00d3a7db103bc5adb7b4436248d909a29ab9c618b106a7`。实际编译已提交，目前尚未消费成功编译或运行修复后47项，不把源码应用算失败已关闭。

完整保守后续清单已补齐并独立复核：主99完整类728默认叶=修复7/47+原60/373+补回32/308；条件5/34单独记账。Owned/Wire52在实际绑定前不扣除，Policy12及World8准确属于修复47，整个World27另行重测。7个源Explicit不选，NetworkOptimization23优先直接重测。见 `PublicationRepair37Application01/CoreConservativeContinuationRoute01/route.json` SHA `50299d6945095b72f0c6899a304b43d4657a8fc9bb1182a97791cb2ef21ef080`；这是完整后续路线，不是通过结果。

UTC23:47:05，fresh95第九个完整类RetryCapability取得原XML `UnityTests/20261013-234705.xml`，全部3叶通过，原raw副本SHA `d5c23fc61b2f2dab1a8faa234497aaec970e696c77226a098d6a4c1c4d154fab`。完整方法/参数成员及3个唯一执行ID已经核对。当前九类43项42通过1失败；最后PolicyTerrain4仍待结果，原session68127继续真实运行，未消费父整批。

自然边界重启的两个必要入口已准备完成。Revision04和Consumer03的52项独审及Root源码准备审查已接受，Root收据 `HonestSourceRepairContinuation01/Revision04/RootPreparationReview01.json` SHA `cf4a3a87a159cbb89a71d676ba00e90ace7c4985b14e028e362af6e304ac6137`；新PID只能来自实际旧launch/exit、新隔离launch和新鲜Read，未来父执行必须晚于实际restart收据。正常退出Reports源码 `EditorNaturalBoundaryRestart01/RequestOwnedEditorExit.cs` 只允许原自有71596，在六idle、无活动请求/status、实际编译身份及隔离桌面验证后延后一秒复核再请求Exit0；25项独审及Root源码准备审查接受（Root SHA `c4bcb2dd321ac46af0bcd23baac51263d4056ad9b7f849eb0267f972ee3ec8bb`）。没有实际退出、重启、编译或运行新47项；schedule不能算实际退出，原launcher退出与原进程终态确认前禁止启动第二个Editor。当前回归不受打断。

UTC23:36:26，fresh95第八个完整类WorkerTerrainAdmission已取得原XML `UnityTests/20261013-233626.xml`，7/7通过，原raw terminal为completed/7Passed，完整类实耗1127.309秒。当前已完成八类40项，39通过1失败；UTC23:38:27开始RetryCapability，其后还有PolicyTerrain。原session68127仍真实运行，父终态/恢复/消费未完成，不把叶回调或已完成类当作整批结束，不改Assets/Tools、不并行Unity/构建/Player。

Root已接受有限历史复用的准备范围并重新核对1378源码/meta及250个引用文件身份，见 `SupportRetryLeaseRepair02/RootLimitedReusePreparationReview01.json`（SHA `af0619b9b009ad2297f721d4e0c796818b8f427c3e6b33e551e6341230f67098`）。纯规则/LAN46与本地模型60均保留原完整历史叶、执行身份和父失败；实际三文件应用/新编译图仍未绑定，尚未接受未来复用。Policy12/World8在修复47中优先使用实际新结果；Owned6/Wire46仅在实际绑定接受后可从后续保守路线移出52项。整个BlockWorld27仍须复测，不以子集替代。此准备不算当前重测、完整核心或Goal通过。

fresh95当前已取得七个完整类的原XML：MotionCopy2/2、CohortIntegration2/2、BindingFutureMotion5/5、SupportRetryWindow1/2、BuildQuotaDoneScalar2/2、BuildQuotaDonePublication4/4、TerrainPublication16/16，合计33项32通过1失败。TerrainPublication原完整XML `UnityTests/20261013-231544.xml`、原raw terminal completed/16Passed及全部16个唯一执行ID已只读核对；第七类耗时2500.304秒，尚不消费父整批。SupportRetry原XML `UnityTests/20261013-222016.xml` 保持Failed；具体失败为第100行第一份替代Pending没有沿用已拒绝事务的同一个BlockPublicationWindow，第89行重试所有者同一窗口检查已走过。当前整批仍在原session68127运行，已经进入WorkerTerrainAdmission7项，其后还有RetryCapability3/PolicyTerrain4；父终态、完整恢复及消费未完成，不消费整组通过、不启动96。

单文件候选01已被独审拒绝并完整保留：仅保留gravity-only窗口时，新付费拆除的同列reservation会阻挡重力，旧窗口又不允许该攻击，可能双方卡死。候选02在两个Runtime文件中保留原窗口并将其精确交给既有Fair完整最新源扫描；只有真实阻挡证明完成，才给当前FIFO头独立付费窗口。新增第三真实FIFO测试复用320格塔和原生支撑拒绝，要求扫描、收费/回调、318格逐地址守恒与原生碰撞；原两个方法及所有共享字节保持。Runtime独审、新测试30项离线检查以及Root三文件源码审查均通过，记录于 `SupportRetryLeaseRepair02/IndependentReview01`、`SupportRetryLeaseRepair01/NewQueueRegressionCandidate01/IndependentReviewSenderTrace01` 和 `SupportRetryLeaseRepair02/RootSourceReview01`。目前仅Reports准备，尚未应用、编译或实测；应用须等整95实际终态、完整失败结果消费、精确恢复和新独立Read。Root复核689源码和689 metadata全部无漂移，不把源码审查算运行修复完成。

三文件应用入口最终版及其27项独审已完成，Root准备审查接受；修复后独立回归入口Revision03的51项独审及Root准备审查也已完成。它先运行完整SupportRetry3项，再运行Fair/Retry/Policy/Dependent等六个完整类，共47默认叶；旧95真实失败继续原样保留，旧96“95Passed”前置不伪造。实际应用、新完整源码图、新Runtime/Tests编译身份均仍未绑定，入口拒绝提前执行。实际编译消费者的19项独审及Root准备审查已经接受；七类47叶结果消费者的32项独审及Root完整源码准备审查也已接受，分别见 `SupportRetryLeaseRepair02/ActualCompileConsumerPreparation01` 和 `HonestSourceRepairContinuation01/TerminalConsumerPreparation01/RootPreparationReview01.json`。这些是准备审查，不是实际编译或回归通过。本轮没有应用源码、启动新的Unity/native或并发回归。仍有效的实际postrestore/postcompile空闲Read允许直接复用，避免因只读记录收据而重复原生观察。

布局提速候选的只读Inspect经独审未通过：安装版UnityEditor DLL离线IL证实两个getter可调整Editor内部选中尺寸及窗口列表，不能认定为严格只读。候选保留原样，不调用，不关闭窗口或改共享偏好；该可选优化不阻断功能回归。Motor三个完整类23项历史资源闭包缺证，历史通过保留但不提升当前来源，列入当前复测路线。独审记录分别为 `IsolatedLayoutCandidate01/IndependentReview01` 和 `RemoteStableMotorReuseReview01`。

鼠标保护当前复核37：UTC23:04:44–23:04:54真实只读采样100次，观察线程和活动输入桌面均为Default，鼠标受限、测试Editor抢前台及读取失败均0，实际观察session5507退出0。保护33运行代码及两个Tools身份未变，fresh95仍沿原隔离入口运行；Runtime及启动工具也经Root和独立只读审查确认。没有激活窗口、切桌面或注入输入。结论仅覆盖该10秒窗口，后续仍强制使用非输入桌面和自动禁捕获。[复核记录](FrontendInputProtection33/CurrentRequest37/verification.md)。

fresh93完整Terrain11项均为Passed，原XML `UnityTests/20261013-210052.xml`；实际wrapper与runner均exit0，源码/meta不变并精确恢复现场。独立Read UTC21:03:40确认同PID71596/非输入桌面及六idle，当前2bb源码图与实际编译769/MVID保持。终态收据 `PublicationRepair37Application01/fresh-fixtures-93-terminal-consumption.json` SHA `18a367b6f74c9b4b7d22f37c7b9717eaae94f7d58875047381fbeb994e82cc6d`，没有遗漏、跳过或重复成员。原fresh92的11个SetUp失败保留。

fresh94已真实终态：OwnedCohortEdit6、World27、CohortWorldWire46、GravityOwnLandingActivation3、GravityCohortPublicationEpoch2、HistoricalRecovery14，共六完整类98/98通过，实际native session60258退出0。原XML、完整源码成员、参数案例及runner0全部核验，10文件及19逻辑/17物理偏好精确恢复，源码/meta未变。UTC21:54:08独立Read再次确认同PID71596、Runtime779/Tests495/Configf10、非输入桌面与六idle。终态收据 `PublicationRepair37Application01/fresh-fixtures-94-terminal-consumption.json` SHA `718baee4cc67889d9490d9d24a6d7cc16d1adf51d916a1cbe485963ad0d36b9e`。

Windows22四份新包模板已通过Root及独立静态审核（RootReview SHA82a7f476），实际core receipt、最终idle、正式构建与包身份仍缺。fresh92 World8/Fair6的严格来源扩展也已Root消费（SHA07daabd1）：仅另一Terrain测试类的私有Setup等待变化，原Tests441执行身份、父runner3/wrapper1及11失败全部保留；World本地模型反射写入的文案纠正随审查引用，不冒称Tests495重新执行。本轮未启动包或提升完整核心验收。

fresh95十完整类47项在fresh94真实终态/恢复/独立Read/消费之后串行启动，实际native session68127仍live。第一类MotionCopy原XML已有2/2通过；整组尚未终态，不能计整组通过。入口已通过Root及独立源码审查，见 `PublicationRepair37Application01/next-impact-batch95.md`；实际未传`--full-assembly`，每类仅运行其完整成员。运行期间不改Assets/Tools、不发并行Editor/构建/Player命令。

针对用户鼠标反馈，UTC21:48:21–21:48:31完成新的10秒/100次只读采样，观察线程桌面与活动输入桌面均为Default；鼠标受限、测试Editor抢前台及读取失败均0。项目Editor实际带`-automated`，两子进程为AssetImportWorker，没有InkLan Player。采样结论仅覆盖该窗口，后台隔离和启动失败即拒绝的保护持续使用，详见 `FrontendInputProtection33/CurrentRequest35/verification.md`。此前CurrentRequest34的100样本原件保留。没有写系统鼠标、模拟输入、激活窗口或切桌面。

五个完整纯逻辑类的36项历史通过证据已完成Root及独立审查，并接受有限源码非影响复用：WorkerQueue7、WorkerQueueIdle2、PublicationPolicy12、FrameBudget12、Transfer3。核对真实调用闭包、122个来源引用、当前1378源/meta和20源/7meta差异；Policy只触及变化World文件中的四个不变静态几何声明，未进入世界实例或地形提交。原全库1783项/37失败/runner1和Transfer74历史身份均保留，不冒称当前重测或完整核心通过。收据 `PublicationRepair37Application01/PureReuseProof01/RootReview01.json` SHA `44fa2a91a509de0daef8bcd44dba6cf15589e17547aeb40b94c4a9f44c73f6c4`。

LanDiscovery完整类10项历史通过也已完成Root及独立严格来源审查，接受有限源码非影响复用。完整fixture/协议/目录/私有UDP/MatchPhase闭包未进入场景、配置、资源或地形；原fresh78全部10叶ID、参数、结果、历史MVID及恢复保留，当前1378源/meta和两段差异核验。Root收据 `PublicationRepair37Application01/SyntheticReuseProof01/RootReview01.json` SHA `08254800ff5addc6ecac06213360015ab7346491c671d24abfc7cd34eb591492`。RealUdp只保留历史电脑执行结论，不证明当前网络条件；AirSwim24、SplooshSummer9、LanRoomCode15因历史资源/字体闭包缺证保持未接受，不删除叶子。

原98个核心选择器已映射到91个完整类并逐项区分原默认/Explicit范围，后续按真实变更调用链决定复测或严格来源扩展，见 `PublicationRepair37Application01/CoreRemainingImpact01/remaining-impact-plan.md`。fresh95的10类47项消费者已通过独立及Root准备审核，待真实终态后消费；其FilesAfter准确保留恢复前测试输出，另核对实际恢复后的原10文件及19/17偏好。付费战斗fresh96的7类27项入口默认仅describe，必须绑定fresh95真实通过收据才允许执行；未提前启动fresh96或Windows22包。

V5完整性能入口已准备并经Root及独立只读审核：六场/八人/D3D12/原采样时长和判定保持，当前三Tools pins与包identity.acceptanceTools及磁盘字节三方绑定；真实旧Windows21身份明确拒绝。候选manifest SHA `8deda1f52a7ab94737508b2343d6ab0a6a4e9a6eae69a80af0f9e01ed87ba036`，离线验证8项身份/漂移拒绝及默认describe均成功，没有实际新包materialize或启动Player。[准备与来源](FrontendInputProtection33/NextLaneCandidate01/README.md)。此项不计性能或包通过。

后台测试鼠标保护33已实际补入两个通用验收工具：场景入口和每次图形Player创建前核对真实非输入桌面，拒绝在Default/Winlogon/当前输入桌面或查询失败时启动；子进程显式禁捕获，Player加`-noSystemCursor`，窗口不主动激活。当前Editor只读确认隔离桌面、`CanCaptureSystemCursor=false`、Cursor.None且visible；没有启动新Player或回归，也没有写鼠标、输入、前台或桌面。18项离线检查和真实桌面拒绝均exit0。[原件及修改边界](FrontendInputProtection33/verification.md)。新Tools SHA必须在下一次同源包及适配器中重新绑定；保留旧签名原件，不使用旧V4入口直接运行修改后的工具。

以下为fresh93结束前的历史快照：fresh92真正终态已消费，Passed14/Failed11，原Terrain失败完整保留，10文件及19偏好完整恢复。收据 `PublicationRepair37Application01/fresh-fixtures-92-terminal-consumption.json` SHA `d47931ac3cdd15fd7ae81fa51c40fa1ccd08b8c7274865f69d36554fb34ed900`。Bootstrap等待真实初始化的单文件修复已应用；实际编译completed/failed=false/errors=[]，Runtime779/Tests495/Configf10，当前完整源码图 `2bbacd9a1733f155e125a86409667673e24e6b87ba97b218e84144ca4e800027`。编译收据 `Dependent92ReadOnlyAudit01/BootstrapWaitCandidate01/Applied01/compile-validation.json` SHA `76930582bac231d569481c860b02ac33b46d163f7f851257b967829213fb1c33`。已通过原串行保全入口启动完整11例Terrain类fresh93，实际session87254当时仍运行；1800秒仅增加外层观察时间，不改600000ms测试时限、4000帧Pump或oracle。当前真实终态以上方最新记录为准。

以下为fresh92终态之前的历史记录，保留当时来源和失败：

Root已实际读取原 `UnityTests/20261013-200457.xml`（SHA `8ed952f6a01abbd463979eb747c3642428b10a2112b52eeb9f5d85e24cb324b2`），完整Terrain类11叶均Failed且failure/message完全相同：SetUp的Unhandled log message，invalid operation handle；原失败保持，不计业务断言通过。源码链为BeforeSceneLoad自动Bootstrap异步加载武器时，新fixture的HeroMigrationTests.Load/InstallWeapons释放仍被等待的Addressables句柄。`Dependent92ReadOnlyAudit01/BootstrapWaitCandidate01`只在注入前等待真实App初始化成功，原11测试体、600000ms时限、4000帧Pump及失败清理不变，尚未应用。原session84349真实poll仍live，runner已串行进入自然Fair完整类；不在活动测试中写Assets、发编译/构建命令或取消运行。

单文件 `BootstrapWaitCandidate01/apply-bootstrap-wait.py`已准备，SHA `5176f0156cf2f985e88db11734398f692431be26e1509c98ecda05a9cf2dae5f`；实际默认离线校验exit0，Root已全文审阅：候选/原字节、完整7f562图、实际编译receipt/MVID及三个精确hunk匹配，teardown以后全部原字节不变。应用仍要求Root实际确认session84349退出、完整父终态消费和恢复、之后的新鲜独立idle Read，并保存原件和精确单文件差异；默认验证不证明应用资格。没有应用、编译或复测候选。

只读抽查fresh92三次域重载为61.651/72.130/72.939秒；对应刷新均零导入、零文件变化、编译约1ms。主要成本在FinalizeReload及Editor初始化（SetupLoadedEditorAssemblies约36.7—43.4秒，其中InitializeOnLoad类初始化约21.8—25.8秒），以及AwakeInstances约14.4—17.0秒；并非主要耗在实际编译。现有日志无逐回调计时，不能归责某个脚本或Pipeline。后续仅在真正idle时定位具体初始化热点，保留域重载语义，不打断活动测试。

Sender诊断应用helper的独立审查发现中途写入新C#但缺meta时，原错误收据会因严格graph再次抛错而丢失。冻结01保持，新v2 `SenderTraceApplicationPreparation23/apply-sender02-v2.py`仅修失败路径的best-effort快照与attemptedFiles/currentTarget；真实四个离线异常/正常fixture通过，独立 `SenderTraceApplicationReview23/review02.md`确认严格守卫、正常六文件delta及备份语义不变。v2 SHA `ec40ceac0e307bf412e635bbd64efbed159f7672b888f47393a3baa8f0507aca`；未应用Sender源码、未运行Unity，不计网络通过。

FreshPreparation01完整所有者与FreshPreparation02退出清理叠层已精确应用14处源码/meta差异，保留全部19新测试的原断言、负载、时限及原1024/.25ms、4096/.75ms预算。首次实际编译发现一处NUnit负成员查询只提供string重载，随后仅等价修改该断言表达式；原编译失败与一次HTTP400状态查询均保留。最终实际编译completed/failed=false/errors=[]，UTC19:42:45独立Read确认Runtime `779cd625-c279-4e4a-a68d-a987d901ba7e`、Tests `441f300a-a7a7-4b86-a8e4-bf6c61ddaa99`、同PID71596/非输入桌面/六idle。原件与编译收据位于 `PublicationRepair37Application01/FreshDependentApplication02`，当前完整图SHA `7f562dc3aefb2345547b6f1fadff6e12e8d39245f8de1ea924581ae08f9a2217`。已通过串行保全入口启动fresh92的两个新完整fixture与自然Fair完整fixture，工具session84349。原XML `UnityTests/20261013-194504.xml`已独立读取，新World完整类8/8通过；UTC19:50:58仍在Terrain类，运行中不计其余11新测试或Fair通过，未重建包、取消或重启测试。

只读网络纠正审计已冻结于 `NetworkCorrectionAudit22/review.md`：49个超标ACK组中37次地形pending解除、12次输入超时，准确交集6，另有6次两者皆非；31条唯一客户端链齐包P95为2613.8ms、本地发布P95为75.3ms，不能单独归责发送16片上限。SenderTraceCandidate02已准备未应用，新增终态flush成功/失败日志以修复01私有写错误不可观察问题；保持原网络预算、ACK、输入与验收oracle。诊断候选不计联机通过。

最新鼠标复核15在同一 fresh91/session13449 回归运行期间完成：UTC19:03:45—19:04:45，原工具session32068实际exit0，597次读取成功，系统鼠标限制0次、测试Editor抢用户前台0次，结论仅覆盖该分钟。当前自动测试禁捕获、失焦释放和非输入桌面串行入口再次经独立源码审查确认；没有操作真实输入、焦点、桌面或取消测试。V4采样前实际后缓冲观测候选已通过独立离线安全审查，未应用/编译/构建/运行，不计分辨率或性能通过。

上述V4候选随后已精确应用，并修正新诊断测试的Play域重载闭包初始化：仅四处源码/meta差异，原断言/攻击规模/超时保持，原fresh91失败保持。Unity实际编译completed/failed=false/errors=[]；UTC19:18:56独立Read确认Runtime `9b0fa5d3-7d3d-4a45-8217-f66fb0512d2e`、Tests `657ab62b-70a2-4a28-ba05-4117e551071d`、Config保持f10，同PID71596/隔离桌面与六idle。原件 `PublicationRepair37Application01/DiagnosticAndBackbufferRepair01`，编译收据 `compile-validation.json`。尚未复测新诊断、重建包或实际验证V4后缓冲；后续包装仍须原六场门通过。

最新37已实际应用13个精确文件差异：私有且无准备证明的积木编辑省去重复依赖遍历；新增原终态位置的撤销原因和原每秒快照中的既有诊断计数。没有提高预算、改FIFO/攻击规模或改变建造/拆除规则。Unity实际编译completed/failed=false/errors=[]，Runtime `db465c99-8638-46d6-a72d-2aa2b23bf8ae`。独立审查修正了“历史恢复取消也被标为冲突”的诊断错误，原候选保持。首次共享编辑124段的超预算尚未修复，正在准备完整事务所有者候选，不能当作已解决联机排空。

fresh89原批已实际exit1/runner3：三个完整类79例78通过1失败；新Owned编辑6/6、原BlockWorld27/27通过，WorldWire45/46，唯一失败为原测试固定旧费用3而新私有编辑费用1。原失败/XML/源码来源和完整恢复保持，独立终态消费 `PublicationRepair37Application01/fresh-fixtures-89-terminal-consumption.json` 已生成。随后仅更新该测试费用预期，所有原对象隔离、Cells COW及wire断言保持；实际编译再次通过，Tests `647aa968-84a9-48df-911f-a42007eaa64e`，Runtime不变。

fresh90已实际exit1但runnerAttempted=false：后台接口只读loaded预检30秒超时，未隔离偏好或提交测试。随后独立Read确认同PID71596、隔离桌面及六idle；没有将观察超时当成活动测试取消。fresh91原工具session13449已实际exit1/runner3且完整恢复；三个完整类54例53通过1失败：WorldWire46/46、自然Fair原五例通过，新八次预约诊断在局部闭包初始化处NullReferenceException，GravityCohort集成2/2通过。独立UTC19:12:56 Read同来源、隔离桌面及六idle；完整原XML、恢复和来源已由 `fresh-fixtures-91-terminal-consumption.json` 消费，targetedFixturesPassed=false保留。随后仅改诊断wrapper/inner初始化顺序，全部原断言/负载/超时保持，尚未复测。当前Windows21可玩包仍保持最终36来源原字节，未重新构建；新37来源核心闭合、联机和性能还需实际通过。

完整性能V3于UTC18:21:22真实结束，原工具session23655实际exit1、native/harness/bootstrap/guard均5。六场执行，五场取得实际1920×1080正常主相机与八人完整时长采样：steady250k帧时P95/P99为18.841/28.870ms、普通响应P95为21633.333ms；combat250k为36.502/62.909ms、普通响应8016.667ms；tower32m为26.623/36.613ms；cascade20k为14.936/21.312ms，以上原判失败保留。blast8原case passed=true、hostExit0，八次完整爆破在309.795ms完成，帧时11.253/15.302ms通过，61.225ms偶发长帧列首版风险。scattered100k后缓冲回到1024×768，采样0且无截图，不计通过。高塔和级联守恒/最终碰撞扫描通过，但整体性能门仍未通过；不得将summary的六结果齐备解释为六场有效采样。

V3全部48个自有Player已有终态，偏好restored/rawEqual/branchExistenceEqual=true、pending解除；503次窗口观察中500次见精确包Player，读取错误0，出现在Default输入桌面0。源码/Tools/完整359文件包未变，完整VerifyOnly实际0。V3撤销重试停滞已留证，具体撤销原因尚未证明；新增Reports候选尚未应用或执行，不能当作修复通过。

最新Network1080Dependencies21/DiagnosticRun01于UTC18:27:17真实结束，原工具session92379实际exit1、native/harness/bootstrap/guard均4；仅八人100ms/1%一组诊断，不替代六组矩阵。全部8人迟加入检查点精确一致（round0/revision102、pink2048/blue1536、32区块及全部哈希一致）；35秒穿行后，原45秒静稳排空等待超时，冻结/重连/清空/重建检查点未到达。1818命令最终全部完成且失败事务0，待检列升至8992、末样本pending1/falling365；最终各人的ACK达到原门槛，不能把通用错误文本误解释为仍缺ACK。七客户端纠正P95均达标，P99为0.730—2.136米，全部超过0.60米门限。实际8人后缓冲均1920×1080；依赖sidecar各有一条末尾未组装完的增量，captureComplete=false，不宣称完整恢复证据。

该诊断8个自有Player及proxy均exit0，偏好原始值与枝存在状态完整恢复、pending已无，92次窗口审计读取错误0、测试Player在输入桌面0；包前后全量359文件一致。本次随后VerifyOnly再次实际0。最新鼠标复核14无新Player运行，60秒598次读数成功、鼠标限制及Editor抢前台0；负载中观察13同样未见限制或Editor/Player抢前台。防护持续启用，详见 `FrontendInputProtection32/verification.md`。Goal保持active，未提交或发布。

以下为上述终态之前的历史进展；其中“仍在运行”描述仅指记录当时。

UTC18:04:21已串行启动完整性能V3，原工具session23655、bootstrap75568，输出 `IsolatedAcceptance21V3/PrototypeV1/Performance`。沿用原六场/时长/八人/正常D3D12主相机/首版门，只增加已验证的自有host不激活尺寸适配，Assets、Tools及包不变。当前原工具仍live，不宣称完整六场终态。

第一场steady250k已完成180.02567秒/15199样本，原case passed=false、hostExit5保留。真实normal1080pMainCamera、八人、完整规模及计时门通过；原1920×1080主相机截图Root已实际阅图，中文材料槽与双方积木数量可读。frameP95=18.84110ms、P99=28.87040ms及普通发布P95=21633.33332ms超门限，均仍失败，不以尺寸修复提升性能通过。后续完整场次由同一原串行器继续。

同期鼠标保护观察13工具33735实际exit0，60秒582次范围/前台/精确包Player归属读取全部成功，限制鼠标0、测试Editor及Player抢用户前台均0；仅覆盖本次采样窗口。只读审计未发现当前V3禁捕获环境继承或非输入桌面保护缺口，没有重复修改Assets、包或取消运行。详见 `FrontendInputProtection32/verification.md`。

UTC17:58:18后台尺寸诊断已成功完成：工具69383/native/bootstrap/guard实际exit0，独立非输入桌面上的精确自有host第一次不激活窗口尺寸请求仍被夹到1024×768，第二次增加SWP_NOSENDCHANGING后客户区稳定为1920×1080；之后原八人tower32m fixture真实运行10.0015秒/794帧。原normal1080pMainCamera gate通过，原main-camera.png为1920×1080且Root实际阅图确认中文材料槽/队伍积木总量HUD。原case诊断passed=false和hostExit5保留，短时长/帧分位/未全部落定失败不升为正式通过。

本次仅修改自己新建的后台Player窗口大小，未激活、移位、改变Z序或系统显示模式，未操作鼠标/焦点或切换桌面。22次窗口审计中20次见精确Player、读取错误0、Player在Default输入桌面0；8个自有Player均终态，偏好完整raw恢复、pending解除。包前后全部359文件一致，后续VerifyOnly实际0。新完整六场V3性能入口正在准备，只增加同一尺寸适配，不改原采样时间/目标/后缓冲/判定或旧失败报告；Goal保持active。

UTC17:47:15完整D3D12性能六场已自然终态：原工具74663实际exit1，native/harness/bootstrap/guard均退出5；六场均因正常主相机后缓冲为1024×768而非1920×1080在采样开始时中止，samples=0，全部未通过，不能据此给出帧率或地形响应结论。没有正常1080p主相机截图可供中文HUD验收。偏好restored/rawEqual/branchExistenceEqual=true；113次窗口审计、111次见精确包Player、读取错误0、Player在Default输入桌面0。旧失败原件保持。下一步仅准备不激活的后台自有Player窗口尺寸诊断，维持同包、正常backbuffer和原数值门，不调整用户桌面显示模式、不切桌面、不操作真实输入；诊断成功也不能代替完整性能复测。

UTC17:39:52最新网络矩阵已自然终态：原工具87160实际exit1，native/harness/bootstrap/guard退出4；6/6已执行，0通过。三组双人在完整战斗后排空超时；八人均在迟加入精确哈希等待超时，client-7实际退出分别为1/0/3。后续冻结、重连、清空、重建缺证不计通过。实际Player尺寸1024×768与请求1920×1080不符，现有checker仍将此判为失败，保留原判。首例已520/520完成且总格数8343恒定，但1260待检列/266下落格未排空；第二例557排队/163完成，普通建造队头已69.45秒。未确认无限循环；正在用源码定位积压原因，不延长超时或改变规则。

网络所有30个自有Player均有真实退出，偏好restored/rawEqual/branchExistenceEqual=true；341次窗口审计，306次见精确包Player，读取错误0、Player在Default输入桌面0。原失败退出码与偏好恢复成功分别保留。结束后完整同包VerifyOnly实际exit0，identity SHA不变；未修改Assets或工具。

自然比赛的独立离线复核实际exit0，12项原流程检查全部true、同完整包及偏好恢复/窗口审计一致；原packageSmokePassed=false、中文HUD/清场未覆盖的false标志原样保留。原件为 `Windows21/FinalSourceCandidate02/RootContinuation36Terminal01/smoke-consumption21v2.stdout.json`。

UTC17:43:24已串行启动同包完整六场D3D12性能验收：原工具74663、bootstrap64520，沿用CodexBlockTests20261013a非输入桌面、禁系统鼠标捕获变量，不改变用户Default桌面/焦点/分辨率。当前运行中不计性能通过。七类中功能已完成，余六类包级门仍待通过；Goal保持active。

以下为此前时点的历史记录，运行状态和等待事项以本节上方最新终态为准。

完整包自然比赛流程已通过（UTC17:26:28）：观察开局到结算180.2119秒、实际积木计分、返回房间并开始下一轮、双方自然exit0；原summary的`naturalRoundWorkflowObservedPassed=true`，刻意保留`packageSmokePassed=false`及未在此harness验证的中文HUD/清空oracle，等待同包网络与主相机补齐。原工具86398实际exit0，native/bootstrap0；19逻辑偏好原始值和枝存在状态准确恢复，已无pending快照；窗口130次审计中129次见精确Player、读取错误0、出现在输入桌面0，其他系统窗口无法分类次数原样记录为诊断。完成后V2 VerifyOnly再次实际exit0、全量包身份SHA不变。

UTC17:28:18已串行启动同包六组联机，工具87160、bootstrap74920，仍在CodexBlockTests20261013a/Default隔离和禁锁鼠标环境中。联机运行中不计通过；尚未启动性能测试。当前七类中功能已完成，剩六类包级门待通过；Windows包构建与自然比赛部分已经完成。

Windows21正式Addressables和Windows包已真实构建完成（UTC17:20:37），完整包清单及构建记录绑定最终36源码和核心收据。V2包身份检查实际exit0，identity SHA `c238c5ab01df16f91b764b16a50231c980e77392f425dee10a9fa223f84ae39f`；postbuild Read六idle。已启动同一包自然180秒比赛，工具86398仍运行、无并行Unity任务；包级比赛完成、联机及性能尚未通过。Player禁锁鼠标环境已继承，窗口在非输入桌面；运行期间观察11实际exit0，598次系统鼠标范围读取成功且限制0次。

最终V4V02 prepare/check/root-reviewed-copy及新发布器check均实际exit0，Root reviewed receipt complete=true、pending=[]、coreMissing=[]：23普通运行、95个选中整行、原86独立五整类19例、98选择器、62复用证明和163基线类审查，原37失败及额外失败全部闭合。新发布器仅在本地独占写核心收据和构建前提，实际exit0：`Windows21/FinalSourceCandidate02/core-test-receipt.json` SHA `ffa9aab86747c1bb6e26ef9a8cb011a08f60d707cb3ad66bfe357dd55e71dfb7`，核心门true；Goal/product/package门仍false。原86整批失败及首轮V4复制失败全部保留，没有修改Assets、重跑测试或放宽批准标准。

已使用冻结V2 recorder向既有隔离Editor提交正式Addressables+Windows64 MonoDevelopment包构建，记录名 `queue-final36-firstplayable01`，工具session72895。等待真实构建记录、包清单和身份核验，排队或构建中不算包通过。后续同一包自然180秒比赛、六组联机、六场性能依次执行；仍固定非输入桌面、禁止系统光标捕获和原生输入操作。

V4离线prepare和check-prepared真实exit0，但首轮root-reviewed-copy真实exit1，原失败目录保持。原封V3诊断确认只缺尚未合并的原86五个整类及其中两个方法选择器；V4合并前错误地只允许整类名称，拒绝了真实完整XML中已Passed的两个方法。原回归和原件有效，无需重新执行Unity。正在准备独占新V4v02验证入口，只修原86已核验整类与真实叶的选择器资格判定；不降低测试标准、不改Assets、不覆盖首轮失败，核心与包验收尚未标记通过。

UTC17:02最新：fresh87 Match4v4完整四例全部Passed，含自然180秒胜利、平局及回到热身清空；十类17/17、实际adapter/runner0，10文件和19逻辑/17物理偏好恢复完成，Read/消费0。原串行session2359实际exit0的CommandExecution事件已保存至 `Windows21/FinalSourceCandidate02/RootContinuation36Terminal01/actual-parent-command-terminal-event.json`，原fresh86整批失败未更改。最终独立Read UTC17:02:06仍为最终三MVID、非输入桌面及六idle；fresh88/87独立执行审计实际0，两批真实父终态、成员、原件和恢复全部绑定通过。V4 prepare正在离线执行，尚未生成核心收据或启动构建。

本次鼠标保护观察10（UTC16:57:28—16:58:28）实际exit0：598次读取成功、鼠标限制0次、测试Editor抢前台0次，仅覆盖此分钟。独立源码审查未发现固定后台路径的捕获缺口。后续Editor/构建/Player仍串行、固定非输入桌面及禁捕获环境；不操作真实鼠标、焦点或桌面。

UTC16:43最新：RescueBubble、PlayerName完整各1/1Passed，Root读取原XML20261013-163613/164039真实叶通过；本批九类13/17Passed，只剩Match4v4四例运行中（含自然180秒比赛）。唯一session2359真实poll仍live，整批恢复/Read/消费及parent终态尚未获得，未进入构建。后台鼠标与前台隔离保护继续保持。

UTC16:33最新：fresh87 SwimPlay完整3/3Passed，Root读取原XML20261013-163310三叶全部通过；本批七类11/17Passed，正在RescueBubblePlay，随后PlayerName及Match4v4四例，共剩6例。session2359真实poll仍live，未获得整批恢复/Read/消费或parent终态。交付两文档及Reports-only性能边界诊断已更新/只读验证，尚未生成核心收据或构建正式包。

UTC16:17补充：session2359最近真实poll仍live；fresh87已观察到GameplayUpdate1/1、HeroChangeZone3/3、HeroMigration1/1，共5项Passed，剩余七类仍由原串行器继续。完整回归批次及parent终态尚未通过。Root已全文审阅冻结publisher候选fcf0714b，并实际只读`--describe` exit0核对依赖pin；尚未生成核心收据或构建。没有新的Assets变化、重试、取消、输入或前台操作。

Root已真实运行late86固定验证器107e9ba7，`FallingSphereSweepDesign34/RootLate86Inspection01/inspect.command.json`实际returncode0，五整类19例scopeComplete=true、wholeRunPassed=false、errors=[]；独立静态审阅SHA1a66a2ad确认原86的失败/超时及恢复绑定。UTC15:41:50独立Editor Read同最终MVID、隔离桌面及六idle。

UTC15:49:33已真实启动冻结串行接续入口91f9，外层工具session2359当前live，证据目录`Windows21/FinalSourceCandidate02/PartialLate86Final36Preparation01/SerialContinuationRuns/continuation36-01`。原Paid84/Pistol83和fresh85前提核验均真实exit0；fresh88尾五完整类5/5Passed，实际adapter/runner exit0，十文件及19逻辑/17物理偏好恢复，source/meta不变。UTC16:03:40独立Read与完整消费均真实exit0，同最终三MVID、隔离桌面、六idle，完整消费errors=[]。UTC16:03:41同owner已启动fresh87十类17例，实际adapter PID58008；运行中不计通过。没有另起Unity/Player操作，不重跑已完成19例，不改Assets或测试timeout/assertion，自动测试禁锁鼠标及隔离桌面保护保持。

最新鼠标请求已在fresh87运行期间完成观察09（UTC16:08:27—16:09:27）：598次成功读取、鼠标限制0次、测试Editor抢前台0次。源码及隔离入口独立只读核对通过；没有输入、焦点、桌面写入。V4独立静态审查记录e4d88715已冻结，无新增静态阻断；最终prepare/copy、真实核心收据及Windows21构建仍待完整终态，Goal保持active。

fresh85原外层工具句柄7814已真实exit0，完整12类、18/18Passed，原runner0、十文件和19项偏好准确恢复，source/meta保持最终DiagnosticRepair02。第一次默认沙盒独立Read未找到Pipeline，失败原件 `postrestore85-01.*` 保留，没有重跑测试或重启Editor；随后完整本机只读访问 `postrestore85-02.stdout` 成功，UTC14:34:54.6219523确认同PID71596、05bc/8b70/f10三MVID、非输入桌面及六项idle。Root已实际运行完整固定成员消费器，[终态核验](FallingSphereSweepDesign34/fresh85-terminal-consumption01.json) 为integrity/pass全部true，12类18个原XML叶全部Passed、无遗漏或跳过。

`serial36-final-two-batches.py` 原外层16051已真实exit1，adapter PID87088真实exit4、原runner1；原件在 `FallingSphereSweepDesign34/SerialFinalTwoBatchRuns/serial36-final-two-01`。fresh86前四完整类各1/1通过，SpecialWeaponParityPlay观察器1200秒超时后原测试继续，最终原XML `20261013-150923.xml` 完整15/15Passed。Root已在新 `FallingSphereSweepDesign34/Late86TerminalRecovery01` 保存其exact15成员、原launch、原XML/status字节与原超时/恢复前manifest。旧fresh86整批10类24例保持incomplete/观察失败，不补造runner0或完整批通过；尾五类尚未提交，fresh87也从未启动。

Root先独立Read确认六idle，再使用原扩展保全器 `--restore-from`（不重跑）真实exit0恢复10文件和19逻辑/17物理偏好；恢复后Read UTC15:20:38确认同PID71596、CodexBlockTests20261013a/Default、05bc/8b70/f10三MVID及六idle，晚于recoveryFinishedUtc15:20:16。原runner1/restorationError和parent失败保留。独立审阅初核原五完整类19例及恢复/身份成立，完整来源闭合仍待新V4审查。剩余调度为新fresh88仅原batch3尾五完整类5例，再fresh87原batch4十完整类17例；Reports候选正在准备，尚未执行，不重新执行已完成的19例、不改原timeout或断言。

原最终36准备633c、62份整行证明候选、163类影响建议及三批成功复制入口均保留冻结；原v01实际退出凭证缺口已由v02补齐并经独立静态审阅，但它不能接受当前failed86/未启动87。新 `PartialLate86Final36Preparation01` 正准备固定此次late-recovery验证、尾五/最后十类调度及V4闭合入口，明确区分原observer失败与五完整类实际Passed。原XML/MVID、helper/source/meta、原37失败及全部失败历史保持；尚未生成新核心收据、构建包或宣称七类首版门通过。

本次鼠标反馈已完成最新60秒只读观察08，UTC15:34:19—15:35:19，598次鼠标范围/前台归属读取全部成功，鼠标限制0、测试Editor成为前台0（仅此采样窗口；本轮没有启动测试或Player）。独立静态核对确认三份Application29保护源码和Windows21 V2隔离入口SHA未变；自动测试禁系统鼠标捕获、非输入桌面、普通游戏失焦释放且不自动重锁保护仍在当前源码中。未重复修改Assets、取消/重启测试或发送输入/焦点/桌面操作。此前观察07原件保留；完整证据见 [后台鼠标现场复核](FrontendInputProtection32/verification.md)。

fresh80完整Pistol15/15通过、fresh81完整比赛方法1/1通过，原runner均exit0，源码/meta未变、文件和19项偏好恢复。fresh80观察器在EnterPlay域重载时连接中断，未得到接触日志；诊断不完整，不宣称动态原因已证实。此前fresh79完整失败原件保留。[fresh80报告](PistolFixedAimContactObserver80/verified-result.md)。

当前Runtime实际诊断确认独立几何缺口：下落积木扩张AABB误报球体边角命中，真实命中点在积木外、TryContact失败。Applied35已精确修改逐cell窄相位并新增真实发布接触测试，实际Unity编译无错误；Runtime `2f13ea8c-a5c0-4b57-b34a-6d1b61829563`、Tests `ee57085b-2f03-40da-9b8f-3123ecce465a`、Config保持f10。重复相同诊断后，真实边接触可解析且接触点在cube上，完全擦过的假角轨迹被拒绝。未改武器参数、攻击预算、积木规则或等待时限。旧来源通过仅保留原身份，新的非影响审阅和实际关联回归仍须闭合。

fresh82已真实exit0，五个完整fixture共58/58Passed：BlockFallingSphereContact9、BlockWorld27、TpsOverlap5、BlockGravityCohortIntegration2和PistolGirlTuning15。完整XML与来源图已独立消费，源码/meta不变、10文件和19偏好恢复；UTC12:57:54独立postrestore Read为同PID71596、隔离桌面、2f13/ee570/f10及六idle。原件和验证为 [actual-progress36.json](FallingSphereSweepDesign34/actual-progress36.json)。没有并行构建/Player。

Applied35实际Runtime的临时诊断又确认既有身份缺口：连续异阵营格的共用边界，两条下落Cast与一条稳定Overlap在TPS查询后丢失Cell/RunId，随后从Point重推认成邻格，三条均发生Owner/Cell不一致。诊断实际inner success=true、临时对象已销毁、场景/source pins未变；不宣称付费攻击或fresh79动态原因已验证。

Applied36已实际应用完整Runtime Version02和新的付费接触测试，共14个精确文件delta，旧dirty原件全部保全。首次写入在第10文件遇到OSError22，核对完整partial源图后仅补写剩5文件，保留中断记录；首次实际编译只报新增测试两处缺少PlayerInputFrame namespace，CompileRepair01仅补一行using Splatoon.Networking，不改测试断言。第二次实际recompile_status为completed、failed=false、errors=[]。UTC13:35:22独立Read确认Runtime `05bceaba-644d-4172-8be2-8e27eeb66c6f`、Tests `4f419a0a-0124-4aa2-8214-3ff321f290d5`、Config保持f10，同PID71596/隔离桌面及六idle；[最终源图](FallingSphereSweepDesign34/Applied36/CompileRepair01/source-after.json)。

新接口保留稳定/下落格的准确Owner/Cell/RunId；已知但失效的接触直接拒绝，不重猜邻格；主弹丸、泡泡、副武器、InkStorm和WaveBreaker均保留完整查询身份，稳定WaveBreaker附着准确支撑格。原未知/地图路径、几何预算和付款规则保留。

fresh83实际完整终态为16例15通过/1失败：PistolGirlTuningTests整类15/15通过，新接触完整fixture在末尾日志读取已退休MeshCollider的enabled时MissingReferenceException，原XML与runner3/外层exit1保留；未将该fixture计通过。10文件和19偏好完整恢复、源码/meta未变，UTC13:42:08独立Read为同Runtime05bc/Tests4f41/Configf10、隔离桌面及六idle。DiagnosticRepair02仅在日志前缓存原stable/falling标签，所有断言和玩法不变；实际编译成功，UTC13:47:23独立Read Runtime不变、Tests `8b700719-1ed0-46c4-8dd7-1f68809d1b94`、六idle。

fresh84已真实exit0/runner0，新增付费接触完整fixture1/1Passed，实际覆盖稳定/下落共面敌友判定、真正付款与拆除计分守恒、失效身份拒绝、WaveBreaker精确支撑和真实退出/rehost。完整XML `20261013-134937.xml`，10文件及19偏好完整恢复、源码/meta未变，UTC13:50:35独立Read为05bc/8b70/f10、隔离桌面、六idle。Root已执行 [fresh84终态消费器](FallingSphereSweepDesign34/fresh84-terminal-consumption01.json)，逐byte核实唯一测试日志变更与手枪整类15/15的精确来源扩展，并保留fresh83新测试失败原件。核心仍须闭合其余32整类及非影响证明；当前没有运行中的测试/构建/Player。此前fresh82的58例仍保留Applied35原始身份，非影响来源须经精确审阅扩展。当前Windows21核心收据、正式同源包、网络及性能门仍未通过，Goal保持active。

本次鼠标反馈继续沿用已编译的Application29保护：当前三份保护源码SHA与原收据一致，自动测试禁止系统光标捕获，普通游戏失去前台许可时释放、恢复焦点不自动锁回，后台大厅物理鼠标测试拒绝执行。最近60秒只读采样598次全部成功，限制鼠标0、测试Editor成为前台0；本次新的独立Read也确认Editor在CodexBlockTests20261013a、用户输入桌面为Default。测试/构建/Player仍串行，不切换用户桌面或注入系统输入。[保护证据](FrontendInputProtection32/verification.md)。

以下保留Applied35之前的实际来源记录：

fresh78已真实结束并完整恢复：LanRoomCode15/LanDiscovery10/NetworkOptimization23/SplooshGirlPlay1/SpecialWeaponParityPlay15，合计64/64Passed。fresh79随后七个完整fixture真实结束：59例，58通过、1失败；失败为PistolGirlTuningTests的固定瞄准墙面建造，保留完整14/15失败行，不摘取通过叶或沿用旧通过抵替。两批源码/meta未变、文件和19项偏好完整恢复。fresh79独立恢复后Read UTC11:46:19确认当前e5c/b46/f10三MVID、PID71596、隔离桌面与六idle。目前没有运行中的测试、构建或Player；新核心收据尚未生成。Version02此前接受76/77/78的候选绑定不代表核心验收通过。[实际进展](NetworkJoinRepair32/LifecycleFixtureApplication33/verified-progress.md)。

GameplayRepair30 已实际合入 Air完整安全恢复、地形等待期间的移动姿态保留、跨回合边界/重连重置与真实纸片墙碰撞检查。Paper Runner34 在同一 Unity/Jobs ILPP 路径下，9角色1620例严格等价已通过；消费器实际解析9角色共8独立Paper资产，原Runner31/32失败和外层旧误断言均保留，未放宽exact门。[Paper实际结果](PaperSampleOptimization30/Runner34/verified-result34.md)。

Application32 已应用有限新下落见证、完整输入/地形排空与真实结束边界、独立float32格坐标oracle，以及Joining时实际NGO停止事件收尾。Runtime `e5c10e2c-ed4a-4eec-835b-7e9f7409fc4b`、Config `f10e0192-24dd-4c09-b871-9bf5db4b0607`。fresh74 的8个关联filter真实22/22通过，runner/wrapper exit0、源码/meta未变、文件及19项偏好恢复、最后六idle；其Tests旧身份 `9f044f2c-1d9d-4cb4-9a0c-90c11753f4da` 保留，[原回执](RegressionTriage/LatestSourceFullRegression15/Runs/fresh-fixtures-74/preservation.json)。fresh72/73旧来源132例另有 [源变更非影响证明](NetworkFallCoverage30/Application32/FreshSourceReview32/review.md)，不冒称新MVID实跑。

LifecycleFixtureApplication33只新增真实UTP测试并修复当前NUnit属性及未保存场景名兼容问题，未改Runtime/Config。fresh75原51例48通过/3准备失败保留；fresh76三真实生命周期例全部通过。fresh77完整PaperTraversal19/ShooterMovement28/PaperLanding16/SwimMovement21/AirSwimBot1，共85/85通过并完整恢复；当前Tests `b46b8ac0-1528-4b2c-aa74-c0edcf16bf6f`。独立恢复后Read于UTC11:13:32确认同PID71596、隔离桌面及六idle。[真实结果与来源](NetworkJoinRepair32/LifecycleFixtureApplication33/verified-progress.md)。Windows21核心来源闭合、独立Player网络/性能与正常中文HUD仍待验收。以下 Application29 / Windows20 均保留各自旧来源。

后台鼠标现场复核：UTC10:47:51—10:48:51只读采样60秒，598次范围和前台读取全部成功，鼠标限制0、测试Editor成为用户前台0；前一轮前台读取受限的原件也保留。自动测试拒锁、隔离桌面、失权释放和不自动重锁保护继续生效；[现场证据](FrontendInputProtection32/verification.md)。Goal保持active。

后台输入保护已于本次 Application29 补强：自动测试明确禁用物理鼠标捕获，普通交互要求本进程实际前台权限，权限失去后只释放不重抢；旧大厅原生鼠标测试后台拒绝启动。已在隔离 Editor 编译通过，fresh69 捕获/比赛回归完整1/1通过并恢复。60秒598次鼠标读取限制0，597次有效前台读取测试Editor成为前台0（另1次前台读取无有效窗口）。测试后六项idle、Cursor=None/visible=true/CanCapture=false。[实际验证](Windows20/FullBaselineFailure37/Application29/background-cursor-verification.md)保留原件。当前源码来源已是Application29；旧Application28整库收据和Windows20包保留原来源，Windows20包尚未含新增补强，下次同源构建带入。Goal保持active。

正式包完整比赛烟测已通过：两个独立Player自然计时180.058850秒完成三分钟首局，12项自然流程检查全true，真实块数结算、返回房间/下一回合与正常退出均有日志；前后全包VerifyOnly通过。native/harness/bootstrap/guard原退出码全部0，两Player退出0，偏好restored/rawEqual均true。窗口审计130个样本、129个有Player，读取错误0，未观察到Player处于用户输入桌面。烟测本身未验证正常后缓冲中文HUD或清空积木oracle，这两项仍由对应包级证据完成。

当前03序列已于UTC08:38:44.854333结束，原工具handle3265实际exit1；网络wrapper/native/harness/bootstrap/originalBootstrap/guard均exit4，stopReason为网络wrapper未成功退出。smoke通过记录保留，性能阶段未启动、没有自动重试，Goal保持active。[序列终态](Windows20/NewSessionPackageUtf8Compat03/Candidates/Runs/20261013T082403Z-e8b7601d/sequence.json)和[网络终态消费](Windows20/NewSessionPackageCommandRecorder01/network-terminal-consumption01.json)保存原结果。

网络六组已全部实际终态，**6/6已执行、2通过/4失败，完整矩阵未通过**：双人0ms/0%与200ms/3%通过；双人100ms/1% P95=0.037836米、raw P99=0.805412米>首版0.60米（809移动ACK、438己方支撑）；八人0ms/0%和100ms/1%均权威下落几何超时，后续冻结/重连/清场/重建缺证，八人100ms/1%另有七客户端纠正失败、client-7支撑90<100；八人200ms/3%在initial join时client-4 exit3，后续probe/恢复/移动样本缺证。requiredProfileMatrixComplete与caseProfilesMatch均true，但networkProfilePassed=false；不以完整执行或部分case通过替代整个矩阵门。

网络29个自有Player均已退出，退出码集合0/3，偏好restored/rawEqual=true；窗口审计291样本、282次有Player窗口、读取错误0，未观察到Player处于用户输入桌面。原退出码非零使组合completeRecovery=false，分别保留运行失败和退出/偏好恢复成功，不改判通过。仅核验白名单报告，未操作源码、测试或进程。六场性能及其余交付结果仍未验收，Goal没有完成。

以下为同一03序列在4/6时的历史观察，来源仍为同一次Network摘要及运行目录；原handle运行状态只描述该旧时点：

UTF8兼容03只改四处JSON读取编码和串行器检查入口，原工具、请求、包、核心收据保持。实际序列为 `Windows20/NewSessionPackageUtf8Compat03/Candidates/Runs/20261013T082403Z-e8b7601d`，原工具handle3265继续live；真实启动/退出/窗口与偏好证据均保存。

联机矩阵最新已终态4/6：双人0ms/0%与200ms/3%通过；双人100ms/1%纠正P95=0.037836米达标、P99=0.805412米超过批准的0.60米门槛（809真实移动ACK、438己方支撑样本），保留失败。八人0ms/0%在权威下落几何等待超时，后续冻结/重连/清场/重建缺证，亦不计通过。正在继续剩余矩阵并只读定位，未在测试中修改Assets。六场性能尚未运行，Goal保持active，剩余六类门仍未全部验收。

新正式包比赛期间另有45秒448次有效只读鼠标/前台采样，鼠标限制0、两Player成为前台0；只覆盖该采样窗口，不扩大为逐帧整场保证。原始数据为 `Windows20/FullBaselineFailure37/Application28/front-desktop-package-smoke-45s-observation01.json`。

以下为此前历史快照：

功能门已实际通过：原范围 **62/62组**，额外后台鼠标契约单列；Core14无遗漏，原37基线失败和已知13个失败夹具全部有较晚同名完整通过闭合。fresh68完整31/31并已恢复、独立Read和18登记；串行协调器实际exit0。最终来源独立Read、Root最终审阅、核心收据和构建前提均已完成，见 [核心验收记录](Windows20/core-gate-review20261013.md)。保留实际历史通过来源及最终源码没有重新集中执行整个库的边界。

正式同源Addressables/Windows包已实际构建成功，UTC08:03:26—08:06:16，约170秒，包位于 `Builds/Blocks-20261012-acceptance20/InkLan.exe`。构建前后均确认同一Editor/源码及六项idle。整个359文件包身份已捕获，独立重算库存无缺失、新增或变化。包身份和构建不代替包级玩法/网络/性能结论。

包级串行器的sequence01/02均在Player启动前的VerifyOnly停止，失败记录保留：先是WindowsPowerShell子进程模块路径不兼容；修正并实际probe通过后，发现UTF8无BOM清单的中文路径被PS5默认编码误读。全包实际SHA一致，新UTF8兼容检查入口正在窄修，冻结工具/请求/核心收据与Assets保持。当前仍有六类包级及交付门待验收，Goal保持active。

以下为此前历史快照：

fresh66六组角色流程/调试/换装/名字/武器调试和fresh67四组待发布地形/采集/worker回归均已全部完整Passed，并完成真实exit0、精确文件/偏好恢复、独立Read、原07全行/XML守卫及18完整登记。权威原范围现为 **58/62组，剩4组**；额外鼠标契约单列，Core14全部具备实际通过证据。

原串行协调器session58976继续fresh68核心整合与基准报告四组。BlockHostPlayTests已观察到完整2/2Passed，其余仍运行；整批恢复和18登记完成前不增加上方计数。实际核心收据、正式Addressables/Windows包和包级验收仍待执行，Goal保持active。

后台鼠标保护现场复核：UTC07:51:38—07:52:23只读采样45秒，448次鼠标范围与前台窗口链读取全部成功，鼠标限制0次、测试Editor占据用户前台0次。继续隔离桌面/无控制台/串行测试，未改Assets或开启另一套测试。证据见 [现场复核](Windows20/FullBaselineFailure37/Application28/background-cursor-fresh68-verification.md)。该证据仅覆盖采样窗口。

以下为此前历史快照：

fresh64爆炸枪/显示四组及fresh65命中/瞄准/接缝/弦化/弹道五组均已全部完整Passed。fresh64共4叶，fresh65共7叶，其中完整弦化3/3。协调器均完成真实exit0、精确文件/偏好恢复、独立Read、原07全行/XML守卫和18完整登记。最新原范围 **48/62组，剩14组**，额外鼠标契约单列，Core14通过证据保持。

fresh66六组角色流程/调试/换装/名字/武器调试正在隔离桌面串行执行，运行中的结果不计通过。正式同源包交接04及独立构建命令记录入口已准备并经Root代码审阅，原App28/18/04冻结工具未改；实际核心收据/签名/构建/Player仍未执行。Goal保持active。

以下为此前历史快照：

fresh63普通枪与调参五组全部完整Passed：模型枪调参、模型枪实战、窄域、双枪及机枪；共19叶。串行协调器已完成真实exit0、精确文件/偏好恢复、独立Read、原07全行/XML守卫与18完整登记。最新原范围 **39/62组，剩23组**，额外鼠标契约单列，Core14全部具备通过证据。协调器session58976继续后续批次，运行中的结果不计通过。

同源Windows包交接04已在Reports准备并完成Root源码审阅，等待全部功能门齐后实际生成核心收据、正式Addressables及Windows64包；未构建、未签收据或执行包级验收。Goal保持active。

以下为此前历史快照：

fresh62六组已全部完整Passed，共6叶：八爆付费收益、角色迁移、副武器玩法/显示和两项特殊武器一致性。runner/wrapper真实exit0，10文件、19逻辑/17物理偏好精确恢复、source/metadata不变；07:16:39独立Read确认同App28 PID71596、三个MVID、隔离桌面与六idle。六组已实际登记18，最新原范围 **34/62组，剩28组**，额外鼠标契约单列；Core14全部具备实际完整通过证据，仍需其余原范围与37失败闭合。

Root已实际启动18串行协调器（工具session58976，`FinalImpactReview18/SerialExecution01/ExecutionFrom63`），fresh63–68六批精确覆盖剩28项；每批完整终态、精确恢复、独立Read、原07全行守卫和18全selector登记后才下一批，失败停止不重试。当前运行不计通过；未启动构建或Player。正式同源Windows包交接工具正在Reports中准备。

以下为此前历史快照：

FinalImpactReview18 已实际初始化并登记原27组独立完整历史通过、fresh61完整选角UI及额外Match鼠标契约。原62范围现为 **28/62组已验证登记，剩34组**；额外鼠标契约单列不抵替原玩法门槛。原57映射、37个基线失败和24/58/59/60批次Failed原件保留，当前UI61的较晚同名完整通过用于闭合选角失败。尚未生成核心收据或宣称全部功能通过。

fresh62六组串行回归已实际启动：八爆支付与充能、角色迁移、副武器玩法/显示，以及两项特殊武器一致性。八爆完整1/1已观察到Passed，但整批尚未终态、恢复和登记，不计入上方28。后续协调器与同源Windows包交接工具并行准备；Unity测试/构建/Player执行仍串行、隔离桌面、不切换前台。

以下为此前历史快照：

Application28 已实际修复 Editor 选角测试的 GameView 分辨率适配，全部原真实点击、三个尺寸与断言保留。fresh61 完整选角夹具实际1/1Passed（XML `20261013-065520.xml`），runner/wrapper均exit0；文件/偏好精确恢复、source/metadata未变，06:57:02独立Read确认同PID71596/隔离桌面、三个程序集身份与六idle，视图缩放/平移/默认缩放精确恢复。fresh60 Failed原件保留，后续同名完整通过用于闭合。证据见 [本轮后台测试结果](Windows20/FullBaselineFailure37/Application28/background-test-result.md)。

本轮60秒鼠标范围采样594次全部成功，限制0次；前台窗口链不可用，不将其0计数作为不抢前台证据。后台光标许可保护和隔离测试入口继续生效。新18来源工具正在收尾；复用与当前结果尚未绑定，因此不公布新的已通过数量。正式Windows包仍待构建，Goal保持active。

以下为14:16及此前的历史快照：

用户反馈后台测试锁走鼠标后，Application26 已实际加入系统光标桌面许可并编译通过。后台保持逻辑游戏控制，但隔离桌面不能锁定或隐藏系统鼠标。真实只读探针确认 CanCaptureSystemCursor=false、Cursor=None、visible=true；45秒系统观测448次，鼠标限制和测试 Editor 成为前台均为0次。证据见 [后台鼠标保护验证](Windows20/FullBaselineFailure37/Application26/background-cursor-verification.md)。

fresh59 的完整开局/战斗/Tab记分板/回热身/退出方法实际1/1Passed，包含后台鼠标与HasControl断言。HeroSelectionUi 仍在主菜单DEBUG点击失败，保留Failed原件。整批runner3、wrapper1，全部文件/偏好精确恢复、source/metadata未变；14:13:34独立Read确认同一App26身份与六项idle。鼠标保护已验证，整批不计通过，现有UI失败继续待修。

App25来源的fresh57六组已经完整通过并绑定，17历史总账为39/62、剩23；fresh58五组通过、一组HeroSelectionUi失败，未绑定整批。随后App26改变了Runtime/Tests来源，旧39组仅保留历史来源记录，新影响审查正在准备，不能直接当作App26通过数量。正式Windows包及包级验收仍待执行，Goal保持active。

以下为13:33及此前的历史快照：

fresh54/55/56各六个完整夹具已全部Passed并实际绑定17。fresh55为角色更新1/1、换装区3/3、命中结算1/1、机枪1/1、双枪1/1与模型枪调参15/15，共22叶；fresh56为玩家名字1/1、快速爆破枪1/1、窄域枪1/1、完整弦化3/3、第三人称瞄准1/1和武器调试1/1，共8叶。两批runner/wrapper真实exit0，10文件、19逻辑/17物理偏好精确恢复，source/metadata不变。fresh56于13:32:59独立Read确认App25原三个MVID、PID71596、隔离桌面13a/用户Default及六项idle，13:33:31完成整行绑定。当前总账 **33/62已通过，剩29个关联复核执行选择**。此前列出的15组遗留项（角色迁移1、重力恢复11、核心3）全部已在当前源码完整通过；仍不能据此宣称全部功能硬门或Goal完成。

Root已审描述式计划，八批严格覆盖最初47项且无重复/遗漏；17串行协调器（工具session17474，`SerialExecution01/ExecutionFrom54`）已闭合fresh54/55/56并进入fresh57：武器表现、爆破付费收益、建造额度发布、队列公平性、重力发布失败和同伴窗口。每批实际完整终态、精确恢复、独立Read、完整Passed绑定后才继续，失败立即停止，不自动重试或取消。源代码保持App25，复用隔离Editor71596，不切用户桌面；最终核心收据、Windows正式包和包级验收仍未执行。

以下为此前执行快照：

FinalImpactReview17已由Root实际冻结、初始化并绑定fresh50/51/52，当前为 **12/62组通过、剩50个新来源执行选择**，旧Runtime通过仅保留历史。fresh51完整5组11叶、fresh52完整6组18叶全部Passed，runner/wrapper实际exit0，原XML与完整恢复/独立Read/source graph均经原守卫核验；fresh52最后Read于13:06:52确认原App25三个MVID、PID71596、隔离桌面13a/用户Default及六idle。上一来源遗留的角色迁移1组及重力/支撑/恢复11组，至此已取得新来源完整通过并入账。

fresh53已实际启动三个完整核心selector：房主实战、全武器积木接入、批量爆破生命周期（工具session63082）；运行中不计通过，不修改源码。其余47组主要为运行代码变化后的关联复核，尚未执行，不能沿用旧Runtime通过冒充新来源通过。17冻结工具manifest SHA `ecb12fcbfdf7c14b5570e7c17137f125e6771dde747d94c481e45d2ee85c67eb`；原57→48要求、原1783/1746/37叶、实际历史失败与较晚完整通过闭合门、全部核心守恒/支付/碰撞/恢复门保留。未生成最终核心收据、未构建正式Windows包。新03包交接仅准备准确17入口与App25末态碰撞取证说明，业务源码与构建/请求工具字节保持，不算交付通过。

以下为此前的执行快照：

fresh50 完整HeroMigration已实际1/1 Passed，XML `20261013-045448.xml` SHA `913a4f92058975e6533ad5fbd5af908ff29cd8afcedde6e09bf10e92a7e76a83`；原999请求/服务器回复/角色不变/不存在消息断言保持，九角色换装、死亡重生、退出重进流程全部走完。runner/wrapper实际exit0，10文件、19逻辑/17物理偏好和完整source/metadata精确恢复，12:55:45独立Read确认同一App25身份与六项idle；原守卫完整核验见 `Application25/fresh50-restored-whole-row-proof.json`。关闭角色迁移失败所需的新来源真实证据已取得，尚未绑定17。fresh51随后实际启动五组重力/支撑完整回归，当前运行中不计通过；源码不变，仍使用原隔离桌面与恢复wrapper。

fresh49 已实际结束：6项中5通过、1失败，runner/wrapper 均实际退出3，协调器停止，未提交 fresh50。选角界面（三分辨率）、副武器功能、副武器视觉、第三地雷替换充能、墙边投掷预览与权威碰撞一致均完整通过；HeroMigration 唯一失败为请求角色999时，本地读取默认配装先触发不存在的配置键异常，尚未到服务器拒绝。原 XML、失败与退出码保留，10文件、19逻辑/17物理偏好精确恢复，源码/metadata不变；独立恢复复核见 `Application24/fresh49-restored-failed-batch-proof.json`。历史16账本仍为28/48已绑定、20未入账，其中5已有实际通过证据、15尚未通过，未将失败批次的叶直接计入总账。

上一源码遗留的15组具体为：角色配置迁移1；重力/支撑/恢复11（支撑重试、上层自身落定、分段下落整合、运动支撑、重新分组连续性、发布屏障释放、默认处理窗口、批量下落、迟到地板冲突、稳定结构穿透恢复、落定穿透分类与恢复）；核心整合3（房主实战、武器积木接入、批量爆破生命周期）。待验证不等于15个已知玩法缺陷。

Application25 已实际应用两个独立审查的 Runtime 变更：无效角色ID沿原RPC进入服务器拒绝并解除Pending；高塔/级联压力报告新增末态全部曝光面原生碰撞及100个原列顶部残留检查，在采样关闭后执行。原Tests/Config/Generated/metadata不变，全部既有断言保留。Unity实际编译completed、无错误；12:46:08独立Read确认隔离Editor71596、原Config、新Runtime `b254e3f3-f8d0-40d3-a6fb-fe1384bf6727`、新Tests `05fccb06-979b-4540-8f8e-c728f675813b`及六项idle，用户仍在Default桌面。新源码图SHA `329de360bb95331c16428fc6a5d7e1990e318f5dce3e2769057141025d302fce`。当前新运行来源尚无测试终态，旧28及fresh49的5项仅保留为历史证据；FinalImpactReview17正在准备新来源去重回归范围（原48加直接角色调用新增8和Benchmark关联新增6，共62个执行选择），没有预填通过或最终核心收据。

以下为 App24 及此前的历史快照：

fresh48完整弦化回归已实际 **3/3 Passed**（XML `20261013-040706.xml`，SHA `d88e0422c36e539a0c5de9ccc501a4c5c8287b9e0ab0b9aa888dbdbf043bf258`）；runner/wrapper实际exit0。十文件与19逻辑/17物理偏好精确恢复，源码/metadata不变；12:07:58独立Read确认隔离Editor71596、原Runtime/Config、新Tests MVID `dc6cb54d-2bc0-4938-bce9-259d3d1fa03c`及六项idle。原始XML实际包含十个英雄/阵营友方移动样本的恢复资格True、旧墨粒子False，以及53条真实射后恢复锁内SwimFalse记录；完整墙面、自然翻越、空中射击/重入、碰撞/命中、低天花板与死亡/重生流程全部走完。完整恢复/原件/源码验证见 `Application24/fresh48-restored-whole-row-proof.json`。

FinalImpactReview16已实际初始化、按完整依赖复用15的27组并绑定fresh48完整弦化通过，最新总账为 **28/48组通过、剩20组**：重力与恢复11、角色/UI/副武器/大招6、核心整合3。fresh45/46/47各完整Failed原件及恢复记录保留，较晚同名完整fresh48通过用于闭合；没有抽取失败夹具中的Passed叶。仍是待最终Root审签的累计草案，未生成核心收据或宣称全部功能门通过。

12:18:06独立Read再次确认App24的Editor71596、隔离桌面13a、用户Default桌面与六项idle；12:20实际启动16串行协调器（工具session18151，`SerialExecution01/ExecutionFrom49`）。描述式检查已确认四批严格覆盖剩20组，fresh49角色/UI/副武器/大招6组、fresh50重力5组、fresh51重力/恢复6组、fresh52核心整合3组。每批真实终态、精确恢复、独立Read和完整通过绑定后才继续；失败停止，不取消或自动重试。运行中不计通过。

Application24已实际应用仅Swim测试的真实恢复锁采样和付费弹丸/地形自然完成：第3角色爆炸泼桶35帧锁内逐Tick要求不能弦化，锁结束后保留原20Tick重入断言；真实Tick/Projectiles.Simulate完成全部已接受工作后才Seed。107条已有Assert调用保持（106整行精确，另一行只更新本地生产模拟时钟），未Clear积木/弹丸，未修改Snapshot锁或Runtime/配置/metadata/共享helper。实际编译completed无错；首个编译后独立Read于12:04:50确认新Tests身份。

fresh47此前完整3项2P1F（XML `20261013-035530.xml`，SHA `9939a51673b95366b73dccae43b045655d27bde9ca1557cbc7f8e108175437a4`）原Failed保持，失败为第3角色射后空中弦化重入，文件/偏好/source/meta均完整恢复。以下为更早快照：

最新已绑定数量仍为 **27/48组、剩21组**。Application22实际应用真实人形接地前提并编译通过；fresh46完整弦化结果为3项2P1F，XML `20261013-034634.xml`保持原Failed。真实分支记录显示首个Move尚未接地、第二个Move产生Below并落地，随后12次中立Swim输入保持人形；旧中立检查已经走过。新的失败发生在SwimPlayTests.cs:346，测试要求旧游动墨粒子发射，而当前默认积木模式明确禁止该粒子。完整恢复与11:47:18独立Read已确认同PID/程序集身份及六项idle；整夹具未绑定。

Application23已实际迁移两处旧墨粒子的正向断言为积木模式的负向断言，保留其余102条已有Assert，并增加3条真实己方恢复/当前积木地形前提检查；运行代码、配置、metadata和共享helper不变。实际编译completed无错误；11:53:37独立Read确认隔离Editor71596、原Runtime/Config、新Tests MVID `17594306-8dc0-48ab-9aa6-e3f570a36792`及六项idle。fresh47完整弦化回归正在后台运行，未取得终态，不计通过。调参说明已按批准的首版标准实际应用，记录在 `Delivery96/TuningGuidePrototypeV1Candidate01/actual-application.json`；不代表包或性能验收通过。

以下为此前各批历史快照，未覆盖上方最新状态：

fresh44已实际四个完整夹具全部Passed：模型枪参数15/15、接缝射击1/1、队列公平性5/5、静态支撑1/1，共22项；runner/wrapper exit0，精确恢复与独立Read后已绑定15。最新为 **27/48组已绑定、剩21组**。fresh45完整弦化回归3项中2P1F，原XML `20261013-032046.xml`保留；中立平台资格断言在SwimPlayTests.cs:280失败，整夹具未绑定。runner/wrapper真实exit3，文件/偏好/source/meta均恢复，11:20:54独立Read确认App21原身份及全部idle；协调器exit1并停止后续提交。Root正在核首帧尚未物理接地时按空中形态处理的测试初始化问题，暂未修改Swim源码。以下23/25为此前绑定快照。

FinalImpactReview15已实际初始化、按完整依赖闭包复用14此前22个完整通过调度并绑定fresh43完整TPS，最新实际记录为 **23/48已绑定、剩25组功能回归**。剩余为重力/支撑/恢复11组、角色/UI/副武器/特殊技能6组、共享依赖复测4组、核心积木整合3组及弦化1组；待验证数量不等于已知缺陷数量。

Application21Retry01已实际应用仅TPS证据采样时钟对齐与只读诊断的候选，36条原断言逐字保留；Runtime/Config/metadata及其他Tests不变。原Application21在复制长路径Reports证据时退出1，TPS写入前终止，原部分证据保留；短保全路径修复后重新应用成功。Unity实际编译completed、无错误；11:06:25独立Read确认隔离PID71596、原Runtime/Config、新Tests MVID `bf55219c-8b17-494c-ac81-29b253e9bd04`和六项idle。

fresh43完整TPS已实际1/1Passed（XML `20261013-030800.xml`，runner/wrapper均exit0）；五角色近远命中、真实RPC弹丸/GPU轨迹、后坐力、准星、枪口阻挡及后续输入断言全部走完。19逻辑/17物理偏好、10文件精确恢复，source/meta未变；11:09:01独立Read确认同一App21身份及全部idle，该整行已实际绑定15。新日志观察到部分角色manualNow与ServerTime有偏差，修复后的样本均在RPC与LateUpdate前未过期；不能倒推原fresh41的具体时钟值。

15串行协调器已启动fresh44，先执行专业模型枪参数、接缝射击、队列公平性及静态支撑四个完整夹具；随后按实际终态与恢复守卫推进fresh45弦化、fresh46角色/UI/副武器/特殊、fresh47/48重力及fresh49核心整合。每批完整通过、精确恢复、独立Read和实际绑定后才启动下一批，失败即停止；运行中不计通过。仍使用隔离桌面13a的Editor71596，不切换用户工作桌面。

| 剩余功能类别 | 组数 | 执行项目 |
|---|---:|---|
| 重力、支撑与落定恢复 | 11 | 支撑重试；上层自身落定验证；分段下落整合；运动支撑；重新分组运动连续性；发布屏障释放；默认处理窗口；批量下落；动态地板冲突；稳定结构穿透恢复；落定穿透分类与恢复。 |
| 角色、界面、副武器与特殊技能 | 6 | 角色配置迁移；选角界面；第三地雷替换时充能；投掷预览/帧率与墙面权威碰撞一致；副武器实战；副武器视觉。 |
| 共享依赖复测 | 4 | 接缝实际付费射击；静态地图支撑量化；地形任务队列公平性；专业模型枪参数。此前完整通过过，相关文件变化后需要重新验证。 |
| 积木核心整合 | 3 | 房主实战流程；武器与积木整合；批量爆破生命周期。 |
| 弦化移动 | 1 | 完整SwimPlayTests，上次中断未取得终态。 |

fresh42窄域枪完整复测已实际1/1Passed（XML `20261013-024314.xml`），runner/wrapper均exit0；10文件、19逻辑/17物理偏好精确恢复，源码/metadata未变。10:43:56独立Read确认同一App20身份及六项idle；该整行已经绑定FinalImpactReview14。

Application20 已实际应用 TPS 新视图初始化的四行测试修正（其中只有一条生产 LateUpdate 调用）。Runtime、Config、metadata及其他Tests不变；Unity实际编译completed、无错误，10:32:05独立Read确认新Tests MVID `34222011-8d1c-471f-80a7-bf4198f0e727`、原Runtime/Config和隔离PID71596。fresh41完整TPS复测实际1F（XML `20261013-023325.xml`、wrapper exit3），五角色十次近远靶命中检查均走过，新的失败提前发生在最后一角色飞行显示检查：ActiveShots预期1、实际0（TpsAimPlayTests.cs:277/114），尚未走到后坐力段，不能宣称后坐力已验证。完整19/17偏好、10文件精确恢复，源码及metadata未变；10:35:06独立Read确认同一App20身份及全部idle。原失败保持，正在核对手动采样时钟与RPC渲染真实网络时钟的过期逻辑；不自动重跑。FinalImpactReview14正准备App20继续链，尚未新增通过行。

FinalImpactReview13 v4 已由 Root 和独立审查复核后实际执行会话证据准备、初始化、历史整行复用及 fresh40 两个 Passed 行绑定。真实 `required57-closure.draft.json` 为21/48已绑定、剩27个执行项；fresh34中断批次和fresh39未提交批次均不绑定。除原25项中最新两项通过外，InkImpact共享文件变化要求fresh23接缝、公平队列、静态支撑三行重测，且 `PistolGirlTuningTests` 的完整 Tests 文件依赖也已变化，新增一项重测；此前26项的估算已按实际绑定记录修正为27。TPS保持Failed，镜头后坐力测试时序候选已审查，待应用并完整复测。10:24:42独立Read确认隔离Editor71596、App19三MVID及六项空闲标志；OS只读查询确认无活跃回归runner。

fresh34 实际观察到 Sploosh 1P、InkImpact/TpsAim/WeaponPresentation 各1F；Swim 已提交但没有最终 XML。09:48 的两类进程查询确认原隔离 Editor105868、协调器和 runner 均不存在，终止原因未知。随后两个独立、脱敏 OS 检查再次确认相关进程均已退出；原源码图、manifest、原始 XML、文件及请求已保全到 `Fresh34InterruptedRecovery01/Recovery01`，只归档删除精确匹配的旧 Swim 请求，没有改写任何测试结果或退出码。整个 fresh34 不绑定通过行，Swim 保持中断未验证。

新 Editor71596 实际启动于隔离桌面 `CodexBlockTests20261013a`，窗口审计确认用户 Default 桌面没有该 Editor 窗口。新会话独立 Read 确认 App18 三个 MVID 及六项空闲标志；原19项逻辑偏好、17个物理值、10个文件经原恢复 Guard 精确恢复，原 fresh34 manifest 未修改。实际恢复证据在 `Fresh34InterruptedRecovery01/Restore01/recovery-state.json`。

Application19 仅实际应用三个已审核 Tests 修正：已卸载 Camera 的 finally 空值保护、近靶位于完整枪口发射通道之外、按 Profile 完成射击动画有限淡入采样；Swim、Runtime、Config 和 metadata 未改。Unity 实际编译完成且无错误，新 Tests MVID 为 `79d5cca6-fa5e-4c0e-9d34-45fe30183590`，Runtime/Config MVID 保持。

fresh39 因新 Editor 尚无 Pipeline 历史 status 文件，在首项 `start.argv` 创建前退出，实际 runner/wrapper exit1/5；没有提交测试。原结果保留，已精确恢复文件与偏好。随后用一个真实现有纯测试完成首次 Pipeline 初始化，实际1/1通过（XML `20261013-020646.xml`）；该单方法不作为完整夹具验收，不写虚假终态。

fresh40 已完整结束：InkImpactHost 1/1P（XML `20261013-020842.xml`）、TpsAim 1/1F（XML `20261013-020912.xml`）、WeaponPresentation 1/1P（XML `20261013-021004.xml`）；runner/wrapper保留实际exit3，源码/metadata未变，19/17偏好及10文件精确恢复。10:11:29独立 Read 确认同一 PID71596、App19三个MVID、隔离桌面及六空闲标志false。新 TPS 失败准确位于镜头后坐力检查（源码353行、实际0）；已走过五角色近远枪口无遮挡、真实命中及 GPU 轨迹检查。测试停用正常呈现后，换装的新 view 未初始化，先 Shot 后首次 LateUpdate 的 reset 会清除 kick；正在独立复核最小测试时序修正，尚未应用。

FinalImpactReview11历史23/48、剩25，13历史21/48、剩27及14历史22/48、剩26的记录保持。最新实际FinalImpactReview15已绑定23/48、剩25项；来源变化后的数量以上方最新记录为准。七类Goal验收仍未全部完成，正式Windows包尚未构建。

## 当前工作

`fresh-fixtures-22` 的历史结果保持 28 通过、2 失败。Application15 两处 Tests-only 修正后的 `fresh-fixtures-23` 已实际执行 7 项、全部通过，runner 和 wrapper 均退出 0；十份输出、19 个逻辑偏好/17 个物理值已精确恢复，源码及 metadata 未变。恢复后独立 Editor Read 确认原 PID、Runtime/Config/Tests MVID 与全部空闲标志一致；本批三个完整夹具已绑定最终来源审查草案。

`fresh-fixtures-24` 已实际执行 107 项：106 通过、1 失败。唯一失败是武器参考测量的 Play 方法触及 UTF 默认 180 秒超时；原始失败保持不变。本批已精确恢复输出、偏好与源码，并完成恢复后独立 Read。

Application16 给上述参考测量增加框架 `Timeout(600000)`；Application17 为另外 15 个 Tests 文件中的 22 个已选 UnityTest 方法补齐相同属性，方法体、内部等待时限及断言均未变。实际编译已完成、无错误；07:44:41 的独立 Read 确认 PID 105868、六项空闲标志为 false，Tests MVID 为 `c3f7fb15-d001-476b-9cca-d48a0b2b1e59`。该 Read 首次 Roslyn 编译耗时 110780ms。

`fresh-fixtures-25` 已实际执行 31/31 项通过；runner 与 wrapper 均退出 0，十个输出和 19 逻辑/17 物理偏好精确恢复，源码与 metadata 未变。07:52:03 的独立 Read 核对 App17 原 PID/三程序集 MVID 和六项空闲标志；本批三个完整夹具已绑定 FinalImpactReview10。

`fresh-fixtures-26` 已实际执行 8/8 项通过：重力屏障发布失败夹具1项、无范围截断爆破夹具3项、原生地形依赖补充夹具4项。wrapper 已退出0，十个输出和19逻辑/17物理偏好精确恢复，源码与 metadata 未变；08:22:23 独立 Read 核对同一 PID/三程序集 MVID 与六项空闲标志，本批三个完整夹具已绑定 FinalImpactReview10。

`fresh-fixtures-27` 已完整结束：五个夹具4通过、1失败，runner/wrapper保留实际exit3；十份输出、19逻辑/17物理偏好精确恢复，源码及metadata未变。恢复后独立Read确认App17原身份和空闲状态。唯一失败是机枪夹具暂停match的FixedUpdate后只推进弹丸，留下130项已接受地形工作，Seed守卫正确拒绝覆盖。

Application18仅修复MachineGunPlayTests：Seed前用现有Tick/Projectiles.Simulate及Publish自然完成已付款攻击，保留88发、22轮、85材料、碰撞、计分及生命周期断言；共享helper和所有非Tests源码/metadata未变。真实编译完成且无错误；首次状态读取在原生Asset Database约118秒刷新阶段超时，失败原件保留。08:59:32第二次独立Read成功，确认同一PID105868、Runtime/Config身份不变、新Tests MVID `e6039922-a350-4ef9-b027-c81d55f9484f`及六项空闲标志false。

FinalImpactReview11已按完整依赖闭包重绑定10已有18个通过调度，再绑定fresh27四个完整通过夹具。fresh33机枪修复验证实际1/1通过，wrapper/runner均exit0，输出/偏好精确恢复且源码/metadata未变；09:07:44独立Read确认同一App18身份、隔离桌面和六项空闲标志false，整行已绑定11，关闭fresh27机枪失败。现合计23个调度通过、剩25个。串行协调器已进入fresh34，随后fresh35–38依次运行UI/技能、两组重力和基础集成，当前运行不计通过。仍复用隔离桌面 `CodexBlockTests20261012a` 的已有Editor；失败或恢复/身份异常即停止后续提交。没有启动用户桌面上的Player。

fresh34已观察到窄域枪完整1/1通过、InkImpactHost完整1/1失败；原XML `20261013-011712.xml` 的异常栈指向Scenario清理阶段访问已销毁Camera（InkImpactHostPlayTests.cs:176）。根因已由源码顺序确认：Connect将地图设为活动场景，测试相机创建于其中；Leave卸载地图后finally又读取camera.gameObject。同次五张hero截图、付费CSV及退出后末尾result.txt支持定位，但原XML仍为Failed，必须完整重跑。Root已审核Reports中仅增加Unity语义nullguard的一行候选，全部战斗/地形/cosmetic/退出断言及共享rig保持；尚未应用。当前TpsAim及后续本批测试仍运行，批次及恢复尚未结束，不绑定任何本批通过行，也未修改正在测试的源码。

| 本批回归 | 已观察结果 |
|---|---|
| 接缝实际付费射击 | fresh22 1F 保留；Application15/fresh23 同名完整1/1通过，已恢复及绑定 |
| 八爆支付、阵营拆除与充能 | 1/1 通过 |
| 建造额度最终原子提交与返还 | 4/4 通过 |
| 队列公平性 | fresh22 4P1F 保留；Application15/fresh23 全部5/5通过，原重试、FIFO、完整守恒/碰撞/支付断言保留，已恢复及绑定 |
| 原生支撑量化 | Application15/fresh23 完整1/1通过，已恢复及绑定 |
| 重力最后同窗攻击与完整落定 | 2/2 通过，原完整规模、碰撞与落定断言保留 |
| 重力自然窗口 | 2/2 通过 |
| 专业模型枪完整调参夹具 | 15/15 通过 |

fresh23的完整公平性通过关闭fresh22较新失败，不抽取旧失败夹具中的通过叶；fresh33完整机枪通过关闭fresh27失败。Application18最终来源草案FinalImpactReview11已绑定23个完整通过调度，57个所需方法归并的48个执行选择剩25个；这个25不是Goal七类验收的数量。已有通过行均按完整依赖闭包与最终来源核对后复用。四个历史Explicit未出现在实际XML，不计执行或通过。

## 七类 Goal 验收

| 类别 | 当前状态与剩余工作 |
|---|---|
| 功能 | 当前Runtime修复后的fresh29完整11项、fresh30完整398项、fresh31完整92项均Passed；fresh32完整42类145项为144P/1F，均已真实终态、精确恢复并独立核对。唯一Aim观察器修正已实际应用，当前等待编译结果，随后须完整Aim33复测；原通过行仍需按最终来源精确绑定并由core生成器核验，当前完整核心尚未提升通过。 |
| 常规与零散性能 | Windows21/V3历史稳态、交火帧分位及普通发布失败，零散场景分辨率无效；当前37正式包尚未构建，仍须1080p、25万/10万格、180秒、帧分位及普通发布P95≤200ms实测。保留全部旧失败和样本。 |
| 八爆性能 | Windows21/V3历史八爆309.795ms与帧分位通过，偶发长帧保留首版风险；当前37须在正式同源包重测完整八爆、原规模、≤2秒与帧分位，不能把历史包结果当当前验收。 |
| 高塔与级联 | Windows21/V3历史守恒和最终碰撞扫描通过，但整体帧分位失败；当前37须在正式包重测完整32米塔、两万格、守恒、最终落定及碰撞。500ms极限清队列仅诊断，未落定或卡死仍不通过。 |
| 弱网纠正 | Windows21最新八人100ms/1%历史P99为0.730–2.136米，超过0.60米且排空未完成。当前Runtime修复尚未在新正式包测量；P95≤0.15m/P99≤0.60m、真实移动ACK及恢复硬门仍待通过。 |
| 联机矩阵 | Windows21六组历史矩阵0/6通过，后续单组诊断不能替代矩阵。当前37正式包须覆盖同机2/8独立Player各0ms/0%、100ms/1%、200ms/3%，六组迟加入、重连、回合清空重建及完整位置/阵营/数量/哈希/计分一致性。 |
| 交付 | Windows21历史正式Addressables、完整包清单及来源核验通过，保留原包；当前37的Windows22仅准备模板，待当前核心硬门闭合后正式构建，再完成同包比赛、中文HUD、六组网络与六场性能及最终报告/调参说明。尚无当前37可交付通过收据。 |

## 推进方式

Application18已实际应用、编译及确认新身份；接缝、Fair、原生支撑量化与Reference依赖三夹具完整复测通过。优先执行无Play或少量Play的完整夹具，减少85–113秒级程序集重载。与Tests-only修改无关的完整通过行，经精确源码依赖与恢复身份审查后复用；真实失败必须由后续同名完整通过关闭。串行调度每批完整终态、精确恢复、独立Read与完整通过绑定后才启动下一批；不改原1200秒观察时限，不自动重试失败。指定Explicit诊断保留未执行，不能计为通过。

本次隔离 Editor 日志实际记录一处118.478秒资源刷新：资源变更0、实际导入0、脚本编译1毫秒，原生 Asset DB 耗时113637毫秒。这说明本次回归等待包含 Editor 刷新/重载开销；该等待不作为独立 Player 的玩法帧时证据。

功能硬门通过后，构建同源候选 Windows 包，再依次运行包级比赛、六组网络、六个性能场景。保留全部原样本及首版长帧风险。不得靠丢格、缩减结构、截断爆破或放宽守恒/碰撞/支付/联机一致性通过验收。未提交或发布变更。
