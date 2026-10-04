'use strict';
const $ = id => document.getElementById(id);
let session = null, view = 'overview', page = 0, filter = '', version = 0, controller = new AbortController();
let imageUrls = [], confirmResolve = null;
const headings = {
  overview: ['THE BIG PICTURE', 'Community overview', 'A good place to see how things are going.'],
  reports: ['A LITTLE ATTENTION', 'Report queue', 'Review reports with context. Take action with care.'],
  plates: ['MADE BY THE COMMUNITY', 'Shared Plates', 'The latest published designs, all in one place.'],
  audit: ['A CLEAR RECORD', 'Staff activity', 'Moderation decisions from the last 30 days.'],
  staff: ['PEOPLE YOU TRUST', 'Your team', 'Give trusted people a place at the desk.']
};
function el(tag, className = '', text = '') { const n = document.createElement(tag); n.className = className; n.textContent = text; return n; }
// Shows invisible and direction-changing characters as their code points, so text cannot hide or reorder itself for a reviewer.
const hidden = /[\u00AD\u061C\u180E\u200B-\u200F\u202A-\u202E\u2060-\u2064\u2066-\u2069\uFEFF]/g;
function visible(text) { return String(text).replace(hidden, c => `[U+${c.codePointAt(0).toString(16).toUpperCase().padStart(4, '0')}]`); }
function button(text, action, style = 'secondary') { const n = el('button', 'button ' + style, text); n.type = 'button'; n.addEventListener('click', () => run(action)); return n; }
function announce(message) { $('notice').textContent = message; $('notice').hidden = !message; }
async function run(action) { try { await action(); } catch (error) { if (error.name !== 'AbortError' && session) announce(error.message || 'Something went wrong. Try refreshing.'); } }
function releaseImages() { imageUrls.forEach(URL.revokeObjectURL); imageUrls = []; }
function clearSession(message) {
  version++; controller.abort(); controller = new AbortController(); releaseImages(); session = null;
  $('content').replaceChildren(); $('workspace').hidden = true; $('login').hidden = false;
  $('staff-id').textContent = ''; $('report-badge').textContent = ''; $('notice').textContent = '';
  $('login-message').textContent = message;
  if ($('confirm').open) { $('confirm').close(); if (confirmResolve) confirmResolve(null); confirmResolve = null; }
}
async function api(action, data = {}, mutation = false, raw = false) {
  const response = await fetch('/admin/api/' + (mutation ? 'action' : 'query'), {
    method: 'POST', credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
    headers: {'Content-Type': 'application/json', 'X-AF-CSRF': session.csrf}, body: JSON.stringify({action, ...data})
  });
  if (response.status === 401) { clearSession('Your session ended. Sign in again to continue.'); throw new Error('Session ended.'); }
  if (!response.ok) throw new Error(response.status === 409 ? 'This item changed since you reviewed it. Refresh and review it again.' :
    response.status === 403 ? 'This action was refused. Your access may have changed; sign in again.' :
    response.status === 429 ? 'Too many requests. Please wait a minute before trying again.' :
    response.status === 404 ? 'This content is no longer available. Refresh the list.' : 'The request could not be completed. Try refreshing.');
  if (raw) return response.blob();
  return response.status === 204 ? null : response.json();
}
function panel(title, subtitle = '', action = null) {
  const card = el('section', 'panel'), head = el('div', 'panel-head'), text = el('div');
  text.append(el('h2', '', title)); if (subtitle) text.append(el('p', 'muted', subtitle)); head.append(text); if (action) head.append(action);
  card.append(head); return card;
}
function empty(title, message, symbol = '✦') {
  const n = el('div', 'empty'); n.append(el('div', 'empty-symbol', symbol), el('h3', '', title), el('p', '', message)); return n;
}
function status(text, warn = false) { return el('span', 'status' + (warn ? ' warn' : ''), text); }
function row(title, subtitle, actions = []) {
  const n = el('li', 'data-row'), info = el('div', 'row-main'), aside = el('div', 'row-actions');
  info.append(el('strong', '', title), el('small', '', subtitle)); aside.append(...actions); n.append(info, aside); return n;
}
function date(seconds) { return new Date(seconds * 1000).toLocaleString(undefined, {dateStyle: 'medium', timeStyle: 'short'}); }
function reportAge(day) { return new Date(day * 86400000).toLocaleDateString(undefined, {month: 'short', day: 'numeric'}); }
function requestConfirmation(title, description) {
  if (confirmResolve) return Promise.resolve(null);
  $('confirm-title').textContent = title; $('confirm-description').textContent = description; $('reason').value = '';
  $('confirm').showModal(); $('reason').focus(); return new Promise(resolve => { confirmResolve = resolve; });
}
function resolveConfirmation(value) { $('confirm').close(); const resolve = confirmResolve; confirmResolve = null; if (resolve) resolve(value); }
$('confirm-form').addEventListener('submit', event => { event.preventDefault(); const reason = $('reason').value.trim(); if (reason.length >= 3) resolveConfirmation(reason); });
$('cancel-action').addEventListener('click', () => resolveConfirmation(null));
$('confirm').addEventListener('cancel', event => { event.preventDefault(); resolveConfirmation(null); });
async function moderate(action, data, title, description) {
  const reason = await requestConfirmation(title, description); if (!reason || !session) return false;
  $('refresh').disabled = true;
  try { await api(action, {...data, reason}, true); return true; }
  finally { $('refresh').disabled = false; }
}
function reportRows(reports) {
  const list = el('ul', 'data-list');
  for (const r of reports) {
    const actions = [];
    if (r.profile) actions.push(button('Review ↗', () => showDetail(r.profile), 'quiet'));
    actions.push(button('Dismiss', async () => {
      if (await moderate('dismiss', {id:r.id, token:r.token}, 'Dismiss this report?', 'This closes the report without changing the player’s Plate.')) { await navigate(view, page, filter); announce('Report dismissed.'); }
    }, 'quiet'));
    list.append(row(r.name || 'Character no longer bound', `${r.world || 'Content removed'} · ${visible(r.reason)} · ${reportAge(r.day)}`, actions));
  }
  return list;
}
async function renderOverview(host, ticket) {
  const [summary, reports] = await Promise.all([api('overview'), api('reports')]); if (ticket !== version) return;
  const counts = summary.counts;
  $('report-badge').textContent = String(counts.reports); $('report-badge').hidden = !counts.reports;
  const metrics = el('section', 'metrics'); metrics.setAttribute('aria-label', 'Community totals');
  for (const [label, value, note, symbol] of [['Published Plates', counts.published, 'Latest stored designs', '◈'], ['Open reports', counts.reports, 'Waiting for a little attention', '⚑'], ['Hidden Plates', counts.hidden, 'Currently held by staff', '▧']]) {
    const card = el('div', 'metric'); card.append(el('p', 'metric-label', label), el('span', 'metric-symbol', symbol), el('strong', '', Number(value).toLocaleString()), el('small', '', note)); metrics.append(card);
  }
  const grid = el('div', 'overview-grid'), queue = panel('Needs your attention', 'Oldest reports first', button('View queue ↗', () => navigate('reports'), 'quiet'));
  queue.append(reports.length ? reportRows(reports.slice(0, 5)) : empty('All clear for now', 'New reports will appear here. Until then, there’s nothing waiting for review.'));
  const side = el('div'), health = panel('Service health', 'A snapshot of the sharing service'), healthBody = el('div', 'panel-body'), list = el('ul', 'status-list');
  for (const [key, label, description] of [['worker','Image worker','Processing connection'], ['images','Image processing','Canary check'], ['backup','Backups','Latest backup signal']]) {
    const item = el('li'), name = el('div', 'status-name', label); name.append(el('small', '', description)); item.append(name, status(summary.health[key] ? '● Healthy' : '● Attention', !summary.health[key])); list.append(item);
  }
  healthBody.append(list); health.append(healthBody); const hint = el('div', 'hint-card');
  hint.append(el('h3', '', 'Small community. Thoughtful care.'), el('p', '', 'These totals reflect stored content. A published Plate is not an online player. No presence tracking is added by this desk.'));
  side.append(health, hint); grid.append(queue, side); host.append(metrics, grid);
}
function pager(host, hasNext) {
  const n = el('div', 'pager'), prev = button('Previous', () => navigate(view, page - 1, filter)), next = button('Next', () => navigate(view, page + 1, filter));
  prev.disabled = page === 0; next.disabled = !hasNext; n.append(prev, el('span', 'muted', 'Page ' + (page + 1)), next); host.append(n);
}
async function renderList(host, ticket) {
  const rows = await api(view, {page, filter}); if (ticket !== version) return;
  if (view === 'plates') {
    const filters = el('div', 'filters');
    for (const [label, value] of [['Published', ''], ['Hidden by staff', 'hidden']]) { const b = button(label, () => navigate('plates', 0, value)); b.setAttribute('aria-pressed', String(filter === value)); filters.append(b); }
    host.append(filters);
  }
  const card = panel(view === 'reports' ? 'Reports awaiting review' : view === 'audit' ? 'Moderation history' : view === 'staff' ? 'Staff access' : filter ? 'Hidden Plates' : 'Published Plates');
  if (!rows.length) card.append(empty(view === 'reports' ? 'Nothing in the queue' : view === 'audit' ? 'A fresh start' : view === 'staff' ? 'Just you, for now' : 'No Plates here yet',
    view === 'staff' ? 'Add a trusted moderator when you’re ready. Your configured owner account always retains access.' : view === 'audit' ? 'Staff actions will appear here with a reason and time.' : 'This list will fill as the community grows.'));
  else if (view === 'reports') card.append(reportRows(rows.slice(0, 50)));
  else {
    const list = el('ul', 'data-list');
    for (const r of rows.slice(0, view === 'staff' ? 100 : 50)) {
      if (view === 'plates') list.append(row(r.name, r.world + (r.published ? ' · Published' : ' · No current content'), [status(r.held ? 'Hidden by staff' : 'Published', !!r.held), button('Review ↗', () => showDetail(r.profile), 'quiet')]));
      else if (view === 'audit') list.append(row(`${r.action} · Staff ${r.actor}`, `${date(r.at)}${r.staffId ? ` · Target staff ${r.staffId}` : ''} · ${visible(r.reason)}`, r.profile ? [button('View Plate', () => showDetail(r.profile), 'quiet')] : []));
      else list.append(row('GitHub ID ' + r.id, r.active ? 'Moderator access enabled' : 'Access revoked', [button(r.active ? 'Revoke' : 'Grant again', async () => {
        if (await moderate(r.active ? 'revoke' : 'grant', {id:r.id}, r.active ? 'Revoke moderator access?' : 'Grant moderator access?', r.active ? 'Existing sessions will stop working on their next request.' : 'This account will be able to review reports and hide or restore shared Plates.')) { await navigate('staff'); announce('Staff access updated.'); }
      }, r.active ? 'danger' : 'secondary')]));
    }
    card.append(list);
  }
  if (view === 'staff') {
    const form = el('form', 'form-row'), field = el('div'), label = el('label', '', 'Moderator’s numeric GitHub account ID'), input = el('input');
    label.htmlFor = 'new-staff'; input.id = 'new-staff'; input.inputMode = 'numeric'; input.pattern = '[1-9][0-9]{0,18}'; input.maxLength = 19; input.required = true; input.placeholder = 'Numeric ID, not username';
    field.append(label, input); const add = el('button', 'button primary', 'Add moderator'); add.type = 'submit'; form.append(field, add);
    form.addEventListener('submit', event => { event.preventDefault(); run(async () => {
      if (await moderate('grant', {id:input.value}, 'Add this moderator?', 'Confirm the numeric ID belongs to the intended GitHub account. Moderators can access published content and reports, but cannot manage staff.')) { await navigate('staff'); announce('Moderator access granted.'); }
    }); }); card.append(form);
  }
  host.append(card); if (view !== 'staff') pager(host, rows.length > 50);
}
async function navigate(nextView, nextPage = 0, nextFilter = '') {
  if (!session) return;
  if (nextView === 'staff' && session.role !== 'owner') nextView = 'overview';
  view = nextView; page = nextPage; filter = nextFilter; const ticket = ++version; releaseImages(); announce('');
  const heading = headings[view]; $('breadcrumb').textContent = view === 'staff' ? 'Team' : view[0].toUpperCase() + view.slice(1);
  $('section-kicker').textContent = heading[0]; $('page-title').textContent = heading[1]; $('page-description').textContent = heading[2];
  document.querySelectorAll('[data-view]').forEach(n => { n.classList.toggle('active', n.dataset.view === view); if (n.dataset.view === view) n.setAttribute('aria-current','page'); else n.removeAttribute('aria-current'); });
  $('content').replaceChildren(empty('Getting your workspace ready', 'Fetching the latest snapshot…', '◈'));
  const host = el('div'); $('refresh').disabled = true;
  try { if (view === 'overview') await renderOverview(host, ticket); else await renderList(host, ticket);
    if (ticket === version) { $('content').replaceChildren(host); $('updated').textContent = 'Updated ' + new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'}); }
  } catch(error) { if (ticket === version) $('content').replaceChildren(empty('Couldn’t load this view', 'Use Refresh to try again.')); throw error; }
  finally { if (ticket === version) $('refresh').disabled = false; }
}
async function showDetail(profileId) {
  const ticket = ++version; releaseImages(); announce('');
  const detail = await api('detail', {profile:profileId}); if (ticket !== version) return;
  $('page-title').textContent = detail.name; $('page-description').textContent = detail.world + ' · Current shared content'; $('section-kicker').textContent = 'PLATE REVIEW';
  const host = el('div'), top = el('div','detail-top'); top.append(button('← Back to list', () => navigate(view, page, filter)));
  if (detail.plate || detail.held) top.append(button(detail.held ? 'Restore shared Plate' : 'Hide shared Plate', async () => {
    const restore = !!detail.held;
    if (await moderate(restore ? 'restore' : 'hide', {profile:profileId, marker:detail.marker, held:!!detail.held}, restore ? 'Restore this shared Plate?' : 'Hide this shared Plate?',
      restore ? 'Remove the moderation hold. The current published revision may be visible again, subject to the normal sharing rules.' : 'Stop serving this Plate and its images. The player keeps ownership and their local copy. Republishing will not lift the hold. Cached copies cannot be recalled.')) { await showDetail(profileId); announce(restore ? 'Moderation hold removed.' : 'Plate hidden from sharing.'); }
  }, detail.held ? 'primary' : 'danger'));
  host.append(top);
  if (!detail.plate) { host.append(empty('No current published content', 'The player may have paused sharing. A moderation hold remains until restored or the binding is removed.')); $('content').replaceChildren(host); return; }
  const grid = el('div','detail-grid'), preview = panel(visible(detail.plate.name), 'Browser preview'), surface = el('div','preview-surface'), canvas = el('canvas');
  canvas.setAttribute('aria-label','Approximate Plate layout. Full text and original uploaded images are available below.'); canvas.setAttribute('role','img'); surface.append(canvas); preview.append(surface, el('p','preview-note','Approximate layout only. Bundled art, game fonts, textures, text fitting and some effects are not reproduced here. Review all text and uploaded images below; use the game for an exact appearance check.'));
  const textPanel = panel('All text', 'Complete text, including clipped or obscured content'), textBody = el('div','panel-body');
  const texts = detail.plate.items.filter(i=>i.kind==='Text'); for (const item of texts) textBody.append(el('div','full-text',visible(item.data.text)));
  if (texts.some(i => visible(i.data.text) !== i.data.text)) textBody.append(el('p','small muted','[U+…] marks an invisible or direction-changing character in the player’s text.'));
  if (!texts.length) textBody.append(el('p','muted','No text elements.')); textPanel.append(textBody); grid.append(preview,textPanel); host.append(grid);
  const pictures = panel('Uploaded images', 'The current revision’s images, before layout and cropping'), imageGrid = el('div','panel-body image-grid'); pictures.append(imageGrid); if (detail.plate.images.length) host.append(pictures);
  $('content').replaceChildren(host);
  const loaded = new Map();
  for (const image of detail.plate.images) {
    const figure = el('figure'), img = el('img'); img.alt = 'Uploaded image ' + (image.index + 1); figure.append(img,el('figcaption','',`${image.width} × ${image.height}`)); imageGrid.append(figure);
    try {
      const blob = await api('image',{profile:profileId,marker:detail.marker,image:image.index},false,true); if (ticket!==version) return;
      const url = URL.createObjectURL(blob); imageUrls.push(url); img.src=url; await img.decode(); if(ticket!==version)return; loaded.set(image.index,img);
    } catch(error) { if(error.name==='AbortError'||ticket!==version)return; img.remove(); figure.prepend(el('p','muted','Image unavailable. Refresh to review the current revision.')); }
  }
  if (ticket===version) drawPreview(canvas,detail.plate,loaded);
}
function drawPreview(canvas, plate, images) {
  const w=plate.canvasWidth/100,h=plate.canvasHeight/100, scale=Math.min(1,1200/Math.max(w,h)); canvas.width=Math.max(1,Math.round(w*scale));canvas.height=Math.max(1,Math.round(h*scale));
  const ctx=canvas.getContext('2d');ctx.scale(scale,scale);
  const color=c=>`rgba(${c.r},${c.g},${c.b},${c.a/255})`, bg=plate.background;
  ctx.fillStyle=color(bg.primary);ctx.fillRect(0,0,w,h);if(images.has(plate.backgroundImage))ctx.drawImage(images.get(plate.backgroundImage),0,0,w,h);
  for(const item of plate.items){const d=item.data;ctx.save();
    if(item.kind==='Quad'||item.kind==='Triangle'){ctx.beginPath();ctx.moveTo(d.a.x/100,d.a.y/100);for(const p of [d.b,d.c,...(d.d?[d.d]:[])])ctx.lineTo(p.x/100,p.y/100);ctx.closePath();ctx.fillStyle=color(d.color);ctx.fill();}
    else if(item.kind==='Text'){ctx.beginPath();ctx.rect(d.position.x/100,d.position.y/100,d.width/100,d.height/100);ctx.clip();ctx.fillStyle=color(d.color);ctx.font=`${d.flags&4?'italic ':''}${d.flags&2?'bold ':''}${d.fontSize/100}px sans-serif`;ctx.textBaseline='top';ctx.textAlign=['left','center','right'][d.align];const x=(d.position.x+(d.align===1?d.width/2:d.align===2?d.width:0))/100;d.text.split('\n').forEach((line,i)=>ctx.fillText(line,x,d.position.y/100+i*d.fontSize/100*1.2));}
    else if(item.kind==='Image'&&images.has(item.image)){const img=images.get(item.image),iw=d.width/100,ih=d.height/100;ctx.translate((d.position.x+d.width/2)/100,(d.position.y+d.height/2)/100);ctx.rotate(d.rotation/100*Math.PI/180);ctx.scale(d.flips&1?-1:1,d.flips&2?-1:1);ctx.globalAlpha=d.opacity/255;ctx.beginPath();ctx.rect(-iw/2,-ih/2,iw,ih);ctx.clip();const k=d.fit===1?Math.min(iw/img.naturalWidth,ih/img.naturalHeight):Math.max(iw/img.naturalWidth,ih/img.naturalHeight);const dw=d.fit===0?iw:img.naturalWidth*k,dh=d.fit===0?ih:img.naturalHeight*k;ctx.drawImage(img,-dw/2,-dh/2,dw,dh);}
    ctx.restore();
  }
}
document.querySelectorAll('[data-view]').forEach(n=>n.addEventListener('click',()=>run(()=>navigate(n.dataset.view))));
$('refresh').addEventListener('click',()=>run(()=>navigate(view,page,filter)));
$('logout').addEventListener('click',()=>run(async()=>{await api('logout',{},true);clearSession('Signed out. Your workspace is closed.');}));
function theme(value){document.documentElement.dataset.theme=value;$('theme').setAttribute('aria-label',value==='dark'?'Switch to light theme':'Switch to dark theme');try{localStorage.setItem('af-desk-theme',value);}catch{}}
$('theme').addEventListener('click',()=>theme(document.documentElement.dataset.theme==='dark'?'light':'dark'));
try{theme(localStorage.getItem('af-desk-theme')==='light'?'light':'dark');}catch{}
(async()=>{try{const response=await fetch('/admin/api/session',{credentials:'same-origin',cache:'no-store',signal:controller.signal});
  if(!response.ok){clearSession(new URLSearchParams(location.search).get('login')==='failed'?'Sign in was unsuccessful or this account is not approved for staff access.':'For the owner and approved moderators.');return;}
  session=await response.json();$('login').hidden=true;$('workspace').hidden=false;$('team-nav').hidden=session.role!=='owner';$('staff-role').textContent=session.role;$('staff-id').textContent='GitHub ID '+session.id;await run(()=>navigate('overview'));
}catch{clearSession('The desk could not be reached. Reload this page to try again.');}})();
