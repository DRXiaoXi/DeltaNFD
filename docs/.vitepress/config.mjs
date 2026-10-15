import { defineConfig } from 'vitepress'

// 自动读取 GitHub Actions 注入的仓库子路径，本地开发时默认回退为 '/'
const rawBase = process.env.VITEPRESS_BASE || '/'
const base = rawBase.endsWith('/') ? rawBase : `${rawBase}/`

export default defineConfig({
  lang: 'zh-CN',
  title: '三角帧不掉洲 (DeltaNFD)',
  description: 'UP主「逐梦之子-晓夕」开源制作 · 专为《三角洲行动》打造的免费系统调优与环境修复工具',
  base: base,
  appearance: 'force-dark', // 强制启用战术暗黑模式
  head: [
    ['link', { rel: 'icon', href: `${base}favicon.ico` }],
    ['meta', { name: 'theme-color', content: '#00e65c' }]
  ],
  themeConfig: {
    logo: '/assets/logo.png',
    siteTitle: '三角帧不掉洲 · DeltaNFD',
    nav: [
      { text: '首页', link: '/' },
      { text: '快速上手', link: '/guide/quick-start' },
      { text: '插件开发', link: '/guide/plugin-dev' },
      { text: '版本发行 (Releases)', link: 'https://github.com/xlht4106/deltanfd/releases' }
    ],
    sidebar: {
      '/guide/': [
        {
          text: '使用指南',
          items: [
            { text: '快速上手与环境整备', link: '/guide/quick-start' }
          ]
        },
        {
          text: '开发者生态',
          items: [
            { text: '插件开发与 UI 规范', link: '/guide/plugin-dev' }
          ]
        }
      ]
    },
    socialLinks: [
      { icon: 'github', link: 'https://github.com/xlht4106/deltanfd' }
    ],
    footer: {
      message: '基于 GPL-3.0-only 永久免费开源 · 纯系统级合法调优 · 六后端按需隔离 · 全操作可追踪可回滚',
      copyright: 'Copyright © 2025-2026 逐梦之子-晓夕 & DeltaNFD Contributors'
    },
    search: {
      provider: 'local',
      options: {
        translations: {
          button: {
            buttonText: '搜索文档',
            buttonAriaLabel: '搜索文档'
          },
          modal: {
            noResultsText: '无法找到相关结果',
            resetButtonTitle: '清除查询条件',
            footer: {
              selectText: '选择',
              navigateText: '切换',
              closeText: '关闭'
            }
          }
        }
      }
    },
    docFooter: {
      prev: '上一篇',
      next: '下一篇'
    },
    outline: {
      label: '本页目录',
      level: [2, 3]
    }
  }
})
