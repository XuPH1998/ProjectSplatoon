import fs from 'node:fs/promises';
import { FileBlob, SpreadsheetFile } from '@oai/artifact-tool';

const source = 'D:/XPHUNITY/ProjectSplatoon/Config/Luban/source/TbGlobal.xlsx';
const report = 'D:/XPHUNITY/ProjectSplatoon/Reports/BlockTerrain';
const wb = await SpreadsheetFile.importXlsx(await FileBlob.load(source));
const sheet = wb.worksheets.getItem('Global');
await fs.mkdir(report, { recursive: true });
console.log((await wb.inspect({kind:'table', range:'Global!Q1:T4', tableMaxRows:4, tableMaxCols:4, maxChars:2000})).ndjson);
const before = await wb.render({sheetName:'Global', range:'Q1:T4', scale:1, format:'png'});
await fs.writeFile(`${report}/global-before.png`, new Uint8Array(await before.arrayBuffer()));
if (!process.argv.includes('--edit')) process.exit(0);
try { await fs.access(`${report}/TbGlobal.before.xlsx`); } catch { await fs.copyFile(source, `${report}/TbGlobal.before.xlsx`); }
sheet.getRange('A4').clear({applyTo:'contents'});
const fields = [
  ['blockBlocksPerMaterial','int','积木：每点材料可建造格数',32],
  ['blockMainAttackMaxBlocks','int','积木：主攻击整组建造上限',512],
  ['blockAuxiliaryBudgetFraction','float','积木：沿途与脚下共享预算比例',.25],
  ['blockFriendlyBlastRadiusFraction','float','积木：己方爆破半径比例',.5],
  ['blockBlocksPerSpecialPoint','int','积木：每点大招充能所需实际变化格数',32],
  ['blockMainSpecialPointCap','int','积木：主攻击整组大招收益上限',2],
  ['blockSubSpecialPointCap','int','积木：副武器整次使用大招收益上限',10],
  ['blockNetworkBytesPerSecond','int','积木：全房间传输预算（字节/秒）',2097152],
];
for (let i=0;i<fields.length;i++) {
  const dest=sheet.getRangeByIndexes(0,20+i,4,1);
  dest.copyFrom(sheet.getRange('T1:T4'),'all');
  dest.values=fields[i].map(value=>[value]);
  dest.format.columnWidth=36;
}
wb.recalculate();
console.log((await wb.inspect({kind:'table', range:'Global!U1:AB4', tableMaxRows:4,tableMaxCols:8,maxChars:2500})).ndjson);
const after=await wb.render({sheetName:'Global',range:'U1:AB4',scale:1,format:'png'});
await fs.writeFile(`${report}/global-block-settings.png`,new Uint8Array(await after.arrayBuffer()));
await (await SpreadsheetFile.exportXlsx(wb)).save(source);
