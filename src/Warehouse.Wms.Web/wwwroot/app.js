(() => {
  // PLC control remains in the device gateway; the browser submits business actions only.
  const titles = { dashboard: '工作台', inbound: '入库作业', outbound: '出库作业', inventory: '库存与库位', statistics: '统计分析', tasks: '设备任务', stocktaking: '盘点', exceptions: '异常中心' };
  const navItems = [...document.querySelectorAll('[data-view]')];
  const panels = [...document.querySelectorAll('[data-view-panel]')];
  const toast = document.getElementById('toast');
  const pda = document.getElementById('pda-mode');
  let toastTimer;

  function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, character => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[character]));
  }

  const rack = document.getElementById('rack-grid');
  for (let i = 0; i < 48; i += 1) {
    const slot = document.createElement('button');
    slot.className = `slot ${i % 9 === 0 ? 'locked' : i % 4 === 0 ? 'free' : 'used'}`;
    slot.title = `A${Math.floor(i / 8) + 1}-${String(i % 8 + 1).padStart(2, '0')}`;
    slot.addEventListener('click', () => showToast(`${slot.title} 库位详情已打开`));
    rack.appendChild(slot);
  }

  async function loadPoints() {
    const zone = document.getElementById('point-zone')?.value;
    const status = document.getElementById('point-status')?.value;
    const query = new URLSearchParams();
    if (zone) query.set('zoneCode', zone);
    if (status) query.set('status', status);
    try {
      const response = await fetch(`/api/warehouse/points?${query}`);
      if (!response.ok) throw new Error('点位接口不可用');
      const points = await response.json();
      if (rack) rack.replaceChildren();
      points.forEach(point => {
        const slot = document.createElement('button');
        const stateClass = point.status === 'Free' ? 'free' : point.status === 'Occupied' ? 'used' : 'locked';
        slot.className = `slot ${stateClass}`;
        slot.textContent = point.locationCode;
        slot.title = `${point.locationCode} / ${point.status} / ${point.freshness}`;
        slot.addEventListener('click', () => showPoint(point));
        rack?.appendChild(slot);
      });
      const freshness = document.getElementById('point-freshness');
      if (freshness) freshness.textContent = `${points.length} 个点位 · ${points.some(p => p.freshness !== 'Fresh') ? '含过期/未知' : '数据新鲜'}`;
    } catch (error) {
      showToast(error.message);
    }
  }

  function showPoint(point) {
    const detail = document.getElementById('point-detail');
    if (!detail) return;
    detail.innerHTML = `<div class="point-detail-grid"><span>库位</span><strong>${escapeHtml(point.locationCode)}</strong><span>状态</span><strong>${escapeHtml(point.status)} / ${escapeHtml(point.freshness)}</strong><span>托盘</span><strong>${escapeHtml(point.palletCode || '空库位')}</strong><span>物料</span><strong>${escapeHtml(point.materialCode || '--')} ${escapeHtml(point.materialName || '')}</strong><span>数量 / 重量</span><strong>${escapeHtml(point.quantity)} / ${escapeHtml(point.weightKg)} kg</strong><span>任务 / 装载点</span><strong>${escapeHtml(point.taskState || '--')} / ${escapeHtml(point.loadPoint || '--')}</strong><span>最近观察</span><strong>${escapeHtml(new Date(point.observedAt).toLocaleString())}</strong></div>`;
  }

  async function loadStatistics() {
    const period = document.getElementById('statistics-period')?.value || 'Day';
    try {
      const response = await fetch(`/api/reports/summary?period=${encodeURIComponent(period)}`);
      if (!response.ok) throw new Error('统计接口不可用');
      const summary = await response.json();
      const kpi = summary.kpi || {};
      const values = [`${kpi.inventoryQuantity ?? 0}`, `${kpi.locationUtilizationPercent ?? 0}%`, `${kpi.taskSuccessRatePercent ?? 0}%`, `${kpi.exceptionCount ?? 0}`];
      document.querySelectorAll('#statistics-kpis article strong').forEach((item, index) => { item.textContent = values[index]; });
      const freshness = document.getElementById('statistics-freshness');
      if (freshness) freshness.textContent = `${summary.freshness} · ${new Date(summary.generatedAt).toLocaleString()}`;
      const trends = document.getElementById('statistics-trends');
      if (trends) trends.innerHTML = (summary.trends || []).map(item => `<div class="trend-row"><span>${escapeHtml(new Date(item.periodStart).toLocaleDateString())}</span><b>入 ${escapeHtml(item.inboundQuantity)} · 出 ${escapeHtml(item.outboundQuantity)} · 移 ${escapeHtml(item.transferQuantity)} · 异常 ${escapeHtml(item.exceptionCount)}</b></div>`).join('') || '<span class="muted">暂无统计批次</span>';
      const states = document.getElementById('statistics-task-states');
      if (states) states.innerHTML = (summary.taskStates || []).map(item => `<div class="trend-row"><span>${escapeHtml(item.state)}</span><b>${escapeHtml(item.count)}</b></div>`).join('') || '<span class="muted">暂无数据</span>';
    } catch (error) {
      showToast(error.message);
    }
  }

  function showToast(message) {
    toast.textContent = message;
    toast.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toast.classList.remove('show'), 2600);
  }

  function navigate(view) {
    if (!titles[view]) return;
    navItems.forEach(item => item.classList.toggle('active', item.dataset.view === view));
    panels.forEach(panel => panel.classList.toggle('active', panel.id === `view-${view}`));
    document.getElementById('page-title').textContent = titles[view];
    localStorage.setItem('wms.lastView', view);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  navItems.forEach(item => item.addEventListener('click', () => navigate(item.dataset.view)));
  document.querySelectorAll('[data-view-link]').forEach(item => item.addEventListener('click', () => navigate(item.dataset.viewLink)));
  document.querySelectorAll('[data-action="refresh"]').forEach(item => item.addEventListener('click', () => showToast('数据刷新请求已提交')));
  document.querySelectorAll('[data-action="load-points"]').forEach(item => item.addEventListener('click', loadPoints));
  document.querySelectorAll('[data-action="load-statistics"]').forEach(item => item.addEventListener('click', loadStatistics));
  document.querySelectorAll('[data-action="reconcile"]').forEach(item => item.addEventListener('click', () => showToast('对账任务已加入队列，未知结果保持锁定')));
  document.querySelectorAll('[data-action="new-inbound"], [data-action="new-outbound"], [data-action="new-task"], [data-action="new-stocktaking"]').forEach(item => item.addEventListener('click', () => showToast('当前入口已就绪，提交前需要登录授权')));
  document.querySelectorAll('[data-action="export"]').forEach(item => item.addEventListener('click', () => showToast('导出请求已提交')));
  document.querySelectorAll('[data-action="pda"]').forEach(item => item.addEventListener('click', () => pda.showModal()));
  document.querySelectorAll('[data-action="close-pda"]').forEach(item => item.addEventListener('click', () => pda.close()));
  document.querySelector('[data-action="scan-confirm"]').addEventListener('click', () => {
    const value = document.querySelector('.scan-input').value.trim();
    if (!value) { showToast('请输入或扫描编码'); return; }
    pda.close();
    showToast(`编码 ${value} 已提交到业务服务`);
  });
  document.querySelectorAll('[data-filter]').forEach(input => input.addEventListener('input', event => {
    localStorage.setItem(`wms.filter.${event.target.dataset.filter}`, event.target.value);
  }));

  const lastView = localStorage.getItem('wms.lastView');
  if (lastView && titles[lastView]) navigate(lastView);
  loadPoints();
  loadStatistics();
})();
