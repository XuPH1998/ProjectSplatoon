// Copy the current authoritative templates; never maintain a second set of gameplay defaults.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const source = path.join(repo, 'Config/Luban/source');
if (!process.argv[2]) throw new Error('请指定新的源表目录，例如 Temp/GameplaySourceCopy；不会覆盖当前权威源表。');
const destination = path.resolve(process.argv[2]);
const key = p => p.toLowerCase();
if (key(destination) === key(repo) || key(destination) === key(source) || key(destination).startsWith(key(source) + path.sep))
  throw new Error('输出必须使用独立的新目录。');
const files = ['TbHero.xlsx', 'TbRoomMode.xlsx', 'TbMap.xlsx', 'TbGlobal.xlsx', 'Defines/gameplay.xml'];
for (const file of files) {
  await fs.access(path.join(source, file));
  try { await fs.access(path.join(destination, file)); throw new Error('输出已存在：' + file); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
}
await fs.mkdir(path.join(destination, 'Defines'), { recursive: true });
for (const file of files) await fs.copyFile(path.join(source, file), path.join(destination, file), fs.constants.COPYFILE_EXCL);
console.log('已从当前权威源表创建 ' + files.length + ' 个文件：' + destination);
