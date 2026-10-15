---
layout: page
title: DeltaNFD - 专为《三角洲行动》打造的硬核系统调优与环境修复引擎
sidebar: false
---

<script setup>
import { ref, onMounted } from 'vue'
import { withBase } from 'vitepress'

const version = ref('v0.915+')
const releaseDate = ref('最新稳定版')
const downloadUrl = ref('https://github.com/xlht4106/deltanfd/releases/latest')
const showQrModal = ref(false)
const activeTab = ref('cpu')

// 自动拉取根目录同步过来的 update.json 或 GitHub Releases API
onMounted(async () => {
  try {
    const res = await fetch(withBase('/update.json'))
    if (res.ok) {
      const data = await res.json()
      const ver = data.version || data.Version || data.latestVersion || data.tag_name
      const url = data.url || data.Url || data.downloadUrl || data.installerUrl
      if (ver) version.value = String(ver).startsWith('v') ? ver : `v${ver}`
      if (url) downloadUrl.value = url
    }
  } catch (e) {
    // 降级尝试 GitHub API
    try {
      const ghRes = await fetch('https://api.github.com/repos/xlht4106/deltanfd/releases/latest')
      if (ghRes.ok) {
        const ghData = await ghRes.json()
        if (ghData.tag_name) version.value = ghData.tag_name
        if (ghData.published_at) releaseDate.value = new Date(ghData.published_at).toLocaleDateString('zh-CN')
        const exeAsset = ghData.assets?.find(a => a.name.endsWith('.exe'))
        if (exeAsset) downloadUrl.value = exeAsset.browser_download_url
      }
    } catch (_) {}
  }
})

const coreModules = [
  {
    id: 'cpu',
    badge: 'Backend.CpuFrame',
    title: '🎯 异构 CPU 与帧率调度引擎',
    desc: '自动探测大小核与 AMD 双 CCD 拓扑结构（CcdTopologyDetector），智能接管 Core Parking 核心停驻循环与离线 CPU Sets 调度，搭载专属 NoPowerSave 电源方案，彻底消除跨 CCD 延迟与团战 1% Low 帧骤降。',
    tags: ['双 CCD 感知', 'Core Parking 动态锁', 'Offline CPU Sets', '专属电源计划']
  },
  {
    id: 'ace',
    badge: 'Backend.Ace',
    title: '🛡️ ACE 反作弊环境专项管家',
    desc: '针对游戏反作弊机制设计的安全体检与环境整备模块。内置虚拟化冲突（VBS/内核隔离）检测、ACE 异常残留分阶段清理守护（AceCleanupGuard）以及注册表 ACL 权限安全保护。',
    tags: ['虚拟化状态诊断', 'ACE 残留安全清理', '注册表权限守护', '零注入纯系统级']
  },
  {
    id: 'gpu',
    badge: 'Backend.Gpu & Shader',
    title: '🚀 GPU 底层驱动与着色器调优',
    desc: '通过 NvApiDrs 直连 NVIDIA 驱动底层配置 Profile，支持一键清理 DirectX / 显卡着色器缓存（ShaderService）、显卡设备热重载与特定场景配置规则守护。',
    tags: ['NvAPI 底层调优', '着色器缓存清理', '设备热重载', '驱动配置备份']
  },
  {
    id: 'procman',
    badge: 'Backend.Procman',
    title: '⚡ 进程压制与智能内存修剪',
    desc: '实时采样后台干扰进程 CPU 占用，支持进程规则化降级、智能工作集内存清理（MemoryPurge/Trimmer）、游戏前瞻启动控制以及 GPU 高性能偏好强制绑定。',
    tags: ['后台干扰压制', 'MemoryPurge 内存释放', 'I/O 与优先级门控', '响应式过滤']
  },
  {
    id: 'runtime',
    badge: 'Backend.Runtime',
    title: '🔧 运行库强力修复与防篡改守护',
    desc: '告别莫名闪退与 DLL 报错。内置 SxS 组件解析器、MSI 元数据扫描、.NET 运行环境自动诊断修复（DotNetRepair）与 RuntimeGuard 运行库防破坏保护机制。',
    tags: ['VC++ / DirectX 强修', 'SxS 依赖解析', '.NET 离线修复', '批量静默重装']
  },
  {
    id: 'tweakdb',
    badge: 'TweakDb & Plugins',
    title: '🧰 百宝箱调优库与声明式插件生态',
    desc: '内置基础设置、系统精简（Debloat）、电源、注册表、安全防护及“UP主推荐（UpRecommended）”一键预设；原生自带「弱网加速优化（WeakNetBoost）」与「硬件体检报告」插件。',
    tags: ['UP主推荐预设', '弱网优化插件', 'Pagefile 虚拟内存管理', '声明式插件扩展']
  }
]

