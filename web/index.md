---
layout: page
title: 三角帧不掉洲 (DeltaNFD) - 专为《三角洲行动》打造的开源系统调优与环境修复工具
sidebar: false
---

<script setup>
import { onMounted, ref } from 'vue'
import { withBase } from 'vitepress'

const releasesUrl = 'https://github.com/DRXiaoXi/DeltaNFD/releases'
const bilibiliUrl = 'https://space.bilibili.com/630440925'
const showQrModal = ref(false)
const demoFixed = ref(false)

// 首屏主按钮：静态文案不写死版本号，能连上 GitHub API 时再补上最新一条发行的标签
const downloadTextId = 'dnfd-download-text'
const releaseCacheKey = 'dnfd_latest_release_tag'
const releasesApiUrl = 'https://api.github.com/repos/DRXiaoXi/DeltaNFD/releases?per_page=1'

onMounted(async () => {
  const downloadTextEl = document.getElementById(downloadTextId)
  if (!downloadTextEl) return

  // 1. 同一个会话里探测过一次就直接复用，避免反复请求触发 GitHub API 限流
  try {
    const cachedTag = sessionStorage.getItem(releaseCacheKey)
    if (cachedTag) {
      downloadTextEl.textContent = `前往 Releases 下载 (${cachedTag})`
      return
    }
  } catch (_) {
    // 隐私模式下 sessionStorage 不可用时忽略缓存，继续实时探测
  }

  // 2. 带 3.5 秒超时的动态版本探测；读 /releases 列表，Pre-release 预发布版同样能取到
  const controller = new AbortController()
  const timeoutId = setTimeout(() => controller.abort(), 3500)

  try {
    const response = await fetch(releasesApiUrl, {
      signal: controller.signal,
      headers: { Accept: 'application/vnd.github.v3+json' }
    })
    clearTimeout(timeoutId)
    if (!response.ok) return

    const releases = await response.json()
    const latest = Array.isArray(releases) ? releases[0] : null
    const rawTag = latest && !latest.draft ? String(latest.tag_name || latest.name || '').trim() : ''
    if (!rawTag) return

    downloadTextEl.textContent = `前往 Releases 下载 (${rawTag})`
    try {
      sessionStorage.setItem(releaseCacheKey, rawTag)
    } catch (_) {}
  } catch (_) {
    // 3. 国内网络连不上 GitHub API（超时、限流、被拦截）时静默降级，保留通用文案
    clearTimeout(timeoutId)
  }
})

const painPoints = [
  {
    mark: 'DRIVER & SHADER',
    title: 'N 卡驱动避坑与三步预热法',
    desc: '确保驱动 <strong>≥ 572.83</strong>，且必须避开 <strong>591 系、610 系、616 系及 620.14</strong>（会导致缺失 256MB 缓存并产生 0KB 异常文件）。推荐 <strong>30 系及以下使用 576.88</strong>，<strong>40 / 50 系使用 596.49</strong>。',
    note: '清理着色器后：先在游戏大厅静止几分钟等 CPU 占用平稳 → 进靶场跑完 → 退出重进游戏即可满血。'
  },
  {
    mark: 'DLSS & SPOOF',
    title: '烟雾火焰马赛克修复与网吧伪装',
    desc: 'N 卡设置支持修改 DLSS 超分模型（推荐 <strong>K 最均衡 / F 损耗最低 / L 最清晰</strong>）。针对烟雾火焰糊成马赛克，可按画质档位调整「抗锯齿透明度」：<strong>质量档开 ×2、平衡档开 ×4、性能档开 ×8</strong>。',
    note: '8G 及以上显存建议开启「显存不回退」减少掉帧；网吧玩家可用显卡伪装「临时模式 + 立即重启显卡」免关机解锁隐藏最低画质。'
  },
  {
    mark: 'DWM & ACE',
    title: 'DWM 占用暴涨自愈与 ACE 清理',
    desc: '若游戏启动前 <code>dwm.exe</code> 占用极低、启动后 CPU 占用飙升，开启「帧格」即可自动干预恢复正常。此外，当 <code>ACE-Base/Core</code> 驱动超过 2 个及以上文件时会引发局内外卡顿，支持一键清理。',
    note: '实测 Intel 显卡玩家 100% 遭遇 DWM 占用 Bug；清理 ACE 后用启动器或 WeGame 执行游戏修复即可。'
  },
  {
    mark: 'CPU & SYSTEM',
    title: '双 CCD 分模式调度与系统减负',
    desc: '台式机可导入「无省电电源计划」。AMD 双 CCD 在<strong>全面战场 / 烽火地带</strong>下可将其他进程驱赶至另一侧 CCD，让游戏独占整颗 CCD 缓存与核心；系统优化优先执行「UP 推荐」分类即可。',
    note: '注意：「黑潮爆破」模式官方已优化调度，关闭双 CCD 专属调度反而温度更低、画面更流畅；内存 &gt;24GB 可关闭内存压缩。'
  }
]

