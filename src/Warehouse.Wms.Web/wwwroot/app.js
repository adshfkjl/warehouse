(() => {
  // PLC control remains in the device gateway; the browser submits business actions only.
  const titles = { dashboard: '工作台', inbound: '入库作业', outbound: '出库作业', inventory: '库存与库位', tasks: '设备任务', stocktaking: '盘点', exceptions: '异常中心' };
  const navItems = [...document.querySelectorAll('[data-view]')];
  const panels = [...document.querySelectorAll('[data-view-panel]')];
  const toast = document.getElementById('toast');
  const pda = document.getElementById('pda-mode');
  let toastTimer;

  const rack = document.getElementById('rack-grid');
  for (let i = 0; i < 48; i += 1) {
    const slot = document.createElement('button');
    slot.className = `slot ${i % 9 === 0 ? 'locked' : i % 4 === 0 ? 'free' : 'used'}`;
    slot.title = `A${Math.floor(i / 8) + 1}-${String(i % 8 + 1).padStart(2, '0')}`;
    slot.addEventListener('click', () => showToast(`${slot.title} 库位详情已打开`));
    rack.appendChild(slot);
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
})();
