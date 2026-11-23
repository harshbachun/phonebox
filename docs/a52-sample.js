// Used only by a52-preview.py. All actions stay inside the official browser demo.
(() => {
  const page = (id, title, icon, type, options) => ({
    id, title, icon, enabled: true, widgets: [{ id: id + '-widget', type, x: 0, y: 0, w: 12, h: 6, options }],
  });
  if (!localStorage.getItem('pb-demo-config')) {
    const config = {
      pages: [
        page('discord', 'Discord', '🎧', 'discord', { layout: 'page', control: true, channels: true, favorites: ['g1', 'g2'], dimFrom: 4 }),
        page('discord-official', 'Discord Official', '🎧', 'discord', { control: true, channels: true, notifications: true }),
        page('ai', 'AI quotas', '✳️', 'quotas', { style: 'bars', providers: ['claude', 'codex'], rotate: false, pageSize: 0 }),
        page('valorant', 'VALORANT', '🎮', 'valorant', {}),
      ],
      display: { idleReturnSec: 0, pageBar: 'bar' }, night: { enabled: false },
      discord: { chat: true, screenShare: true }, rules: [], shortcuts: [],
    };
    localStorage.setItem('pb-demo-config', JSON.stringify(config));
  }
  const demo = window.fetch.bind(window);
  const since = Date.now() - 96000;
  let muted = false;
  window.fetch = async (input, options) => {
    const path = new URL(typeof input === 'string' ? input : input.url, location.href).pathname;
    if (path === '/agent/api/valorant') return Response.json({
      game: { running: true, since, focused: true },
      presence: { status: 'In game', loop: 'INGAME', map: 'Ascent', mode: 'Custom', partySize: 1, maxPartySize: 12, since },
      mic: { available: true, muted, name: 'Sample microphone' }, clip: { key: 'f10' }, talk: { key: 'v' },
    });
    if (path.startsWith('/agent/api/valorant/')) {
      if (path.endsWith('/mic/toggle')) muted = !muted;
      return Response.json({ ok: true, muted, lease: 'preview' });
    }
    const result = await demo(input, options);
    if (!result.ok) return result;
    if (path === '/agent/api/discord') {
      const data = await result.json();
      for (const member of data.snapshot.members) member.isSelf = member.id === '1';
      return Response.json(data);
    }
    if (['/agent/api/discord/mute', '/agent/api/discord/deafen'].includes(path)) {
      const { snapshot } = await (await demo('/agent/api/discord')).json();
      return Response.json({ ok: true, muted: snapshot.muted, deafened: snapshot.deafened });
    }
    return result;
  };
})();