const coreModules = [
  {
    id: 'diag',
    badge: '环境体检',
    title: '🩺 着色器 / ACE / 运行库红绿灯',
    desc: '识别 N 卡问题驱动导致的 0KB 残缺着色器与多 GameVer 目录冲突；清理冗余 ACE-CORE 驱动；重装内置 14.42 C++ 运行库并用 IFEO 防护阻止游戏静默覆盖。',
    tags: ['0KB / GameVer 归因', 'ACE-CORE 冗余清理', '14.42 运行库基线', '基础 / 完全防护']
  },
  {
    id: 'frame',
    badge: '帧格',
    title: '⚡ 运行时持续稳帧',
    desc: '集成电源计划 30 秒巡检锁定、防核心停放、禁用游戏进程 EcoQoS、0.5ms 定时器、前台响应加速、网卡 / USB / PCIe 省电控制，以及目标游戏启动后的 DWM 自愈。',
    tags: ['DWM 异常自愈', '电源计划锁定', '0.5ms 定时器', '外设省电控制']
  },
  {
    id: 'cpulab',
    badge: 'CPU 实验室',
    title: '🎯 AMD 双 CCD 独占调度',
    desc: '台式机可一键导入「无省电电源计划」。AMD 双 CCD 平台可通过硬约束让游戏独占一侧 CCD，并通过 CPU Sets 软约束将后台进程驱赶至另一侧，同时支持单 CCD「排除 CPU0」。',
    tags: ['无省电电源计划', '双 CCD 独占', 'CPU Sets 驱赶', '排除 CPU0']
  },
  {
    id: 'gpu',
    badge: '显卡伪装与 N 卡设置',
    title: '🚀 隐藏画质解锁与 NvAPI 调优',
    desc: '修改注册表中的显卡描述以尝试激活隐藏最低画质，支持临时模式与立即重载；直连 NvAPI 调整 DLSS 超分模型、抗锯齿透明采样与 CUDA 显存回退策略。',
    tags: ['临时伪装', '立即重载显卡', 'DLSS F / K / M / L', '显存偏好不回退']
  },
  {
    id: 'sysopt',
    badge: '系统优化',
    title: '🧰 UP 推荐与虚拟内存管理',
    desc: '以 UP 推荐分类为起点，涵盖暂停百年更新、检查并关闭自动加密、禁用遥测 / 快速启动 / VBS / 预读取、关闭鼠标加速及 Win11 24H2 区域内存优化（>24GB 内存可关内存压缩与分页合并），并支持固定虚拟内存最小值防止扩容卡顿。',
    tags: ['UP 推荐分类', 'BitLocker 预防', '大内存去压缩', '页面文件固定'],
  },
  {
    id: 'extra',
    badge: '进程与插件',
    title: '🔌 跨游戏调度与插件生态',
    desc: '「不玩三角洲辣」可指定其它游戏主进程；后台管理提供受保护的自动内存回收；拓展插件支持导入 .dnfdplugin 包、查看运行历史、恢复备份并按权限逐项授权。',
    tags: ['自定义游戏目标', '后台内存回收', '.dnfdplugin 插件', '逐项权限授权']
  }
]

const acknowledgements = [
  { name: '14代耐电王', role: '头号测试员 · 运行库帧数与延迟实测', avatar: '/assets/acknowledgements/lead-tester.jpg', highlight: true },
  { name: '上官赐福', role: '提供 648 元资金支持', avatar: '/assets/acknowledgements/shangguan-cash-support.jpg', highlight: true },
  { name: '怜爱', role: 'Token 提供者 · 算力与 API 中转支持', avatar: '/assets/acknowledgements/token-provider.jpg', highlight: true },
  { name: '无数不留姓名的彦祖', role: 'Bug 排雷员 · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-anonymous-yanzhu.png' },
  { name: '九妹亡命天涯', role: 'Bug 排雷员 · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-jiemei-wangming.jpg' },
  { name: '目夺', role: 'Bug 排雷员 · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-muduo.jpg' },
  { name: '葡萄酒吐司', role: 'Bug 排雷员 · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-putaojiu-tusi.jpg' },
  { name: '无机盐', role: 'Bug 排雷员 · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-wujiyan.jpg' },
  { name: '梓', role: 'Bug 排雷员 · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-zi.jpg' }
]
</script>

