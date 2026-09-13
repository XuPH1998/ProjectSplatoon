// Rebuild the five authoritative, strongly typed Luban source workbooks.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
const runtime = process.env.INK_NODE_MODULES;
if (!runtime) throw new Error('Set INK_NODE_MODULES to the bundled node_modules directory.');
const require = createRequire(path.join(runtime, 'ink-author.cjs'));
const { Workbook, SpreadsheetFile } = await import(pathToFileURL(require.resolve('@oai/artifact-tool')).href);
const root = path.resolve(process.argv[2] || '.');
const tables = {
  Character: [
    ['id','int','角色 ID',1],['name','string','角色名称','RifleGirl'],['visualAddress','string','正式角色外观地址','Character/RifleGirl'],
    ['maxHealth','float','生命上限',100],['maxInk','float','墨水上限',100],['recoverInk','float','普通回墨（点/秒）',10],['swimRecoverInk','float','潜墨回墨（点/秒）',35],
    ['moveSpeed','float','移动速度（米/秒）',5],['swimSpeed','float','潜墨速度（米/秒）',8],['enemyInkMultiplier','float','敌方墨水速度倍率',0.55],['jumpSpeed','float','起跳速度（米/秒）',7],['gravity','float','角色重力（米/秒²）',22]],
  Weapon: [
    ['id','int','武器 ID',1],['name','string','武器名称','RifleGirlRifle'],['prefabAddress','string','正式武器地址','Weapon/RifleGirlRifle'],
    ['fireRate','float','权威射速（发/秒）',40],['damage','float','每颗伤害',3.75],['shotInk','float','每颗耗墨',0.3],
    ['speedMin','float','最低初速（米/秒）',20],['speedMax','float','最高初速（米/秒）',25],['gravity','float','墨弹重力（米/秒²）',19.62],
    ['lifetime','float','有效寿命（秒）',0.5],['collisionRadius','float','扫掠半径（米）',0.025],['spreadDegrees','float','散布半角（度）',1.82],
    ['paintRadiusMin','float','最小涂色半径（米）',0.2],['paintRadiusMax','float','最大涂色半径（米）',1.5],['paintHardness','float','笔刷硬度（0 到 1）',0.01],['paintStrength','float','笔刷强度（0 到 1）',1]],
  RoomMode: [
    ['id','int','房间模式 ID',1],['name','string','房间模式名称','三分钟涂地赛'],['characterId','int','默认角色 ID',1],['weaponId','int','默认武器 ID',1],['mapId','int','场地 ID',1],
    ['maxPlayers','int','最大人数（当前场景上限 4）',4],['minPlayers','int','最低开局人数',2],['matchSeconds','float','比赛时长（秒）',180],['respawnSeconds','float','重生等待（秒）',3],['protectionSeconds','float','重生保护（秒）',2],
    ['friendlyFire','bool','是否友伤',false],['groundOnlyScore','bool','仅竞技场地面计分',true]],
  Map: [
    ['id','int','场地 ID',1],['name','string','地图名称','立体训练场'],['sceneAddress','string','场景地址','maps/TrainingGround'],['width','float','场地宽度 X（米）',32],['length','float','场地长度 Z（米）',64],['cellSize','float','归属网格边长（米）',0.125],['layoutVersion','int','场地布局协议版本',4]],
  Global: [
    ['id','int','唯一全局记录 ID，固定 1',1],['defaultModeId','int','默认房间模式 ID',1],['networkTickRate','int','网络频率（Hz）',30],['projectileStepRate','int','弹道子步频率（Hz）',120],
    ['defaultPort','int','默认 UDP 端口',7777],['connectionTimeout','float','连接超时（秒）',20],['inputTimeout','float','输入失联停止（秒）',0.3],
    ['snapshotChunkBytes','int','每个快照分块的最大字节数',4096],['chunksPerFrame','int','每帧最多发送的快照块数',8],['checkpointStamps','int','触发滚动检查点的涂色事件数',512],['paintThreshold','float','地面归属阈值（0 到 1）',0.5],['maxPaintMemoryMiB','int','涂色 RT 内存预算（MiB）',128]]
};
const source=path.join(root,'Config/Luban/source');
await fs.mkdir(path.join(source,'Defines'),{recursive:true});
const schemas=['<module name="">'];
for (const [name, fields] of Object.entries(tables)) {
  const wb=Workbook.create(); const sheet=wb.worksheets.add(name);
  const rows=[['##var',...fields.map(x=>x[0])],['##type',...fields.map(x=>x[1])],['##',...fields.map(x=>x[2])],['',...fields.map(x=>x[3])]];
  sheet.getRangeByIndexes(0,0,4,rows[0].length).values=rows;
  const area=sheet.getRangeByIndexes(0,0,4,rows[0].length);
  area.format.font.name='Microsoft YaHei';area.format.font.size=10;area.format.columnWidth=22;
  sheet.getRangeByIndexes(0,0,2,rows[0].length).format.fill='#E8EEF4';
  sheet.getRangeByIndexes(2,0,1,rows[0].length).format.fill='#243E56';
  sheet.getRangeByIndexes(2,0,1,rows[0].length).format.font.color='#FFFFFF';
  sheet.getRangeByIndexes(2,0,1,rows[0].length).format.wrapText=true;
  sheet.getRangeByIndexes(2,0,1,rows[0].length).format.rowHeight=42;
  wb.recalculate();
  const out=await SpreadsheetFile.exportXlsx(wb);await out.save(path.join(source,`Tb${name}.xlsx`));
  const preview=await wb.render({sheetName:name,range:`A1:${String.fromCharCode(65+Math.min(fields.length,7))}4`,scale:1.5});
  await fs.mkdir(path.join(root,'Temp/InkTablePreviews'),{recursive:true});
  await fs.writeFile(path.join(root,'Temp/InkTablePreviews',`${name}.png`),new Uint8Array(await preview.arrayBuffer()));
  schemas.push(`  <bean name="${name}Config" comment="${name} 强类型配置">`);
  for(const [key,type,desc] of fields)schemas.push(`    <var name="${key}" type="${type}" comment="${desc}"/>`);
  schemas.push(`  </bean>`,`  <table name="Tb${name}" value="${name}Config" input="Tb${name}.xlsx" mode="map" index="id"/>`);
  console.log(`Tb${name}: ${fields.length} typed fields, one default record`);
}
schemas.push('</module>');
await fs.writeFile(path.join(source,'Defines/gameplay.xml'),schemas.join('\n')+'\n');