const acknowledgements = [
  { name: '首席测试官', role: 'Lead Tester · 核心质量把关', avatar: '/assets/acknowledgements/lead-tester.jpg', highlight: true },
  { name: '上官', role: 'Cash Support · 特别资金支持', avatar: '/assets/acknowledgements/shangguan-cash-support.jpg', highlight: true },
  { name: 'Token 提供支持', role: 'Token Provider · 算力与开发支持', avatar: '/assets/acknowledgements/token-provider.jpg', highlight: true },
  { name: '匿名岩柱', role: 'Bug Hunter · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-anonymous-yanzhu.png' },
  { name: '姐妹网名', role: 'Bug Hunter · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-jiemei-wangming.jpg' },
  { name: '木朵', role: 'Bug Hunter · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-muduo.jpg' },
  { name: '葡萄酒吐司', role: 'Bug Hunter · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-putaojiu-tusi.jpg' },
  { name: '乌鸡燕', role: 'Bug Hunter · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-wujiyan.jpg' },
  { name: '紫', role: 'Bug Hunter · 捉虫猎人', avatar: '/assets/acknowledgements/bug-hunter-zi.jpg' }
]
</script>

<div class="dn-landing">
  <!-- 1. HERO 首屏区 -->
  <section class="dn-hero">
    <div class="dn-hero-badge">
      <span class="dn-pulse"></span>
      <span>GPL-3.0 开源透明 · 6 大独立后端进程隔离 · 全操作可逆可回滚</span>
    </div>
    <img class="dn-hero-logo" :src="withBase('/assets/logo.png')" alt="DeltaNFD Logo" />
    <h1 class="dn-hero-title">
      <span>DeltaNFD</span> 战术级系统调优引擎
    </h1>
    <p class="dn-hero-subtitle">
      专为《三角洲行动》玩家与硬件发烧友打造。集 <strong>异构 CPU 拓扑调度</strong>、<strong>ACE 反作弊环境整备</strong>、<strong>NvAPI 驱动级调优</strong>、<strong>进程内存压制</strong> 与 <strong>运行库强力修复</strong> 于一体的现代化 Windows 性能工作站。
    </p>
    <div class="dn-cta-group">
      <a class="dn-btn dn-btn-primary" :href="downloadUrl" target="_blank" rel="noopener">
        <span>⬇ 立即下载安装包 ({{ version }})</span>
      </a>
      <a class="dn-btn dn-btn-secondary" :href="withBase('/guide/quick-start')">
        <span>📖 快速上手指南</span>
      </a>
      <button class="dn-btn dn-btn-outline" @click="showQrModal = true">
        <span>💬 加入反馈交流群</span>
      </button>
    </div>
    <div class="dn-hero-meta">
      <span>运行环境：Windows 10 / 11 (x64)</span>
      <span class="dn-meta-divider">|</span>
      <a href="https://github.com/xlht4106/deltanfd/releases" target="_blank">查看历史版本与校验码 ↗</a>
    </div>
    <!-- 模拟软件主控台视觉预览框 -->
    <div class="dn-console-preview">
      <div class="dn-console-bar">
        <div class="dn-dots"><span></span><span></span><span></span></div>
        <div class="dn-console-title">DeltaNFD Workstation — Dashboard & Backend Host Status [ONLINE]</div>
        <div class="dn-console-status">6 Backends Ready</div>
      </div>
      <div class="dn-console-grid">
        <div class="dn-stat-card">
          <div class="dn-stat-label">CPU 拓扑与帧率引擎</div>
          <div class="dn-stat-value dn-green">CCD 优选 & 停驻已接管</div>
          <div class="dn-stat-sub">OfflineHelper 断电恢复守护就绪</div>
        </div>
        <div class="dn-stat-card">
          <div class="dn-stat-label">ACE 反作弊环境整备</div>
          <div class="dn-stat-value dn-green">VBS 虚拟化无冲突</div>
          <div class="dn-stat-sub">AceCleanupGuard 权限保护已启用</div>
        </div>
        <div class="dn-stat-card">
          <div class="dn-stat-label">NVIDIA 驱动与着色器</div>
          <div class="dn-stat-value dn-green">NvApiDrs 已直连</div>
          <div class="dn-stat-sub">DirectX / 着色器缓存健康</div>
        </div>
        <div class="dn-stat-card">
          <div class="dn-stat-label">安全备份与熔断中心</div>
          <div class="dn-stat-value dn-amber">TweakBackup 已同步</div>
          <div class="dn-stat-sub">支持随时一键还原系统默认状态</div>
        </div>
      </div>
    </div>
  </section>
  <!-- 2. 痛点直击对比区 (Before vs After) -->
  <section class="dn-section">
    <div class="dn-section-header">
      <span class="dn-eyebrow">WHY DELTANFD</span>
      <h2>告别玄学批处理，直击三大核心痛点</h2>
      <p>将底层系统工程转化为实打实的帧率稳定性与环境可靠性</p>
    </div>
    <div class="dn-compare-grid">
      <div class="dn-compare-card">
        <div class="dn-compare-bad">❌ 传统痛点：团战掉帧、大小核/双CCD乱跳导致 1% Low 帧断崖下跌</div>
        <div class="dn-compare-good">
          <h4>✅ DeltaNFD 方案：硬件拓扑感知 + 离线级安全调度</h4>
          <p>自动识别 Intel 大小核与 AMD 双 CCD 架构，精细管理 Core Parking 与 CPU Sets，配合 <code>OfflineHelper</code> 独立守护进程，即使游戏或工具异常退出也能在数秒内自动恢复系统默认调度。</p>
        </div>
      </div>
      <div class="dn-compare-card">
        <div class="dn-compare-bad">❌ 传统痛点：反作弊环境冲突、残留服务报错、乱用网传脚本导致系统崩溃</div>
        <div class="dn-compare-good">
          <h4>✅ DeltaNFD 方案：合规边界内的 ACE 诊断与清理守护</h4>
          <p>绝不触碰游戏内存与文件完整性。纯系统级排查虚拟化（VBS）配置冲突，提供分阶段、带安全门控（<code>AceCleanupGuard</code> / <code>AceRegistrySafety</code>）的残留清理与权限修复。</p>
        </div>
      </div>
      <div class="dn-compare-card">
        <div class="dn-compare-bad">❌ 传统痛点：游戏莫名闪退、VC++/DirectX/.NET 损坏且常规安装器无法覆盖重装</div>
        <div class="dn-compare-good">
          <h4>✅ DeltaNFD 方案：SxS 级强力运行库修复与防篡改保护</h4>
          <p>内置 <code>RuntimeStrongRepair</code> 与 <code>RuntimeSxsResolver</code>，支持损坏 MSI/INF 依赖深度清理、批量静默重装以及运行库防护，一站式解决疑难杂症。</p>
        </div>
      </div>
    </div>
  </section>
  <!-- 3. 六大核心功能矩阵 (Bento Grid) -->
  <section class="dn-section">
    <div class="dn-section-header">
      <span class="dn-eyebrow">CORE ARCHITECTURE</span>
      <h2>六大独立后端模块 + 插件化生态</h2>
      <p>采用前后端分离的多进程架构，每个核心领域由专属 Backend 引擎独立承载</p>
    </div>
    <div class="dn-bento-grid">
      <div class="dn-bento-card" v-for="mod in coreModules" :key="mod.id">
        <div class="dn-bento-badge">{{ mod.badge }}</div>
        <h3>{{ mod.title }}</h3>
        <p>{{ mod.desc }}</p>
        <div class="dn-tag-list">
          <span class="dn-tag" v-for="t in mod.tags" :key="t">{{ t }}</span>
        </div>
      </div>
    </div>
  </section>
  <!-- 4. 安全防呆与工程实力背书 -->
  <section class="dn-section dn-safety-box">
    <div class="dn-section-header">
      <span class="dn-eyebrow">SAFETY & RELIABILITY</span>
      <h2>为什么你可以放心把系统交给 DeltaNFD？</h2>
      <p>比优化效果更重要的，是确保每一项修改都可追踪、可回滚、有熔断</p>
    </div>
    <div class="dn-safety-grid">
      <div class="dn-safety-item">
        <div class="dn-safety-num">01</div>
        <h4>全链路自动备份 (TweakBackupStore)</h4>
        <p>在修改任何注册表、服务、电源计划或驱动参数前，系统自动记录原始状态快照，支持单项回退或全局一键恢复。</p>
      </div>
      <div class="dn-safety-item">
        <div class="dn-safety-num">02</div>
        <h4>断电与异常退出自愈 (OfflineRestoreHandover)</h4>
        <p>独创离线交接守护机制。即便遭遇死机、断电或强制杀进程，<code>DeltaNFD.OfflineHelper</code> 也会在重启或超时后自动接管并还原 CPU 调度与电源状态。</p>
      </div>
      <div class="dn-safety-item">
        <div class="dn-safety-num">03</div>
        <h4>远端紧急熔断策略 (EmergencyPolicy)</h4>
        <p>内置带数字签名校验的紧急策略通道。若游戏版本大更或 Windows 补丁引发特定优化项兼容问题，客户端可即时拉取策略自动熔断高风险项。</p>
      </div>
      <div class="dn-safety-item">
        <div class="dn-safety-num">04</div>
        <h4>60+ 项自动化冒烟测试 (BackendSmokeTest)</h4>
        <p>每次版本构建均经过涵盖权限边界、并发备份、离线恢复、插件沙箱与安装器交接的严苛自动化测试验证。</p>
      </div>
    </div>
  </section>
  <!-- 5. 版本演进与开源透明 -->
  <section class="dn-section">
    <div class="dn-section-header">
      <span class="dn-eyebrow">EVOLUTION</span>
      <h2>持续高频迭代 · 紧跟游戏与系统更新</h2>
    </div>
    <div class="dn-timeline">
      <div class="dn-time-node">
        <span class="dn-ver">v0.915 / v0.914</span>
        <p>强化进程规则引擎、离线调度交接稳定性与运行库深度修复流，完善插件生态与 UI 体验。</p>
      </div>
      <div class="dn-time-node">
        <span class="dn-ver">v0.91 / v0.90.1</span>
        <p>引入多后端进程解耦架构、完善 NvAPI 驱动配置与着色器缓存清理守护、升级安装包自包含部署能力。</p>
      </div>
      <div class="dn-time-node">
        <span class="dn-ver">v0.89.0 / v0.83.0</span>
        <p>奠定核心系统调优数据库（TweakDb）、ACE 环境检测与异构 CPU 帧率优化基础架构。</p>
      </div>
    </div>
  </section>
  <!-- 6. 社区致谢墙 (Acknowledgements) -->
  <section class="dn-section">
    <div class="dn-section-header">
      <span class="dn-eyebrow">HALL OF FAME</span>
      <h2>社区荣誉与致谢墙</h2>
      <p>感谢每一位参与极限测试、提交关键 Bug 与提供算力/资金支持的伙伴</p>
    </div>
    <div class="dn-ack-grid">
      <div 
        class="dn-ack-card" 
        :class="{ 'is-highlight': person.highlight }"
        v-for="person in acknowledgements" 
        :key="person.name"
      >
        <img :src="withBase(person.avatar)" :alt="person.name" loading="lazy" />
        <div class="dn-ack-info">
          <strong>{{ person.name }}</strong>
          <span>{{ person.role }}</span>
        </div>
      </div>
    </div>
  </section>
  <!-- 反馈交流群二维码弹窗 -->
  <div class="dn-modal-mask" v-if="showQrModal" @click.self="showQrModal = false">
    <div class="dn-modal-card">
      <h3>扫码加入 DeltaNFD 反馈交流群</h3>
      <p>获取最新测试版通知、反馈疑难杂症或交流硬件调优心得</p>
      <img :src="withBase('/assets/FeedbackGroupQr.png')" alt="反馈交流群二维码" />
      <button class="dn-btn dn-btn-primary" @click="showQrModal = false">关闭窗口</button>
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