<div class="dn-landing">
  <section class="dn-hero">
    <div class="dn-hero-badge">
      <span class="dn-pulse"></span>
      <span>B 站 UP 主「逐梦之子-晓夕」开源力作 · 永久免费 · 完全开源</span>
    </div>
    <img class="dn-hero-logo" :src="withBase('/assets/logo.png')" alt="三角帧不掉洲 Logo" />
    <h1 class="dn-hero-title">
      <span>三角帧不掉洲</span> (DeltaNFD)
    </h1>
    <p class="dn-hero-subtitle">
      针对《三角洲行动》优化痛点多、玩家排查无门的现状，B 站 UP 主<strong>「逐梦之子-晓夕」</strong>将整套硬核排障经验浓缩成这款开源工具。集 <strong>着色器 / ACE / 运行库环境体检</strong>、<strong>帧格运行时稳帧</strong>、<strong>AMD 双 CCD 独占调度</strong>、<strong>网吧免重启显卡伪装</strong> 与 <strong>跨游戏进程调度</strong> 于一体，拒绝粗制滥造的“AI 许愿圈米工具”。
    </p>
    <div class="dn-cta-group">
      <a class="dn-btn dn-btn-primary" :href="releasesUrl" target="_blank" rel="noopener">
        <span id="dnfd-download-text">前往 Releases 下载最新版</span>
      </a>
      <a href="https://pan.quark.cn/s/ea7f97614465" rel="noopener" target="_blank" class="dn-btn dn-btn-secondary dnfd-btn-quark">
        <span>⚡ 夸克网盘下载 (提取码: A691)</span>
      </a>
      <a href="https://www.bilibili.com/video/BV1AWHW6hEBE/" rel="noopener" target="_blank" class="dn-btn dn-btn-secondary dnfd-btn-bili">
        <span>📺 14分钟原理与实机演示</span>
      </a>
      <a class="dn-btn dn-btn-secondary" :href="withBase('/guide/quick-start')">
        <span>📖 快速上手指南</span>
      </a>
      <button class="dn-btn dn-btn-outline" type="button" @click="showQrModal = true">
        <span>加入反馈交流群</span>
      </button>
    </div>
    <div class="dn-hero-meta">
      <span>Windows 10 / 11 x64</span>
      <span class="dn-meta-divider">|</span>
      <a :href="bilibiliUrl" target="_blank" rel="noopener">B 站 @逐梦之子-晓夕</a>
      <span class="dn-meta-divider">|</span>
      <a :href="releasesUrl" target="_blank" rel="noopener">历史发行版与校验码</a>
    </div>
    <div class="dnfd-sac-alert">
      <span class="dnfd-sac-badge">⚠️ 必看提示</span>
      <span>若工具运行被拦截，请进入 <strong>Windows 安全中心 → 应用和浏览器控制 → 智能应用控制</strong> 将其关闭。公测期如遇 Bug 或掉帧，请前往 B 站视频置顶评论加群反馈。</span>
    </div>
    <div class="dn-console-preview">
      <div class="dn-console-bar">
        <div class="dn-dots"><span></span><span></span><span></span></div>
        <div class="dn-console-title">三角帧不掉洲 · 主页体检与一键帧格</div>
        <button class="dn-demo-toggle" type="button" :aria-pressed="demoFixed" @click="demoFixed = !demoFixed">
          {{ demoFixed ? '查看异常状态' : '模拟一键修复' }}
        </button>
      </div>
      <div class="dn-ui-mockup">
        <div class="dn-ui-quickbar">
          <div>
            <strong>一键帧格模式</strong>
            <p>{{ demoFixed ? '帧格模式运行中：电源计划锁定 · 加速响应 · 前台加速 · 省电延迟 · DWM 自愈' : '开启后联动已配置的帧格功能；各功能开关仍由帧格页独立控制' }}</p>
          </div>
          <button class="dn-switch-pill" type="button" :class="{ active: demoFixed }" :aria-pressed="demoFixed" @click="demoFixed = !demoFixed">
            {{ demoFixed ? '已开启' : '待开启' }}
          </button>
        </div>
        <div class="dn-ui-check-list">
          <div class="dn-ui-check-header">
            组件检测状态：红色项表示当前存在拖累帧数的风险，点击「去处理」可跳转到对应指南
          </div>
          <div class="dn-ui-check-row">
            <span class="dn-dot" :class="demoFixed ? 'ok' : 'err'"></span>
            <strong class="dn-row-name">着色器</strong>
            <span class="dn-row-msg" :class="demoFixed ? 'ok-text' : 'err-text'">
              {{ demoFixed ? '正常 · 驱动 596.49 · 无 0KB 空文件 · 2 个 ≥256MB NVPH' : '检测到 2 个 GameVer 文件夹，并存在 0KB NVPH 异常特征' }}
            </span>
            <a class="dn-row-btn" :href="withBase('/guide/quick-start#_1-着色器维护-解决编译掉帧与切换异常')">去处理 →</a>
          </div>
          <div class="dn-ui-check-row">
            <span class="dn-dot" :class="demoFixed ? 'ok' : 'err'"></span>
            <strong class="dn-row-name">ACE</strong>
            <span class="dn-row-msg" :class="demoFixed ? 'ok-text' : 'err-text'">
              {{ demoFixed ? '正常 · 仅 1 个 ACE-CORE 驱动文件，无多版本冗余' : '检测到 3 个 ACE-CORE 系列 .sys 文件，存在多版本冗余风险' }}
            </span>
            <a class="dn-row-btn" :href="withBase('/guide/quick-start#_2-ace-反作弊组件清理-解决局内外周期性顿卡')">去处理 →</a>
          </div>
          <div class="dn-ui-check-row">
            <span class="dn-dot" :class="demoFixed ? 'ok' : 'err'"></span>
            <strong class="dn-row-name">运行库</strong>
            <span class="dn-row-msg" :class="demoFixed ? 'ok-text' : 'err-text'">
              {{ demoFixed ? '已安装官方 14.42 基线 · 运行库防护开启中' : '检测到游戏可能静默覆盖 v14 运行库，建议重新校准并开启防护' }}
            </span>
            <a class="dn-row-btn" :href="withBase('/guide/quick-start#_3-运行库重装与防篡改守护-解决静默更新掉帧')">去处理 →</a>
          </div>
        </div>
      </div>
    </div>
  </section>
  <section class="dn-section">
    <div class="dn-section-header">
      <span class="dn-eyebrow">REAL-WORLD BENCHMARKS</span>
      <h2>拒绝玄学安慰剂，用实机帧数与帧时间说话</h2>
      <p>以下数据来自原作者演示视频中的实机对比，仅代表对应硬件与游戏版本</p>
    </div>
    <div class="dn-bench-grid">
      <article class="dn-bench-card">
        <h3>📊 C++ 运行库版本实测</h3>
        <p class="dn-bench-sub">测试协助：@14代耐电王 · 同一靶场点位对比</p>
        <div class="dn-bar-group">
          <div class="dn-bar-label">
            <span><strong>内置 14.42 版</strong> · 帧数最高，工具默认集成</span>
            <strong class="dn-green">351 FPS <em class="dnfd-fps-tag dnfd-fps-up">+49 FPS (+16.2%)</em></strong>
          </div>
          <div class="dn-bar-track"><div class="dn-bar-fill fill-best dn-bar-100"></div></div>
        </div>
        <div class="dn-bar-group">
          <div class="dn-bar-label">
            <span><strong>14.40 版</strong> · 帧数适中，操作延迟最低</span>
            <strong class="dn-cyan">334 FPS <em class="dnfd-fps-tag dnfd-fps-lowlat">延迟最低</em></strong>
          </div>
          <div class="dn-bar-track"><div class="dn-bar-fill fill-mid dn-bar-95"></div></div>
        </div>
        <div class="dn-bar-group">
          <div class="dn-bar-label">
            <span><strong>游戏静默覆盖版本</strong> · 帧数明显缩水</span>
            <strong class="dn-red">302 FPS <em class="dnfd-fps-tag dnfd-fps-down">静默负优化</em></strong>
          </div>
          <div class="dn-bar-track"><div class="dn-bar-fill fill-bad dn-bar-86"></div></div>
        </div>
        <p class="dn-bench-foot">工具提供运行库一键重装与 IFEO 防护（基础拦截 v14 安装包 / 完全防护拦截所有 C++ 安装）。<strong class="dnfd-text-warn">⚠️ 注意：重装运行库会卸载现有 VC++ 并导致当前运行的 C++ 依赖软件全部崩溃，重装时请勿运行其他软件；若后续安装驱动或其它软件被拦截，先关闭防护并清理拦截即可。</strong></p>
      </article>
      <article class="dn-bench-card">
        <h3>💻 笔记本过热环境实战</h3>
        <p class="dn-bench-sub">华硕天选 A16 · R9 7940HX 双 CCD · RTX 4070 Laptop 8G · 32G DDR5</p>
        <ul class="dn-highlight-list">
          <li>
            <strong>测试条件：</strong>未带风压散热器，CPU 处于过热运行状态，全程不开启帧生成。
          </li>
          <li>
            <strong>全面战场与烽火地带：</strong>开启双 CCD 专属调度、帧格与环境整备后，实战稳定 <strong>200 ~ 245+ FPS</strong>，帧时间约 <strong>3.6ms ~ 5.1ms</strong>。
          </li>
          <li>
            <strong>黑潮爆破：</strong>该模式官方已有特殊调度，关闭双 CCD 调度后温度更低，仍约 <strong>200+ FPS</strong>、<strong>3.1ms ~ 4.1ms</strong>。
          </li>
        </ul>
        <p class="dn-bench-foot">这是单台设备的作者实测，不是跨硬件通用承诺。笔记本请优先确认散热与功耗释放。</p>
      </article>
    </div>
  </section>
  <section class="dn-section">
    <div class="dn-section-header">
      <span class="dn-eyebrow">SCENARIO-BASED TROUBLESHOOTING</span>
      <h2>四大高频症状与黄金参数速查</h2>
      <p>拒绝盲目乱调，直接对照视频实测结论按症状下药</p>
    </div>
    <div class="dn-pain-grid">
      <article class="dn-pain-card" v-for="item in painPoints" :key="item.mark">
        <span class="dn-pain-mark">{{ item.mark }}</span>
        <h3>{{ item.title }}</h3>
        <p v-html="item.desc"></p>
        <span class="dn-pain-note" v-html="item.note"></span>
      </article>
    </div>
  </section>
  <section class="dn-section">
    <div class="dn-section-header">
      <span class="dn-eyebrow">CORE CAPABILITIES</span>
      <h2>全方位覆盖《三角洲行动》的性能痛点</h2>
      <p>从驱动、反作弊冗余、运行库版本到异构 CPU 调度的一站式解决方案</p>
    </div>
    <div class="dn-bento-grid">
      <article class="dn-bento-card" v-for="mod in coreModules" :key="mod.id">
        <div class="dn-bento-badge">{{ mod.badge }}</div>
        <h3>{{ mod.title }}</h3>
        <p>{{ mod.desc }}</p>
        <div class="dn-tag-list">
          <span class="dn-tag" v-for="tag in mod.tags" :key="tag">{{ tag }}</span>
        </div>
      </article>
    </div>
  </section>
  <section class="dn-section dn-safety-box">
    <div class="dn-section-header">
      <span class="dn-eyebrow">SAFETY & ARCHITECTURE</span>
      <h2>纯系统级合法调优 · 可追踪、可回滚、可熔断</h2>
      <p>不修改游戏内存与客户端文件，后端按模块隔离运行</p>
    </div>
    <div class="dn-safety-grid">
      <article class="dn-safety-item">
        <div class="dn-safety-num">01</div>
        <h4>备份与还原机制</h4>
        <p>对支持回滚的注册表、服务、电源计划与驱动配置保存原值，并提供单项恢复、一键恢复或强制清理入口。</p>
      </article>
      <article class="dn-safety-item">
        <div class="dn-safety-num">02</div>
        <h4>多后端隔离与未确认操作核对</h4>
        <p>官方后端按功能模块拆分运行。超时、断线或异常退出时保留证据并要求人工核对，不把未知结果当作成功。</p>
      </article>
      <article class="dn-safety-item">
        <div class="dn-safety-num">03</div>
        <h4>签名紧急熔断策略</h4>
        <p>紧急策略通道使用数字签名与固定信任根校验。游戏或 Windows 更新引发兼容问题时，可熔断对应高风险命令。</p>
      </article>
      <article class="dn-safety-item">
        <div class="dn-safety-num">04</div>
        <h4>永久免费与 GPL-3.0 开源</h4>
        <p>前端、官方后端、插件规范与测试工具均可公开审计。历史入口的残留状态会在升级后按提示恢复，再进入当前功能。</p>
      </article>
    </div>
  </section>
  <section class="dn-section">
    <div class="dn-section-header">
      <span class="dn-eyebrow">HALL OF FAME</span>
      <h2>社区荣誉与特别致谢</h2>
      <p>“我只是个医学生，能把脑海中的构想变成这款开源软件，离不开各位粉丝的测试、算力与资金支持。” —— <strong>原作者 @逐梦之子-晓夕</strong></p>
    </div>
    <div class="dn-ack-grid">
      <article
        class="dn-ack-card"
        :class="{ 'is-highlight': person.highlight, 'dnfd-hof-core': person.highlight }"
        v-for="person in acknowledgements"
        :key="person.name"
      >
        <img :src="withBase(person.avatar)" :alt="person.name" loading="lazy" />
        <div class="dn-ack-info">
          <strong>{{ person.name }}</strong>
          <span>{{ person.role }}</span>
        </div>
      </article>
    </div>
  </section>
  <section class="dn-section">
    <div class="dn-section-header">
      <span class="dn-eyebrow">EVOLUTION</span>
      <h2>持续迭代 · 紧跟游戏与系统更新</h2>
      <p>正式发布前仍会保留未确认操作核对与回滚义务</p>
    </div>
    <div class="dn-timeline">
      <article class="dn-time-node">
        <span class="dn-ver">Beta0.9192 / Alpha0.915</span>
        <p>运行库 v14 卸载与 14.42 基线重建流程继续加固；下线旧脱机模式与帧格全局内存清理，改用后台内存回收、逐插件授权和可核对恢复记录。</p>
      </article>
      <article class="dn-time-node">
        <span class="dn-ver">v0.915 / v0.914</span>
        <p>强化多后端进程隔离、运行库诊断与恢复、插件授权和声明式界面渲染，完善 NVIDIA 驱动配置与着色器缓存维护。</p>
      </article>
      <article class="dn-time-node">
        <span class="dn-ver">v0.91 / v0.89</span>
        <p>奠定系统调优数据库、ACE 环境检测、异构 CPU 调度和帧格运行时优化的基础架构。</p>
      </article>
    </div>
  </section>
  <div
    class="dn-modal-mask"
    v-if="showQrModal"
    role="dialog"
    aria-modal="true"
    aria-label="反馈交流群二维码"
    @click.self="showQrModal = false"
  >
    <div class="dn-modal-card">
      <h3>扫码加入「三角帧不掉洲」反馈群</h3>
      <p>遇到 Bug 或帧数异常时，可以在群内直接反馈给作者 @逐梦之子-晓夕。</p>
      <img :src="withBase('/assets/FeedbackGroupQr.png')" alt="反馈交流群二维码" />
      <button class="dn-btn dn-btn-primary" type="button" @click="showQrModal = false">关闭窗口</button>
    </div>
  </div>
