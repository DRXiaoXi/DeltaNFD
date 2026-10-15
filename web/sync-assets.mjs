import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.resolve(__dirname, '..');
const publicDir = path.resolve(__dirname, 'public');
const assetsDest = path.resolve(publicDir, 'assets');
const ackDest = path.resolve(assetsDest, 'acknowledgements');

fs.mkdirSync(ackDest, { recursive: true });

// 1. 同步主图标与反馈群二维码
const copyIfExist = (src, dest) => {
  if (fs.existsSync(src)) fs.copyFileSync(src, dest);
};

copyIfExist(path.join(rootDir, 'src/DeltaNFD/Assets/DeltaNFD.png'), path.join(assetsDest, 'logo.png'));
copyIfExist(path.join(rootDir, 'src/DeltaNFD/Assets/DeltaNFD.ico'), path.join(publicDir, 'favicon.ico'));
copyIfExist(path.join(rootDir, 'src/DeltaNFD/Assets/FeedbackGroupQr.png'), path.join(assetsDest, 'FeedbackGroupQr.png'));
copyIfExist(path.join(rootDir, 'update.json'), path.join(publicDir, 'update.json'));

// 2. 同步致谢墙头像
const ackSrcDir = path.join(rootDir, 'src/DeltaNFD/Assets/Acknowledgements');
if (fs.existsSync(ackSrcDir)) {
  for (const file of fs.readdirSync(ackSrcDir)) {
    copyIfExist(path.join(ackSrcDir, file), path.join(ackDest, file));
  }
}
console.log('[DeltaNFD Docs] 静态资源与 update.json 同步完成！');