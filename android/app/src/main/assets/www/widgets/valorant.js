import { API, PANEL_HEADERS, api, h, haptic, labelled, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// Deskmate: the Valorant page. One scrolling column, built for one hand during a match:
//   1. match status (your own Riot presence, Discord-Rich-Presence style; tap it for the map's callout image)
//   2. Save clip · Hold to talk · Mic mute, big, filling the rest of the first screen
//   3. post-match stats
// Everything comes from /api/valorant (agent: ValorantHost.cs).
// Nothing here is invented: a field the computer did not report shows as unavailable.

const SVG = (inner, cls = '') => {
  const s = document.createElement('span');
  s.className = 'vl-i ' + cls;
  s.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${inner}</svg>`;
  return s;
};
const ICON = {
  party: '<g fill="currentColor" stroke="none"><circle cx="9" cy="8" r="3.5"/><path d="M2 21v-2a7 7 0 0 1 14 0v2Z"/><circle cx="17" cy="6" r="3"/><path d="M16 11a6 6 0 0 1 7 6v1h-5a9 9 0 0 0-4-6Z"/></g>',
  gamepad: '<path fill="currentColor" stroke="none" fill-rule="evenodd" d="M7 4h10c3 0 4.5 1.8 5 5l1 7c.6 4.3-3 5.8-5.5 2.8L15 16H9l-2.5 2.8C4 21.8.4 20.3 1 16l1-7c.5-3.2 2-5 5-5Zm-.5 3v2H4.6v1.8h1.9v2h1.8v-2h2V9h-2V7Zm11.6.4a1.3 1.3 0 1 0 0 2.6 1.3 1.3 0 0 0 0-2.6Zm-3 3a1.3 1.3 0 1 0 0 2.6 1.3 1.3 0 0 0 0-2.6Z"/>',
  clip: '<path d="m22 8-6 4 6 4V8Z"/><rect x="2" y="6" width="14" height="12" rx="2"/>',
  mic: '<path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z"/><path d="M19 10v2a7 7 0 0 1-14 0v-2"/><path d="M12 19v3"/>',
  micOff: '<path d="m2 2 20 20"/><path d="M18.89 13.23A7.12 7.12 0 0 0 19 12v-2"/><path d="M5 10v2a7 7 0 0 0 12 5"/><path d="M15 9.34V5a3 3 0 0 0-5.68-1.33"/><path d="M9 9v3a3 3 0 0 0 5.12 2.12"/><path d="M12 19v3"/>',
  talk: '<path d="M7.9 20A9 9 0 1 0 4 16.1L2 22Z"/>',
  close: '<path d="M18 6 6 18"/><path d="m6 6 12 12"/>',
  refresh: '<path d="M21 12a9 9 0 1 1-3-6.7L21 8"/><path d="M21 3v5h-5"/>',
};

const post = async (path) => {
  const r = await fetch(`${API}${path}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
  let body = null;
  try { body = await r.json(); } catch (_) {}
  return { ok: r.ok, status: r.status, body };
};
const mmss = (ms) => { const s = Math.max(0, Math.floor(ms / 1000)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };
const logo = () => { const im = new Image(); im.alt = ''; im.className = 'vl-logo'; im.src = 'valorant.png'; return im; };
const mapUrl = (name) => `${API}/valorant/map?name=${encodeURIComponent(name)}`;

/** Full-screen, pinch-zoomable map image (tap ✕ or the dark edge to close). */
function openMapViewer(name) {
  haptic();
  const img = new Image();
  img.alt = name;
  img.src = mapUrl(name);
  const stage = h('div', 'vl-viewer-stage', img);
  const close = labelled(h('button', 'vl-viewer-close', SVG(ICON.close)), 'Close');
  const viewer = h('div', 'vl-viewer', stage, h('div', 'vl-viewer-title', name), close);
  let scale = 1, tx = 0, ty = 0, lastTap = 0;
  const pts = new Map();
  let start = null;
  const apply = () => { img.style.transform = `translate(${tx}px, ${ty}px) scale(${scale})`; };
  const clamp = () => { scale = Math.min(6, Math.max(1, scale)); if (scale === 1) { tx = 0; ty = 0; } };
  stage.addEventListener('pointerdown', (e) => {
    stage.setPointerCapture(e.pointerId);
    pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
    const now = Date.now();
    if (pts.size === 1 && now - lastTap < 300) { scale = scale > 1 ? 1 : 2.5; clamp(); apply(); lastTap = 0; return; }
    lastTap = now;
    start = { scale, tx, ty, pts: new Map(pts) };
  });
  stage.addEventListener('pointermove', (e) => {
    if (!pts.has(e.pointerId) || !start) return;
    pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
    const a = [...pts.values()], b = [...start.pts.values()];
    if (a.length >= 2 && b.length >= 2) {
      const dist = (p) => Math.hypot(p[0].x - p[1].x, p[0].y - p[1].y);
      scale = start.scale * (dist(a) / Math.max(1, dist(b)));
    } else if (a.length === 1 && b.length >= 1 && scale > 1) {
      tx = start.tx + (a[0].x - b[0].x);
      ty = start.ty + (a[0].y - b[0].y);
    }
    clamp(); apply();
  });
  const end = (e) => { pts.delete(e.pointerId); start = { scale, tx, ty, pts: new Map(pts) }; };
  stage.addEventListener('pointerup', end);
  stage.addEventListener('pointercancel', end);
  close.addEventListener('click', () => viewer.remove());
  img.onerror = () => { stage.replaceChildren(h('div', 'vl-viewer-missing', `No image for ${name}`)); };
  document.body.append(viewer);
}

W.valorant = {
  needs: ['state', 'tick'],
  create(box) {
    box.classList.add('vl-box');
    let data = null, loaded = false, failed = false;

    // ---- 1. match status ------------------------------------------------------------------------------------------
    const thumb = h('div', 'vl-thumb');
    const mLine1 = h('div', 'vl-m1'), mLine2 = h('div', 'vl-m2');
    const partyCount = h('span'), elapsed = h('span');
    const party = h('span', 'vl-party', SVG(ICON.party), partyCount);
    const timer = h('span', 'vl-time', SVG(ICON.gamepad), elapsed);
    const activity = h('div', 'vl-activity', party, timer);
    const match = h('section', 'vl-match', thumb, h('div', 'vl-mtext', h('div', 'vl-brand', 'VALORANT'), mLine1, mLine2), activity);
    match.addEventListener('click', () => { const m = data?.presence?.map; if (m && data.presence.mapImage) openMapViewer(m); });

    // ---- 2. controls --------------------------------------------------------------------------------------------
    const clipLabel = h('b', null, 'Save clip'), clipSub = h('small', null, 'F10');
    const clipBtn = labelled(h('button', 'vl-btn vl-clip', SVG(ICON.clip), clipLabel, clipSub), 'Save clip: press the Medal hotkey');
    const talkLabel = h('b', null, 'Hold to talk'), talkSub = h('small', null, '');
    const talkBtn = labelled(h('button', 'vl-btn vl-talk', SVG(ICON.talk), talkLabel, talkSub), 'Hold to talk in team voice');
    const muteIcon = h('span', 'vl-mute-icon', SVG(ICON.mic));
    const muteLabel = h('b', null, 'Mic'), muteSub = h('small', null, '');
    const muteBtn = labelled(h('button', 'vl-btn vl-mute', muteIcon, muteLabel, muteSub), 'Mute or unmute the game microphone');
    // left to right: emergency mute (its own look, out of the way), Save clip, then Hold to talk, the biggest, by the right thumb
    const controls = h('section', 'vl-controls', muteBtn, clipBtn, talkBtn);

    // ---- 3. post-match stats ------------------------------------------------------------------------------------------
    const statsBody = h('div', 'vl-stats-body');
    const refresh = labelled(h('button', 'vl-refresh', SVG(ICON.refresh)), 'Check the match history now');
    const statsCard = h('section', 'vl-card vl-stats', h('div', 'vl-h', 'After the match', refresh), statsBody);

    const scroll = h('div', 'vl-scroll', match, controls, statsCard);
    box.append(scroll);

    const load = async () => {
      try { data = await api('/valorant'); loaded = true; failed = false; }
      catch (_) { failed = true; }
      render();
      return !failed;
    };
    const poll = every(1000, load, 3000);

    // ---- Save clip: one press per tap; a retried request carries the same tap id ------------------------------------
    let clipBusy = false, clipTimer = null;
    clipBtn.addEventListener('click', async () => {
      if (clipBusy) return;
      clipBusy = true; clipBtn.classList.add('busy'); haptic();
      const tap = Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
      let msg = 'Hotkey sent', ok = true;
      try {
        const r = await post(`/valorant/clip?tap=${tap}`);
        if (!r.ok || !r.body?.sent) { ok = false; msg = r.body?.error || 'Not sent'; }
      } catch (_) { ok = false; msg = 'PC not answering'; }
      clipLabel.textContent = msg;
      clipBtn.classList.toggle('done', ok);
      clipBtn.classList.toggle('err', !ok);
      clearTimeout(clipTimer);
      clipTimer = setTimeout(() => { clipLabel.textContent = 'Save clip'; clipBtn.classList.remove('done', 'err'); }, 1800);
      setTimeout(() => { clipBusy = false; clipBtn.classList.remove('busy'); }, 600);
    });

    // ---- Hold to talk: a lease the PC lets go of by itself if this phone stops renewing it ------------------------------
    let press = null, holdTimer = null;
    let holding = false, lease = null, renewTimer = null;
    const talkDown = async (gesture) => {
      holding = true; haptic(); renderTalk();
      try {
        const r = await post('/valorant/talk/down');
        if (press !== gesture) {
          if (r.body?.lease) post(`/valorant/talk/up?lease=${r.body.lease}`).catch(() => {});
          return;
        }
        lease = r.body?.lease || null;
        renewTimer = setInterval(async () => {
          if (!lease) return;
          try { const rr = await post(`/valorant/talk/renew?lease=${lease}`); if (rr.status === 404 && press === gesture) talkUp(); } catch (_) {}
        }, 300);
      } catch (_) { if (press === gesture) { talkUp(); toast('PC not answering', 'err'); } }
      load();
    };
    const talkUp = () => {
      clearTimeout(holdTimer);
      press = null;
      if (!holding) return;
      holding = false;
      clearInterval(renewTimer); renewTimer = null;
      const l = lease; lease = null;
      post('/valorant/talk/up' + (l ? `?lease=${l}` : '')).catch(() => {}); // the PC also lets go when the lease lapses
      renderTalk();
      setTimeout(load, 150);
    };
    talkBtn.addEventListener('pointerdown', (e) => {
      if (press || !e.isPrimary || e.button !== 0) return;
      const gesture = { id: e.pointerId, x: e.clientX, y: e.clientY };
      press = gesture;
      try { talkBtn.setPointerCapture(e.pointerId); } catch (_) {}
      if (e.pointerType === 'mouse') talkDown(gesture);
      else holdTimer = setTimeout(() => talkDown(gesture), 150);
    });
    talkBtn.addEventListener('pointermove', (e) => {
      if (press?.id === e.pointerId && !holding && Math.hypot(e.clientX - press.x, e.clientY - press.y) > 8) talkUp();
    });
    for (const ev of ['pointerup', 'pointercancel', 'lostpointercapture']) {
      talkBtn.addEventListener(ev, (e) => { if (press?.id === e.pointerId) talkUp(); });
    }
    talkBtn.addEventListener('contextmenu', (e) => e.preventDefault());
    const onHide = () => { if (document.visibilityState !== 'visible') talkUp(); };
    document.addEventListener('visibilitychange', onHide);
    window.addEventListener('blur', talkUp);
    // swiping to another page moves this one off screen: let go
    const seen = new IntersectionObserver((entries) => { if (entries.some((x) => !x.isIntersecting)) talkUp(); }, { threshold: 0 });
    seen.observe(box);

    // ---- Mic mute: the PC's real state, including changes made there ---------------------------------------------------
    let muteBusy = false;
    muteBtn.addEventListener('click', async () => {
      if (muteBusy) return;
      muteBusy = true; haptic(); muteBtn.classList.add('busy');
      try {
        const r = await post('/valorant/mic/toggle');
        if (data && r.body) data.mic = { available: r.body.available, muted: r.body.muted, name: r.body.name, error: r.body.error };
        if (!r.body?.available) toast(r.body?.error || 'Microphone unavailable', 'err');
      } catch (_) { toast('PC not answering', 'err'); }
      muteBusy = false; muteBtn.classList.remove('busy');
      renderMute();
      load();
    });

    refresh.addEventListener('click', async () => { haptic(); try { await post('/valorant/stats/refresh'); } catch (_) {} setTimeout(load, 2500); });

    // ---- rendering ----------------------------------------------------------------------------------------------------
    const renderTalk = () => {
      const t = data?.talk;
      const key = (t?.key || 'v').toUpperCase();
      const mutedMic = data?.mic?.muted === true;
      talkBtn.classList.toggle('held', holding);
      talkBtn.classList.toggle('live', holding && t?.down === true);
      talkBtn.classList.toggle('blocked', holding && t?.down === false && t?.reason && t.reason !== 'Talking');
      talkLabel.textContent = holding ? (t?.down ? 'Talking' : t?.reason && t.reason !== 'Not held' ? t.reason : 'Holding…') : 'Hold to talk';
      talkSub.textContent = mutedMic && !holding ? `${key} · mic is muted` : `${key} · team voice`;
    };
    const renderMute = () => {
      const m = data?.mic;
      const muted = m?.muted === true;
      muteBtn.classList.toggle('on', muted);
      muteBtn.classList.toggle('unknown', !m?.available);
      muteIcon.replaceChildren(SVG(muted ? ICON.micOff : ICON.mic));
      muteLabel.textContent = !m?.available ? 'Mic unavailable' : muted ? 'MIC MUTED' : 'Mic live';
      muteSub.textContent = m?.available ? (m.name || '') + ' · tap to ' + (muted ? 'unmute' : 'mute') : (m?.error || '');
    };
    const renderMatch = () => {
      const g = data?.game, p = data?.presence;
      const since = p ? p.since : g?.since;
      party.hidden = !g?.running || !p?.partySize;
      timer.hidden = !g?.running || !since;
      activity.hidden = party.hidden && timer.hidden;
      if (!party.hidden) {
        partyCount.textContent = p.maxPartySize ? `${p.partySize} of ${p.maxPartySize}` : String(p.partySize);
        party.setAttribute('aria-label', 'Party size: ' + partyCount.textContent);
      }
      if (!timer.hidden) elapsed.textContent = mmss(Date.now() - since);
      thumb.replaceChildren();
      if (!loaded) { mLine1.textContent = failed ? 'PC not answering' : 'Connecting…'; mLine2.textContent = ''; return; }
      if (!g?.running) {
        thumb.append(logo());
        mLine1.textContent = 'Not running';
        mLine2.textContent = 'Opens by itself when VALORANT starts';
        match.className = 'vl-match off';
        return;
      }
      match.className = 'vl-match' + (p?.loop === 'INGAME' ? ' live' : '');
      if (p?.map && p.mapImage) { const im = new Image(); im.alt = ''; im.src = mapUrl(p.map); thumb.append(im); }
      else thumb.append(logo());
      if (!p) {
        mLine1.textContent = 'In VALORANT';
        mLine2.textContent = data.presenceError || 'Waiting for the Riot Client';
        return;
      }
      mLine1.textContent = [p.mode, p.map].filter(Boolean).join(' · ') || 'VALORANT';
      const score = p.ally != null && p.enemy != null ? ` · ${p.ally}–${p.enemy}` : '';
      mLine2.textContent = p.status + score;
    };
    const renderStats = () => {
      const st = data?.stats;
      statsBody.replaceChildren();
      if (!st) return;
      if (!st.ready) {
        statsBody.append(h('div', 'vl-setup', h('b', null, 'Stats need setting up'), h('ul', null, ...(st.missing || []).map((m) => h('li', null, m))),
          h('small', null, `Source: ${st.provider}`)));
        return;
      }
      const last = st.last, today = st.today;
      if (last) {
        const res = last.won === true ? 'Win' : last.won === false ? 'Loss' : 'Draw';
        const rounds = last.roundsWon != null && last.roundsLost != null ? ` ${last.roundsWon}–${last.roundsLost}` : '';
        statsBody.append(h('div', 'vl-last',
          h('div', 'vl-last-head', h('b', 'vl-res ' + res.toLowerCase(), res + rounds), h('span', null, [last.mode, last.map, last.agent].filter(Boolean).join(' · '))),
          h('div', 'vl-kvs',
            h('div', 'vl-kv big', h('small', null, 'Kills'), h('b', null, String(last.kills))),
            h('div', 'vl-kv big', h('small', null, 'Deaths'), h('b', null, String(last.deaths))),
            h('div', 'vl-kv big', h('small', null, 'K/D'), h('b', null, last.deathless ? `${last.kd} (no deaths)` : last.kd.toFixed(2))))));
      } else statsBody.append(h('div', 'vl-empty', 'No finished match found yet'));
      statsBody.append(h('div', 'vl-today', h('small', null, `Today (${today.date})`),
        h('b', null, `${today.wins} W – ${today.losses} L`), today.draws ? h('small', null, ` · ${today.draws} draw${today.draws > 1 ? 's' : ''}`) : null));
      if (st.error) statsBody.append(h('div', 'vl-note warn', st.error));
      else if (st.updatedAt) statsBody.append(h('div', 'vl-note', `Match history checked ${mmss(Date.now() - st.updatedAt)} ago · ${st.account || ''}`));
    };
    const render = () => { renderMatch(); renderTalk(); renderMute(); renderStats(); clipSub.textContent = (data?.clip?.key || 'f10').toUpperCase() + ' · Medal'; };

    render();
    return {
      update(kind) { if (kind === 'tick') poll(); renderMatch(); },
      destroy() { talkUp(); seen.disconnect(); document.removeEventListener('visibilitychange', onHide); window.removeEventListener('blur', talkUp); },
    };
  },
};