</div>

<style scoped>
.dn-landing {
  max-width: 1180px;
  margin: 0 auto;
  padding: 2rem 1.5rem 5rem;
  color: #e6edf3;
}

.dn-hero {
  text-align: center;
  padding: 2.5rem 0 3.5rem;
}

.dn-hero-badge {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 6px 16px;
  border-radius: 999px;
  background: rgba(0, 230, 92, 0.08);
  border: 1px solid rgba(0, 230, 92, 0.3);
  color: #6ee7a0;
  font-size: 0.85rem;
  font-weight: 600;
  margin-bottom: 1.5rem;
}

.dn-pulse {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #00e65c;
  box-shadow: 0 0 10px rgba(0, 230, 92, 0.8);
}

.dn-hero-logo {
  width: 88px;
  height: 88px;
  margin: 0 auto 1.2rem;
  border-radius: 18px;
  box-shadow: 0 0 35px rgba(0, 230, 92, 0.2);
}

.dn-hero-title {
  font-size: 3rem;
  font-weight: 800;
  line-height: 1.15;
  letter-spacing: 0;
  margin: 0 0 1.2rem;
}

.dn-hero-title span {
  color: #7ef0a7;
}

.dn-hero-subtitle {
  max-width: 840px;
  margin: 0 auto 2rem;
  font-size: 1.08rem;
  line-height: 1.75;
  color: #9ba7b4;
}

