# DeltaNFD 介绍页与用户指南（VitePress）

本地开发与构建（在 `web/` 下执行）：

```bash
npm run docs:dev     # 同步静态资源并启动本地预览
npm run docs:build   # 同步静态资源并打包，产物在 `.vitepress/dist/`
```

说明：

- `public/`、`.vitepress/cache/`、`.vitepress/dist/` 均为生成或忽略文件，不要入库；
  `sync-assets.mjs` 会从 `src/DeltaNFD/Assets/` 与根目录 `update.json` 自动同步所需资源。
- GitHub Pages 发布见 `.github/workflows/deploy-docs.yml`，推送 `web/**`、`update.json`
  或 `src/DeltaNFD/Assets/**` 时自动重新部署。
