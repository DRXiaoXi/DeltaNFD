import { defineConfig } from 'vitepress'

// 自动读取 GitHub Actions 注入的仓库子路径，本地开发时默认回退为 '/'
const rawBase = process.env.VITEPRESS_BASE || '/'
const base = rawBase.endsWith('/') ? rawBase : `${rawBase}/`

export default defineConfig({
  lang: 'zh-CN',
  title: '三角帧不掉洲 (DeltaNFD)',
  description: 'UP主「逐梦之子-晓夕」开源制作 · 专为《三角洲行动》打造的免费系统调优与环境修复工具',
  base: base,
  srcExclude: ['README.md'], // web/README.md 仅为工程说明，不发布为页面
  appearance: 'force-dark', // 强制启用战术暗黑模式
  head: [
    ['link', { rel: 'icon', href: `${base}favicon.ico` }],
    ['meta', { name: 'theme-color', content: '#00e65c' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:title', content: '三角帧不掉洲 (DeltaNFD) · 永久免费开源的三角洲帧数优化工具' }],
    ['meta', { property: 'og:description', content: '集着色器/ACE/运行库环境体检、帧格运行时稳帧、AMD双CCD独占调度、网吧免重启显卡伪装于一体。' }],
  ],
  themeConfig: {
    logo: '/assets/logo.png',
    siteTitle: '三角帧不掉洲 · DeltaNFD',
    nav: [
      { text: '首页', link: '/' },
      { text: '快速上手', link: '/guide/quick-start' },
      { text: '插件开发', link: '/guide/plugin-dev' },
      { text: '📺 B站演示视频', link: 'https://www.bilibili.com/video/BV1AWHW6hEBE/' },
      {
        text: '📦 下载与发行版',
        items: [
          { text: '⚡ 夸克网盘下载 (提取码: A691)', link: 'https://pan.quark.cn/s/ea7f97614465' },
          { text: '🐙 GitHub Releases (版本发行页)', link: 'https://github.com/DRXiaoXi/DeltaNFD/releases' },
          { text: '🔧 本站文档与 Fork 源码 (xlht4106)', link: 'https://github.com/xlht4106/deltanfd' }
        ]
      }
    ],
    sidebar: {
      '/guide/': [
        {
          text: '📖 使用与排障指南',
          items: [
            { text: '🚀 快速上手与全模块指南', link: '/guide/quick-start' },
            { text: '🔌 拓展插件使用与开发规范', link: '/guide/plugin-dev' }
          ]
        },
        {
          text: '🔗 常用资源直达',
          items: [
            { text: '⚡ 夸克网盘下载 (提取码: A691)', link: 'https://pan.quark.cn/s/ea7f97614465' },
            { text: '📺 B站 14分钟演示视频', link: 'https://www.bilibili.com/video/BV1AWHW6hEBE/' },
            { text: '🐙 GitHub Releases (版本发行页)', link: 'https://github.com/DRXiaoXi/DeltaNFD/releases' }
          ]
        }
      ]
    },
    socialLinks: [
      { icon: 'github', link: 'https://github.com/DRXiaoXi/DeltaNFD' }
    ],
    footer: {
      message: '基于 GPL-3.0-only 永久免费开源 · 纯系统级合法调优 · 六后端按需隔离 · 全操作可追踪可回滚',
      copyright: '软件原作者：B 站 @逐梦之子-晓夕 (DRXiaoXi) | 网页与文档构建维护：xlht4106'
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
      label: '本页目录导航',
      level: [2, 3]
    }
  }
})