.dn-hero-subtitle strong {
  color: #e6edf3;
}

.dn-cta-group {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 14px;
  margin-bottom: 1.2rem;
}

.dn-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 44px;
  padding: 0.75rem 1.4rem;
  border-radius: 8px;
  font-weight: 700;
  font-size: 0.96rem;
  text-decoration: none !important;
  transition: transform 0.2s ease, border-color 0.2s ease, background 0.2s ease, box-shadow 0.2s ease;
  cursor: pointer;
}

.dn-btn-primary {
  background: #00e65c;
  color: #041208 !important;
  box-shadow: 0 8px 24px rgba(0, 230, 92, 0.22);
  border: 1px solid #00e65c;
}

.dn-btn-primary:hover {
  transform: translateY(-2px);
  box-shadow: 0 12px 28px rgba(0, 230, 92, 0.3);
}

.dn-btn-secondary {
  background: #161f2c;
  color: #e6edf3 !important;
  border: 1px solid #2a3648;
}

.dn-btn-secondary:hover {
  border-color: #00e65c;
  background: #1c2838;
}

.dn-btn-outline {
  background: transparent;
  color: #7ef0a7;
  border: 1px solid rgba(0, 230, 92, 0.4);
}

.dn-btn-outline:hover {
  background: rgba(0, 230, 92, 0.1);
}