/* Hero 首屏 */
.dn-hero {
  text-align: center;
  padding: 3rem 0 4rem;
}
.dn-hero-badge {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 6px 16px;
  border-radius: 999px;
  background: rgba(0, 255, 102, 0.08);
  border: 1px solid rgba(0, 255, 102, 0.3);
  color: #00ff66;
  font-size: 0.85rem;
  font-weight: 600;
  margin-bottom: 1.5rem;
}
.dn-pulse {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #00ff66;
  box-shadow: 0 0 10px #00ff66;
}
.dn-hero-logo {
  width: 88px;
  height: 88px;
  margin: 0 auto 1.2rem;
  border-radius: 20px;
  box-shadow: 0 0 35px rgba(0, 255, 102, 0.25);
}
.dn-hero-title {
  font-size: clamp(2.2rem, 5vw, 3.6rem);
  font-weight: 800;
  line-height: 1.15;
  letter-spacing: -0.02em;
  margin-bottom: 1.2rem;
}
.dn-hero-title span {
  background: linear-gradient(135deg, #00ff66 0%, #00b8ff 100%);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
}
.dn-hero-subtitle {
  max-width: 780px;
  margin: 0 auto 2rem;
  font-size: 1.1rem;
  line-height: 1.7;
  color: #9ba7b4;
}
.dn-hero-subtitle strong {
  color: #e6edf3;
}

/* 按钮组 */
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
  padding: 0.8rem 1.6rem;
  border-radius: 10px;
  font-weight: 700;
  font-size: 0.98rem;
  text-decoration: none !important;
  transition: all 0.2s ease;
  cursor: pointer;
}
.dn-btn-primary {
  background: linear-gradient(135deg, #00e65c, #00b347);
  color: #041208 !important;
  box-shadow: 0 8px 25px rgba(0, 230, 92, 0.3);
  border: none;
}
.dn-btn-primary:hover {
  transform: translateY(-2px);
  box-shadow: 0 12px 30px rgba(0, 255, 102, 0.45);
}
.dn-btn-secondary {
  background: #161f2c;
  color: #e6edf3 !important;
  border: 1px solid #2a3648;
}
.dn-btn-secondary:hover {
  border-color: #00ff66;
  background: #1c2838;
}
.dn-btn-outline {
  background: transparent;
  color: #00ff66;
  border: 1px solid rgba(0, 255, 102, 0.4);
}
.dn-btn-outline:hover {
  background: rgba(0, 255, 102, 0.1);
}

.dn-hero-meta {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  align-items: center;
  gap: 10px;
  font-size: 0.85rem;
  color: #7d8997;
  margin-bottom: 3rem;
}
.dn-meta-divider {
  opacity: 0.3;
}

/* 模拟控制台预览 */
.dn-console-preview {
  background: #0d131c;
  border: 1px solid #1f2b3d;
  border-radius: 16px;
  overflow: hidden;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.6), 0 0 40px rgba(0, 255, 102, 0.06);
  text-align: left;
}
.dn-console-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 18px;
  background: #121a26;
  border-bottom: 1px solid #1f2b3d;
  font-size: 0.8rem;
  color: #8b98a9;
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
.dn-console-status {
  color: #00ff66;
  font-family: monospace;
  font-weight: 700;
}
.dn-console-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 16px;
  padding: 20px;
}
.dn-stat-card {
  background: #121a26;
  border: 1px solid #1d293a;
  padding: 16px;
  border-radius: 12px;
}
.dn-stat-label {
  font-size: 0.8rem;
  color: #8b98a9;
  margin-bottom: 6px;
}
.dn-stat-value {
  font-size: 1.05rem;
  font-weight: 700;
  margin-bottom: 4px;
}
.dn-green { color: #00ff66; }
.dn-amber { color: #ffbe0b; }
.dn-stat-sub {
  font-size: 0.76rem;
  color: #697789;
}

/* 通用板块头部 */
.dn-section {
  padding: 4rem 0 1rem;
}
.dn-section-header {
  text-align: center;
  margin-bottom: 2.5rem;
}
.dn-eyebrow {
  display: inline-block;
  font-family: monospace;
  font-size: 0.8rem;
  letter-spacing: 0.15em;
  color: #00ff66;
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

/* 痛点对比 */
.dn-compare-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
  gap: 20px;
}
.dn-compare-card {
  background: #0e141f;
  border: 1px solid #1e293b;
  border-radius: 14px;
  overflow: hidden;
}
.dn-compare-bad {
  padding: 14px 18px;
  background: rgba(239, 68, 68, 0.08);
  border-bottom: 1px solid rgba(239, 68, 68, 0.2);
  color: #fca5a5;
  font-size: 0.88rem;
  font-weight: 600;
}
.dn-compare-good {
  padding: 18px;
}
.dn-compare-good h4 {
  margin: 0 0 8px;
  color: #00ff66;
  font-size: 1.05rem;
}
.dn-compare-good p {
  margin: 0;
  font-size: 0.9rem;
  color: #9ba7b4;
  line-height: 1.6;
}

/* Bento 六大模块 */
.dn-bento-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(340px, 1fr));
  gap: 20px;
}
.dn-bento-card {
  background: linear-gradient(180deg, #101722 0%, #0b1018 100%);
  border: 1px solid #1e2a3a;
  border-radius: 16px;
  padding: 24px;
  transition: transform 0.2s ease, border-color 0.2s ease;
}
.dn-bento-card:hover {
  transform: translateY(-4px);
  border-color: rgba(0, 255, 102, 0.45);
}
.dn-bento-badge {
  display: inline-block;
  font-family: monospace;
  font-size: 0.75rem;
  padding: 3px 10px;
  border-radius: 6px;
  background: rgba(0, 184, 255, 0.1);
  color: #38bdf8;
  margin-bottom: 12px;
}
.dn-bento-card h3 {
  margin: 0 0 10px;
  font-size: 1.2rem;
}
.dn-bento-card p {
  font-size: 0.92rem;
  color: #94a3b8;
  line-height: 1.65;
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

/* 安全背书区 */
.dn-safety-box {
  margin-top: 3rem;
  padding: 3rem 2rem;
  background: linear-gradient(135deg, rgba(0, 255, 102, 0.04) 0%, rgba(0, 184, 255, 0.04) 100%);
  border: 1px solid rgba(0, 255, 102, 0.2);
  border-radius: 20px;
}
.dn-safety-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 20px;
}
.dn-safety-item {
  background: rgba(11, 16, 24, 0.8);
  border: 1px solid #1e293b;
  padding: 20px;
  border-radius: 14px;
}
.dn-safety-num {
  font-family: monospace;
  font-size: 1.4rem;
  font-weight: 800;
  color: #00ff66;
  margin-bottom: 8px;
}
.dn-safety-item h4 {
  margin: 0 0 8px;
  font-size: 1.02rem;
}
.dn-safety-item p {
  margin: 0;
  font-size: 0.88rem;
  color: #94a3b8;
  line-height: 1.6;
}

/* 时间轴 */
.dn-timeline {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 16px;
}
.dn-time-node {
  background: #0e141f;
  border-left: 3px solid #00ff66;
  padding: 16px 20px;
  border-radius: 0 12px 12px 0;
}
.dn-ver {
  font-family: monospace;
  font-weight: 700;
  color: #00ff66;
  font-size: 1rem;
}
.dn-time-node p {
  margin: 8px 0 0;
  font-size: 0.88rem;
  color: #94a3b8;
}

/* 社区致谢墙 */
.dn-ack-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 14px;
}
.dn-ack-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 14px;
  background: #0f1622;
  border: 1px solid #1e2a3a;
  border-radius: 12px;
}
.dn-ack-card.is-highlight {
  border-color: rgba(255, 190, 11, 0.45);
  background: linear-gradient(135deg, rgba(255, 190, 11, 0.08), #0f1622);
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
}
.dn-ack-info strong {
  font-size: 0.95rem;
  color: #f1f5f9;
}
.dn-ack-info span {
  font-size: 0.76rem;
  color: #8b98a9;
}

/* 二维码弹窗 */
.dn-modal-mask {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.75);
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
  border-radius: 16px;
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
  border-radius: 12px;
  background: #fff;
  padding: 8px;
}
</style>