.dn-hero-meta {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  align-items: center;
  gap: 10px;
  font-size: 0.85rem;
  color: #7d8997;
  margin-bottom: 2.8rem;
}

.dn-hero-meta a {
  color: #a9b6c5;
  text-decoration: none;
}

.dn-hero-meta a:hover {
  color: #7ef0a7;
}

.dn-meta-divider {
  opacity: 0.3;
}

.dn-console-preview {
  background: #0d131c;
  border: 1px solid #1f2b3d;
  border-radius: 12px;
  overflow: hidden;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.55), 0 0 40px rgba(0, 230, 92, 0.05);
  text-align: left;
}

.dn-console-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px;
  padding: 12px 18px;
  background: #121a26;
  border-bottom: 1px solid #1f2b3d;
  font-size: 0.82rem;
  color: #a9b6c5;
}

.dn-dots {
  display: flex;
  gap: 6px;
}

.dn-dots span {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: #2b384b;
}

.dn-demo-toggle {
  background: rgba(0, 230, 92, 0.1);
  border: 1px solid rgba(0, 230, 92, 0.35);
  color: #7ef0a7;
  padding: 5px 12px;
  border-radius: 6px;
  font-size: 0.78rem;
  font-weight: 600;
  cursor: pointer;
}

.dn-demo-toggle:hover {
  background: rgba(0, 230, 92, 0.18);
}

.dn-ui-mockup {
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.dn-ui-quickbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 16px 20px;
  background: #131c29;
  border: 1px solid #223044;
  border-radius: 10px;
}

.dn-ui-quickbar strong {
  font-size: 1.05rem;
  color: #f1f5f9;
}

.dn-ui-quickbar p {
  margin: 4px 0 0;
  font-size: 0.84rem;
  color: #8b98a9;
}

.dn-switch-pill {
  padding: 7px 16px;
  border-radius: 999px;
  border: 1px solid #334155;
  font-size: 0.82rem;
  font-weight: 700;
  cursor: pointer;
  background: #263345;
  color: #94a3b8;
  white-space: nowrap;
}

.dn-switch-pill.active {
  background: #00e65c;
  border-color: #00e65c;
  color: #041208;
}

.dn-ui-check-list {
  background: #131c29;
  border: 1px solid #223044;
  border-radius: 10px;
  padding: 16px 20px;
}

.dn-ui-check-header {
  font-size: 0.85rem;
  font-weight: 700;
  color: #cbd5e1;
  margin-bottom: 12px;
}

.dn-ui-check-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 0;
  border-top: 1px solid rgba(255, 255, 255, 0.06);
  font-size: 0.88rem;
}

.dn-dot {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  flex-shrink: 0;
}

.dn-dot.err {
  background: #ff5555;
  box-shadow: 0 0 8px rgba(255, 85, 85, 0.75);
}

.dn-dot.ok {
  background: #00e65c;
  box-shadow: 0 0 8px rgba(0, 230, 92, 0.7);
}

.dn-row-name {
  width: 64px;
  flex-shrink: 0;
  color: #f1f5f9;
}

.dn-row-msg {
  flex: 1;
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.err-text {
  color: #ff7b7b;
  font-weight: 600;
}

.ok-text {
  color: #7ef0a7;
}

.dn-row-btn {
  padding: 4px 12px;
  background: #1e293b;
  border: 1px solid #334155;
  border-radius: 6px;
  color: #e2e8f0 !important;
  text-decoration: none !important;
  font-size: 0.78rem;
  flex-shrink: 0;
}

.dn-row-btn:hover {
  border-color: #00e65c;
}

.dn-section {
  padding: 4rem 0 1rem;
}

.dn-section-header {
  text-align: center;
  margin-bottom: 2.5rem;
}

.dn-eyebrow {
  display: inline-block;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.8rem;
  letter-spacing: 0.12em;
  color: #7ef0a7;
  margin-bottom: 0.5rem;
}

.dn-section-header h2 {
  font-size: 2rem;
  font-weight: 800;
  margin: 0 0 0.6rem;
  border: none;
}

.dn-section-header p {
  color: #8b98a9;
  margin: 0;
}

.dn-bench-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(340px, 1fr));
  gap: 20px;
}

.dn-bench-card,
.dn-pain-card,
.dn-bento-card,
.dn-safety-item,
.dn-ack-card,
.dn-time-node {
  background: #0e141f;
  border: 1px solid #1e2a3a;
  border-radius: 12px;
}

.dn-bench-card {
  padding: 24px;
}

.dn-bench-card h3 {
  margin: 0 0 6px;
  font-size: 1.2rem;
}

.dn-bench-sub {
  font-size: 0.82rem;
  color: #7d8997;
  margin: 0 0 20px;
}

.dn-bar-group {
  margin-bottom: 16px;
}

.dn-bar-label {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  font-size: 0.86rem;
  margin-bottom: 6px;
}

.dn-bar-label strong:last-child {
  white-space: nowrap;
}

.dn-bar-track {
  height: 10px;
  background: #162030;
  border-radius: 999px;
  overflow: hidden;
}

.dn-bar-fill {
  height: 100%;
  border-radius: 999px;
}

.dn-bar-100 { width: 100%; }
.dn-bar-95 { width: 95%; }
.dn-bar-86 { width: 86%; }
.fill-best { background: #22c55e; }
.fill-mid { background: #38bdf8; }
.fill-bad { background: #ef4444; }
.dn-green { color: #7ef0a7; }
.dn-cyan { color: #7dd3fc; }
.dn-red { color: #fca5a5; }

.dn-bench-foot {
  margin: 16px 0 0;
  font-size: 0.82rem;
  color: #94a3b8;
  line-height: 1.65;
}

.dn-highlight-list {
  margin: 0;
  padding-left: 18px;
  color: #9ba7b4;
  font-size: 0.9rem;
  line-height: 1.75;
}

.dn-highlight-list li {
  margin-bottom: 12px;
}

.dn-highlight-list strong {
  color: #e6edf3;
}

.dn-pain-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 16px;
}

.dn-pain-card {
  padding: 22px;
}

.dn-pain-mark {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 52px;
  height: 26px;
  padding: 0 10px;
  border-radius: 6px;
  background: rgba(0, 230, 92, 0.1);
  border: 1px solid rgba(0, 230, 92, 0.22);
  color: #7ef0a7;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.72rem;
  font-weight: 800;
  margin-bottom: 14px;
}

.dn-pain-card h3 {
  margin: 0 0 10px;
  font-size: 1.08rem;
}

.dn-pain-card p {
  margin: 0 0 14px;
  color: #94a3b8;
  font-size: 0.88rem;
  line-height: 1.68;
}

.dn-pain-note {
  display: block;
  color: #7d8997;
  font-size: 0.76rem;
  line-height: 1.55;
}

.dn-bento-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 20px;
}

.dn-bento-card {
  padding: 24px;
  transition: transform 0.2s ease, border-color 0.2s ease;
}

.dn-bento-card:hover {
  transform: translateY(-3px);
  border-color: rgba(0, 230, 92, 0.4);
}

.dn-bento-badge {
  display: inline-block;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.75rem;
  padding: 3px 10px;
  border-radius: 6px;
  background: rgba(56, 189, 248, 0.1);
  color: #7dd3fc;
  margin-bottom: 12px;
}

.dn-bento-card h3 {
  margin: 0 0 10px;
  font-size: 1.18rem;
}

.dn-bento-card p {
  font-size: 0.92rem;
  color: #94a3b8;
  line-height: 1.68;
  margin-bottom: 16px;
}

.dn-tag-list {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.dn-tag {
  font-size: 0.76rem;
  padding: 4px 10px;
  border-radius: 6px;
  background: #162030;
  color: #cbd5e1;
  border: 1px solid #233248;
}

.dn-safety-box {
  margin-top: 3rem;
  padding: 3rem 0 1rem;
  border-top: 1px solid rgba(0, 230, 92, 0.16);
  border-bottom: 1px solid rgba(0, 230, 92, 0.16);
}

.dn-safety-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
  gap: 18px;
}

.dn-safety-item {
  padding: 20px;
}

.dn-safety-num {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 1.35rem;
  font-weight: 800;
  color: #7ef0a7;
  margin-bottom: 8px;
}

.dn-safety-item h4 {
  margin: 0 0 8px;
  font-size: 1rem;
}

.dn-safety-item p {
  margin: 0;
  font-size: 0.88rem;
  color: #94a3b8;
  line-height: 1.65;
}

.dn-ack-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 14px;
}

.dn-ack-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 14px;
  background: #0f1622;
}

.dn-ack-card.is-highlight {
  border-color: rgba(250, 204, 21, 0.4);
  background: #151a1d;
}

.dn-ack-card img {
  width: 46px;
  height: 46px;
  border-radius: 50%;
  object-fit: cover;
  border: 2px solid #28374d;
}

.dn-ack-info {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.dn-ack-info strong {
  font-size: 0.95rem;
  color: #f1f5f9;
}

.dn-ack-info span {
  font-size: 0.76rem;
  color: #8b98a9;
  line-height: 1.45;
}

.dn-timeline {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 16px;
}

.dn-time-node {
  border-left: 3px solid #00e65c;
  padding: 16px 20px;
  border-radius: 0 10px 10px 0;
}

.dn-ver {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-weight: 700;
  color: #7ef0a7;
  font-size: 1rem;
}

.dn-time-node p {
  margin: 8px 0 0;
  font-size: 0.88rem;
  color: #94a3b8;
  line-height: 1.65;
}

.dn-modal-mask {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.72);
  backdrop-filter: blur(6px);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 999;
  padding: 1rem;
}

.dn-modal-card {
  background: #101722;
  border: 1px solid #28374d;
  border-radius: 12px;
  padding: 28px;
  max-width: 380px;
  width: 100%;
  text-align: center;
}

.dn-modal-card h3 {
  margin: 0 0 8px;
}

.dn-modal-card p {
  font-size: 0.85rem;
  color: #94a3b8;
  margin-bottom: 16px;
}

.dn-modal-card img {
  width: 240px;
  height: 240px;
  object-fit: contain;
  margin: 0 auto 20px;
  border-radius: 10px;
  background: #fff;
  padding: 8px;
}

@media (max-width: 720px) {
  .dn-landing {
    padding: 1.25rem 1rem 4rem;
  }

  .dn-hero {
    padding-top: 1.5rem;
  }

  .dn-hero-title {
    font-size: 2.25rem;
  }

  .dn-section-header h2 {
    font-size: 1.65rem;
  }

  .dn-ui-quickbar,
  .dn-ui-check-row {
    align-items: flex-start;
  }

  .dn-ui-quickbar,
  .dn-ui-check-row {
    flex-direction: column;
  }

  .dn-row-name {
    width: auto;
  }

  .dn-row-btn {
    width: 100%;
    text-align: center;
  }
}
</style>
