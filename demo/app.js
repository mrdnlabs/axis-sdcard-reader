// Demo director. Everything on screen is a pure function of time t (window.__seek(t)), so the video renders
// frame-exact and deterministically. The app window is a faithful rebuild of the real UI; the timeline canvases
// are direct ports of TimelineControl.OnRender / DayOverviewControl.OnRender; the camera feed is synthetic (so no
// real camera footage ever ends up in a public video).
'use strict';
(() => {
    const Q = new URLSearchParams(location.search);
    const RENDER = Q.has('render');
    const REF = Q.has('ref');          // 1440x900 app-only view with the fixture data, for diffing against a real capture
    if (RENDER) document.body.classList.add('render');

    const DURATION = Music.DURATION;

    // ------------------------------------------------------------------------------------------------ helpers
    const $ = id => document.getElementById(id);
    const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
    const lerp = (a, b, k) => a + (b - a) * k;
    const prog = (a, b, t) => clamp((t - a) / (b - a), 0, 1);
    const ease = k => k < .5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2;
    const easeOut = k => 1 - Math.pow(1 - k, 3);
    const back = k => { const c1 = 1.5, c3 = c1 + 1; return 1 + c3 * Math.pow(k - 1, 3) + c1 * Math.pow(k - 1, 2); };
    const env = (t, a, b, fi = .3, fo = .3) => Math.min(prog(a, a + fi, t), 1 - prog(b - fo, b, t));
    function prng(seed) {
        let a = seed | 0;
        return () => { a = a + 0x6D2B79F5 | 0; let x = Math.imul(a ^ a >>> 15, 1 | a);
                       x = x + Math.imul(x ^ x >>> 7, 61 | x) ^ x; return ((x ^ x >>> 14) >>> 0) / 4294967296; };
    }
    const H = (h, m = 0, s = 0) => h * 3600 + m * 60 + s;
    const pad = n => String(Math.floor(n)).padStart(2, '0');
    const clk = s => { s = ((Math.floor(s) % 86400) + 86400) % 86400; return `${pad(s / 3600)}:${pad(s % 3600 / 60)}:${pad(s % 60)}`; };
    const hm = s => clk(s).slice(0, 5);
    const fmtDur = sec => sec >= 3600 ? `${Math.floor(sec / 3600)}h ${Math.floor(sec % 3600 / 60)}m`
                                      : `${Math.floor(sec / 60)}m ${pad(sec % 60)}s`;       // PlaybackViewModel.FormatDuration
    const segDur = sec => sec >= 3600 ? `${Math.floor(sec / 3600)}h ${Math.floor(sec % 3600 / 60)}m`
                                      : `${Math.max(1, Math.floor(sec / 60))}m`;           // TimelineControl label
    function rr(g, x, y, w, h, r) {
        r = Math.max(0, Math.min(r, w / 2, h / 2));
        g.beginPath(); g.moveTo(x + r, y); g.arcTo(x + w, y, x + w, y + h, r); g.arcTo(x + w, y + h, x, y + h, r);
        g.arcTo(x, y + h, x, y, r); g.arcTo(x, y, x + w, y, r); g.closePath();
    }
    const cache = new WeakMap();
    function text(el, v) { if (cache.get(el) !== v) { el.textContent = v; cache.set(el, v); } }
    function html(el, v) { if (el._h !== v) { el.innerHTML = v; el._h = v; } }
    function css(el, prop, v) { if (el['_' + prop] !== v) { el.style[prop] = v; el['_' + prop] = v; } }
    const shown = (el, on) => css(el, 'display', on ? '' : 'none');
    const opa = (el, v) => css(el, 'opacity', String(Math.round(clamp(v, 0, 1) * 1000) / 1000));
    const cls = (el, c, on) => el.classList.toggle(c, !!on);

    // ------------------------------------------------------------------------------------------------ theme (dark)
    const T = {
        Subtle2: '#0f141a', TlBorder: '#242b34', TlLine: '#1c232c', Border2: '#2a313b', Accent: '#3b82f6',
        AccentTint: 'rgba(59,130,246,.157)', Sel: '#a855f7', SelFill: 'rgba(168,85,247,.298)', Playhead: '#eef2f7',
        PlayheadHalo: 'rgba(0,0,0,.5)', Faint: '#5f6a77', Muted2: '#8b95a0',
        kind: { continuous: '#3b82f6', event: '#ef4444', manual: '#eab308', scheduled: '#14b8a6', other: '#8b95a0' },
    };
    const PRIORITY = { manual: 4, event: 3, scheduled: 2, continuous: 1, other: 0 };

    // ------------------------------------------------------------------------------------------------ demo data
    // Modelled on a real AXIS Camera Station Edge card: one continuous recording spanning the day, plus parallel
    // motion recordings (and one manual) on the same view. Names/serials are fictitious.
    const EV0 = H(12, 2, 47), EV1 = H(12, 3, 50);        // the "hero" motion event
    const MAN0 = H(12, 4, 20), MAN1 = H(12, 5, 40);       // a manual recording
    const MARK_IN = H(12, 2, 30), MARK_OUT = H(12, 6, 42);
    const recs = [{ start: H(7), end: H(19, 30), kind: 'continuous' }];
    {
        const r = prng(71426);
        for (let t = H(7, 41, 12); t < H(19, 10);) {
            const dur = 24 + Math.floor(r() * 68);
            if (t + dur < H(11, 57) || t > H(12, 9)) recs.push({ start: t, end: t + dur, kind: 'event' });
            const hr = t / 3600;
            const busy = (hr > 11.2 && hr < 13.5) || (hr > 16.5 && hr < 18.5) ? 0.42 : 1;
            t += dur + Math.floor((380 + r() * 1320) * busy);
        }
        recs.push({ start: EV0, end: EV1, kind: 'event' }, { start: MAN0, end: MAN1, kind: 'manual' });
        recs.sort((a, b) => a.start - b.start);
    }
    const dayClips = recs.length;
    const TOTAL = 77, JUL12 = 2, CAM2 = 12, CAM3 = TOTAL - dayClips - JUL12 - CAM2;
    const footage = recs.reduce((s, r) => s + (r.end - r.start), 0);

    const bestAt = s => {   // PlaybackViewModel.BestSegmentIndex: highest-priority recording covering s
        let best = null;
        for (const r of recs) if (s >= r.start && s < r.end && (!best || PRIORITY[r.kind] > PRIORITY[best.kind])) best = r;
        return best;
    };

    // ------------------------------------------------------------------------------------------------ timeline port
    const SPANS = [300, 900, 1800, 3600, 10800, 21600, 86400];
    const tickStep = s => s <= 300 ? 30 : s <= 900 ? 120 : s <= 1800 ? 300 : s <= 3600 ? 600 : s <= 10800 ? 1800 : s <= 21600 ? 3600 : 10800;
    const DAYNAME = { 0: 'Jul 14', 1: 'Jul 15', '-1': 'Jul 13' };

    function drawDetail(g, W, st) {                              // TimelineControl.OnRender
        const HZ = 13, TH = 52, top = HZ;
        g.clearRect(0, 0, W, 83);
        const spp = st.span / W, left = st.center - st.span / 2, right = st.center + st.span / 2;
        rr(g, .5, top + .5, W - 1, TH - 1, 7); g.fillStyle = T.Subtle2; g.fill(); g.lineWidth = 1; g.strokeStyle = T.TlBorder; g.stroke();
        g.save(); g.beginPath(); g.rect(0, top, W, TH); g.clip();

        const step = tickStep(st.span), labels = [];
        let lastX = NaN;
        for (let b = Math.floor(left / step) * step; b <= right + step; b += step) {
            const x = Math.round((b - left) / spp) + .5;
            if (x < -60 || x > W + 60 || Math.abs(x - lastX) < 1) continue;
            lastX = x;
            const mid = ((b % 86400) + 86400) % 86400 === 0;
            g.strokeStyle = mid ? T.Border2 : T.TlLine; g.beginPath(); g.moveTo(x, top + 1); g.lineTo(x, top + TH - 1); g.stroke();
            labels.push([x, mid ? DAYNAME[Math.floor(b / 86400)] : (st.span <= 900 ? clk(b) : hm(b)), mid]);
        }

        const vis = st.segs.filter(s => s.end >= left && s.start <= right).sort((a, b) => PRIORITY[a.kind] - PRIORITY[b.kind]);
        g.font = '11px Consolas'; g.textBaseline = 'middle';
        for (const s of vis) {
            const x1 = (s.start - left) / spp, w = Math.max(2, (s.end - left) / spp - x1);
            rr(g, x1, top + 8, w, 34, 4); g.fillStyle = T.kind[s.kind]; g.fill();
            if (w >= 90) {
                let label = `${hm(s.start)} · ${segDur(s.end - s.start)}`;
                const maxW = w - 16;
                while (label.length > 1 && g.measureText(label).width > maxW) label = label.slice(0, -2) + '…';
                g.fillStyle = s.kind === 'manual' ? '#000' : '#fff';
                g.fillText(label, Math.max(x1 + 8, 8), top + 8 + 17);
            }
        }
        if (st.selIn != null && st.selOut != null && st.selOut > st.selIn && st.selOut > left && st.selIn < right) {
            const x1 = (st.selIn - left) / spp, x2 = (st.selOut - left) / spp;
            rr(g, x1, top + 4, Math.max(2, x2 - x1), TH - 8, 4); g.fillStyle = T.SelFill; g.fill();
            g.lineWidth = 1.5; g.strokeStyle = T.Sel; g.stroke();
        }
        const bracket = (x, isIn) => {                           // DrawMarkBracket
            const arm = 7, th = 2, bt = top + 4, bb = top + TH - 4, ax = isIn ? x : x - arm;
            g.fillStyle = T.Sel; g.fillRect(x - th / 2, bt, th, bb - bt); g.fillRect(ax, bt, arm, th); g.fillRect(ax, bb - th, arm, th);
        };
        if (st.selIn != null && st.selIn >= left && st.selIn <= right) bracket((st.selIn - left) / spp, true);
        if (st.selOut != null && st.selOut >= left && st.selOut <= right) bracket((st.selOut - left) / spp, false);
        g.restore();

        g.font = '10px Consolas'; g.textBaseline = 'top';
        const placed = [];                                        // on-track ticks only, no collisions, days first
        for (const [x, s, mid] of labels.filter(l => l[0] >= -1 && l[0] <= W + 1).sort((a, b) => b[2] - a[2])) {
            const w = g.measureText(s).width, l = clamp(x - w / 2, 0, Math.max(0, W - w)), r = l + w;
            if (placed.some(p => l < p[1] + 6 && r + 6 > p[0])) continue;
            placed.push([l, r]);
            g.fillStyle = mid ? T.Muted2 : T.Faint;
            g.fillText(s, l, top + TH + 3 + 1);
        }
        const px = W / 2;
        g.strokeStyle = T.PlayheadHalo; g.lineWidth = 1;
        for (const dx of [-1.5, 1.5]) { g.beginPath(); g.moveTo(px + dx, top - 4); g.lineTo(px + dx, top + TH + 4); g.stroke(); }
        g.fillStyle = T.Playhead; g.fillRect(px - 1, top - 4, 2, TH + 8);
        g.fillStyle = T.Accent;
        const tri = (a, b, c) => { g.beginPath(); g.moveTo(...a); g.lineTo(...b); g.lineTo(...c); g.closePath(); g.fill(); };
        tri([px - 6, top - 13], [px + 6, top - 13], [px, top - 4]);
        tri([px - 6, top + TH + 13], [px + 6, top + TH + 13], [px, top + TH + 4]);
    }

    function drawOverview(g, W, st) {                            // DayOverviewControl.OnRender (canvas has 2px bleed)
        const SH = 18, B = 2;
        g.clearRect(0, 0, W, SH + 2 * B);
        g.save(); g.translate(0, B);
        const dayStart = Math.floor(st.center / 86400) * 86400, day = 86400;
        rr(g, .5, .5, W - 1, SH - 1, 5); g.fillStyle = T.Subtle2; g.fill(); g.lineWidth = 1; g.strokeStyle = T.TlBorder; g.stroke();
        g.save(); g.beginPath(); g.rect(0, 0, W, SH); g.clip();
        g.strokeStyle = T.TlLine;
        for (const h of [6, 12, 18]) { const x = Math.round(h * 3600 / day * W) + .5; g.beginPath(); g.moveTo(x, 1); g.lineTo(x, SH - 1); g.stroke(); }
        for (const s of [...st.segs].sort((a, b) => PRIORITY[a.kind] - PRIORITY[b.kind])) {
            const a = s.start - dayStart, e = s.end - dayStart;
            if (e < 0 || a > day) continue;
            const x1 = Math.max(0, a) / day * W, x2 = Math.min(day, e) / day * W;
            rr(g, x1, 4, Math.max(1.5, x2 - x1), 10, 2); g.fillStyle = T.kind[s.kind]; g.fill();
        }
        if (st.selIn != null && st.selOut != null && st.selOut > st.selIn) {
            const a = Math.max(0, st.selIn - dayStart), e = Math.min(day, st.selOut - dayStart);
            if (e > 0 && a < day) { g.fillStyle = T.Sel; g.fillRect(a / day * W, SH - 3.5, Math.max(2, (e - a) / day * W), 2.5); }
        }
        for (const m of [st.selIn, st.selOut]) {
            if (m == null) continue;
            const mx = (m - dayStart) / day * W;
            if (mx >= 0 && mx <= W) { g.fillStyle = T.Sel; g.fillRect(mx - 1, 3, 2, SH - 6); }
        }
        g.restore();
        let vl = clamp((st.center - st.span / 2 - dayStart) / day * W, 0, W), vr = clamp((st.center + st.span / 2 - dayStart) / day * W, 0, W);
        if (vr - vl >= 1) {
            rr(g, vl, -1, Math.max(8, vr - vl), SH + 2, 4); g.fillStyle = T.AccentTint; g.fill();
            g.lineWidth = 1.5; g.strokeStyle = T.Accent; g.stroke();
        }
        const cx = clamp((st.center - dayStart) / day, 0, 1) * W;
        g.fillStyle = T.Playhead; g.fillRect(cx - .75, 0, 1.5, SH);
        g.restore();
    }

    // ------------------------------------------------------------------------------------------------ camera feed
    // A parking lot outside a building entrance, seen from a dome camera. World units 1600x900 (16:9).
    const WW = 1600, WH = 900;
    const kf = y => .8 + .2 * (y / WH);                 // keystone: far rows narrower
    const kx = (x, y) => WW / 2 + (x - WW / 2) * kf(y);
    const shade = (hex, a) => {
        const n = parseInt(hex.slice(1), 16), c = [n >> 16, n >> 8 & 255, n & 255]
            .map(v => Math.round(a >= 0 ? v + (255 - v) * a : v * (1 + a)));
        return `rgb(${c[0]},${c[1]},${c[2]})`;
    };

    function car(g, cx, cy, head, col, o = {}) {
        const s = kf(cy), L = 205 * s, W = 94 * s, px = kx(cx, cy);
        g.save(); g.translate(px + 9 * s, cy + 13 * s); g.rotate(head + Math.PI / 2);
        for (let i = 3; i >= 0; i--) { rr(g, -W / 2 - i * 3, -L / 2 - i * 3, W + i * 6, L + i * 6, W * .32 + i * 3); g.fillStyle = `rgba(0,0,0,${.07 + (3 - i) * .045})`; g.fill(); }
        g.restore();
        g.save(); g.translate(px, cy); g.rotate(head + Math.PI / 2);
        const lg = g.createLinearGradient(-W / 2, 0, W / 2, 0);
        lg.addColorStop(0, shade(col, -.42)); lg.addColorStop(.16, shade(col, -.06)); lg.addColorStop(.42, shade(col, .2));
        lg.addColorStop(.62, col); lg.addColorStop(1, shade(col, -.45));
        rr(g, -W / 2, -L / 2, W, L, W * .3); g.fillStyle = lg; g.fill();
        g.lineWidth = 1.2 * s; g.strokeStyle = 'rgba(0,0,0,.35)'; g.stroke();
        // mirrors
        g.fillStyle = shade(col, -.3); rr(g, -W / 2 - 7 * s, -L * .16, 9 * s, 12 * s, 3 * s); g.fill(); rr(g, W / 2 - 2 * s, -L * .16, 9 * s, 12 * s, 3 * s); g.fill();
        // glass
        const glass = g.createLinearGradient(0, -L * .22, 0, L * .34);
        glass.addColorStop(0, '#2b3949'); glass.addColorStop(.5, '#17202b'); glass.addColorStop(1, '#253241');
        g.fillStyle = glass;
        g.beginPath(); g.moveTo(-W * .39, -L * .2); g.lineTo(W * .39, -L * .2); g.lineTo(W * .34, -L * .04); g.lineTo(-W * .34, -L * .04); g.closePath(); g.fill();
        g.beginPath(); g.moveTo(-W * .34, L * .23); g.lineTo(W * .34, L * .23); g.lineTo(W * .38, L * .34); g.lineTo(-W * .38, L * .34); g.closePath(); g.fill();
        g.fillRect(-W * .43, -L * .03, W * .07, L * .25); g.fillRect(W * .36, -L * .03, W * .07, L * .25);
        // roof + reflection
        rr(g, -W * .33, -L * .035, W * .66, L * .255, W * .1); g.fillStyle = shade(col, .1); g.fill();
        g.fillStyle = 'rgba(255,255,255,.13)'; g.beginPath(); g.moveTo(-W * .3, -L * .19); g.lineTo(-W * .05, -L * .19); g.lineTo(-W * .2, -L * .06); g.lineTo(-W * .31, -L * .06); g.closePath(); g.fill();
        // lights
        g.fillStyle = o.lights ? '#fffbe6' : '#d9dde2';
        rr(g, -W * .42, -L * .5 + 3 * s, W * .2, 6 * s, 3 * s); g.fill(); rr(g, W * .22, -L * .5 + 3 * s, W * .2, 6 * s, 3 * s); g.fill();
        g.fillStyle = o.brake ? '#ff3b30' : '#8e1b1b';
        rr(g, -W * .42, L * .5 - 9 * s, W * .2, 6 * s, 3 * s); g.fill(); rr(g, W * .22, L * .5 - 9 * s, W * .2, 6 * s, 3 * s); g.fill();
        if (o.lights) {                              // daytime running lights: a faint local glow, not a beam
            g.globalCompositeOperation = 'lighter';
            for (const hx of [-W * .32, W * .32]) {
                const rg = g.createRadialGradient(hx, -L * .5, 0, hx, -L * .5, 26 * s);
                rg.addColorStop(0, 'rgba(255,245,210,.28)'); rg.addColorStop(1, 'rgba(255,245,210,0)');
                g.fillStyle = rg; g.beginPath(); g.arc(hx, -L * .5, 26 * s, 0, Math.PI * 2); g.fill();
            }
            g.globalCompositeOperation = 'source-over';
        }
        if (o.brake) {
            g.globalCompositeOperation = 'lighter';
            for (const hx of [-W * .32, W * .32]) {
                const rg = g.createRadialGradient(hx, L * .5, 0, hx, L * .5, 55 * s);
                rg.addColorStop(0, 'rgba(255,40,30,.45)'); rg.addColorStop(1, 'rgba(255,40,30,0)');
                g.fillStyle = rg; g.beginPath(); g.arc(hx, L * .5, 55 * s, 0, Math.PI * 2); g.fill();
            }
            g.globalCompositeOperation = 'source-over';
        }
        g.restore();
    }

    function person(g, x, y, phase, dir) {
        const s = kf(y) * 1.45, px = kx(x, y);
        g.save(); g.translate(px, y);
        g.fillStyle = 'rgba(0,0,0,.28)'; g.beginPath(); g.ellipse(9 * s, 7 * s, 16 * s, 8 * s, .5, 0, Math.PI * 2); g.fill();
        g.rotate(dir);
        const sw = Math.sin(phase) * 9 * s;
        g.fillStyle = '#23272e';
        g.beginPath(); g.ellipse(-6 * s, sw, 4.5 * s, 7 * s, 0, 0, Math.PI * 2); g.fill();
        g.beginPath(); g.ellipse(6 * s, -sw, 4.5 * s, 7 * s, 0, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#34507a'; g.beginPath(); g.ellipse(0, 0, 15 * s, 9.5 * s, 0, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#2a4163'; g.beginPath(); g.ellipse(-12 * s, -sw * .6, 4 * s, 6 * s, 0, 0, Math.PI * 2); g.fill();
        g.beginPath(); g.ellipse(12 * s, sw * .6, 4 * s, 6 * s, 0, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#c99a78'; g.beginPath(); g.arc(0, -1 * s, 6.8 * s, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#3a2a20'; g.beginPath(); g.arc(0, 1.2 * s, 6 * s, Math.PI * .05, Math.PI * .95, true); g.fill();
        g.restore();
    }

    function tree(g, x, y, r, R) {
        const s = kf(y), px = kx(x, y);
        g.fillStyle = 'rgba(0,0,0,.3)'; g.beginPath(); g.ellipse(px + 22 * s, y + 26 * s, r * 1.05 * s, r * .8 * s, 0, 0, Math.PI * 2); g.fill();
        for (let i = 0; i < 9; i++) {
            const a = R() * Math.PI * 2, d = R() * r * .45 * s, cr = (r * .5 + R() * r * .35) * s;
            const cx = px + Math.cos(a) * d, cy = y + Math.sin(a) * d * .8;
            const rg = g.createRadialGradient(cx - cr * .3, cy - cr * .35, cr * .1, cx, cy, cr);
            rg.addColorStop(0, '#7b9b5f'); rg.addColorStop(.6, '#4d6b3d'); rg.addColorStop(1, '#2e4527');
            g.fillStyle = rg; g.beginPath(); g.arc(cx, cy, cr, 0, Math.PI * 2); g.fill();
        }
    }

    function worldStatic(g, R) {
        let gr = g.createLinearGradient(0, 0, 0, WH); gr.addColorStop(0, '#565b62'); gr.addColorStop(1, '#3b3f45');
        g.fillStyle = gr; g.fillRect(0, 0, WW, WH);
        for (let i = 0; i < 9000; i++) {                                   // asphalt aggregate
            const v = R(); g.fillStyle = v > .5 ? `rgba(255,255,255,${.02 + R() * .05})` : `rgba(0,0,0,${.04 + R() * .08})`;
            g.fillRect(R() * WW, 236 + R() * (WH - 236), 1.5 + R() * 2.5, 1.5 + R() * 2);
        }
        for (let i = 0; i < 16; i++) {                                     // tyre wear / oil in the lane
            const x = R() * WW, y = 500 + R() * 120, r = 40 + R() * 90;
            const rg = g.createRadialGradient(x, y, 0, x, y, r); rg.addColorStop(0, 'rgba(18,20,24,.2)'); rg.addColorStop(1, 'rgba(18,20,24,0)');
            g.fillStyle = rg; g.beginPath(); g.ellipse(x, y, r * 1.7, r * .55, 0, 0, Math.PI * 2); g.fill();
        }
        // building facade
        gr = g.createLinearGradient(0, 0, 0, 122); gr.addColorStop(0, '#7b8088'); gr.addColorStop(1, '#8f949b');
        g.fillStyle = gr; g.fillRect(0, 0, WW, 122);
        for (let i = 0; i < 11; i++) {
            const x0 = 20 + i * 146, x1 = x0 + 124;
            if (x0 > 640 && x0 < 900) continue;
            const glass = g.createLinearGradient(kx(x0, 10), 10, kx(x1, 92), 92);
            glass.addColorStop(0, '#2c3846'); glass.addColorStop(.45, '#1b222c'); glass.addColorStop(.52, '#3a4a5c'); glass.addColorStop(.6, '#1d252f'); glass.addColorStop(1, '#232c37');
            g.fillStyle = glass;
            g.beginPath(); g.moveTo(kx(x0, 12), 12); g.lineTo(kx(x1, 12), 12); g.lineTo(kx(x1, 92), 92); g.lineTo(kx(x0, 92), 92); g.closePath(); g.fill();
            g.strokeStyle = '#5d636b'; g.lineWidth = 3; g.stroke();
            g.beginPath(); g.moveTo(kx((x0 + x1) / 2, 12), 12); g.lineTo(kx((x0 + x1) / 2, 92), 92); g.stroke();
        }
        // entrance: glass doors + canopy
        g.fillStyle = '#1a2230'; g.beginPath(); g.moveTo(kx(690, 8), 8); g.lineTo(kx(910, 8), 8); g.lineTo(kx(910, 118), 118); g.lineTo(kx(690, 118), 118); g.closePath(); g.fill();
        const door = g.createLinearGradient(0, 8, 0, 118); door.addColorStop(0, '#3d556e'); door.addColorStop(1, '#233345');
        g.fillStyle = door;
        for (const [a, b] of [[700, 795], [805, 900]]) { g.beginPath(); g.moveTo(kx(a, 14), 14); g.lineTo(kx(b, 14), 14); g.lineTo(kx(b, 116), 116); g.lineTo(kx(a, 116), 116); g.closePath(); g.fill(); }
        g.fillStyle = '#b4b9bf'; g.fillRect(kx(660, 118), 118, kx(940, 118) - kx(660, 118), 16);
        g.fillStyle = 'rgba(0,0,0,.28)'; g.fillRect(kx(660, 134), 134, kx(940, 134) - kx(660, 134), 12);
        // sidewalk + curb
        gr = g.createLinearGradient(0, 122, 0, 224); gr.addColorStop(0, '#a4a8ad'); gr.addColorStop(1, '#b3b7bb');
        g.fillStyle = gr; g.fillRect(0, 122, WW, 102);
        g.fillStyle = 'rgba(0,0,0,.18)'; g.fillRect(0, 122, WW, 6);
        g.strokeStyle = 'rgba(60,64,70,.35)'; g.lineWidth = 2;
        for (let x = 0; x <= WW; x += 110) { g.beginPath(); g.moveTo(kx(x, 128), 128); g.lineTo(kx(x, 224), 224); g.stroke(); }
        g.beginPath(); g.moveTo(0, 176); g.lineTo(WW, 176); g.stroke();
        for (const bx of [640, 700, 900, 960]) {
            const px = kx(bx, 206);
            g.fillStyle = 'rgba(0,0,0,.3)'; g.beginPath(); g.ellipse(px + 7, 212, 10, 5, .4, 0, Math.PI * 2); g.fill();
            g.fillStyle = '#f2b400'; g.beginPath(); g.arc(px, 205, 8.5, 0, Math.PI * 2); g.fill();
            g.fillStyle = '#ffd966'; g.beginPath(); g.arc(px - 2, 203, 4, 0, Math.PI * 2); g.fill();
        }
        g.fillStyle = '#d2d5d9'; g.fillRect(0, 224, WW, 9);
        g.fillStyle = '#7d8187'; g.fillRect(0, 233, WW, 5);
        g.fillStyle = 'rgba(0,0,0,.25)'; g.fillRect(0, 238, WW, 6);
        // stall lines + wheel stops
        g.strokeStyle = '#e4e6e9'; g.lineCap = 'butt';
        for (let i = 0; i <= 11; i++) {
            const x = 25 + i * 150;
            g.lineWidth = 5 * kf(350); g.beginPath(); g.moveTo(kx(x, 252), 252); g.lineTo(kx(x, 468), 468); g.stroke();
            g.lineWidth = 5 * kf(780); g.beginPath(); g.moveTo(kx(x, 664), 664); g.lineTo(kx(x, WH), WH); g.stroke();
        }
        g.fillStyle = '#9da1a6';
        for (let i = 0; i < 11; i++) { const cx = 100 + i * 150, w = 92 * kf(268); g.fillRect(kx(cx, 268) - w / 2, 262, w, 10); }
        // lane arrow
        g.fillStyle = 'rgba(232,234,237,.85)';
        g.beginPath(); const ay = 560;
        g.moveTo(kx(1010, ay), ay); g.lineTo(kx(1070, ay - 26), ay - 26); g.lineTo(kx(1070, ay - 10), ay - 10); g.lineTo(kx(1180, ay - 10), ay - 10);
        g.lineTo(kx(1180, ay + 10), ay + 10); g.lineTo(kx(1070, ay + 10), ay + 10); g.lineTo(kx(1070, ay + 26), ay + 26); g.closePath(); g.fill();
        // parked cars
        const rowA = { 0: '#e9ebee', 1: '#40454d', 3: '#a3191d', 5: '#b7bcc2', 6: '#1f3a66', 8: '#15171a', 9: '#dfe3e8', 10: '#6e747c' };
        for (const [i, c] of Object.entries(rowA)) car(g, 100 + i * 150, 365, -Math.PI / 2, c);
        const rowB = { 1: '#aeb3b9', 2: '#eceef1', 4: '#26452f', 6: '#5f656d', 7: '#2d5aa6', 9: '#1b1d21' };
        for (const [i, c] of Object.entries(rowB)) car(g, 100 + i * 150, 790, Math.PI / 2, c);
        tree(g, 1560, 510, 120, R); tree(g, 40, 600, 110, R);
    }

    // hero event choreography (u = footage seconds after EV0)
    function dynamic(g, u, fps) {
        u = Math.floor(u * fps) / fps;
        if (u >= 2) {
            let x, y, head, brake = false, lights = u < 21;
            if (u < 9) { const k = easeOut(prog(2, 9, u)); x = lerp(1790, 905, k); y = 562; head = Math.PI; }
            else if (u < 16) {
                const k = ease(prog(9, 16, u)), P = [[905, 562], [760, 562], [700, 470], [700, 392]];
                const b = (a, c) => (1 - k) ** 3 * P[0][a] + 3 * (1 - k) ** 2 * k * P[1][a] + 3 * (1 - k) * k * k * P[2][a] + k ** 3 * P[3][a];
                const d = (a) => 3 * (1 - k) ** 2 * (P[1][a] - P[0][a]) + 6 * (1 - k) * k * (P[2][a] - P[1][a]) + 3 * k * k * (P[3][a] - P[2][a]);
                x = b(0); y = b(1); head = Math.atan2(d(1), d(0)); brake = u > 13.5;
            } else { const k = easeOut(prog(16, 19, u)); x = 700; y = lerp(392, 366, k); head = -Math.PI / 2; brake = u < 19.6; }
            car(g, x, y, head, '#c7d0da', { brake, lights });
        }
        if (u >= 21 && u < 27.2) {
            let x, y, dir;
            if (u < 24) { const k = prog(21, 24, u); x = lerp(618, 626, k); y = lerp(396, 212, k); dir = 0; }
            else { const k = prog(24, 27, u); x = lerp(626, 752, k); y = lerp(212, 150, k); dir = Math.PI / 2 * .75; }
            g.save(); g.globalAlpha = clamp((27.2 - u) / .4, 0, 1) * clamp((u - 21) / .3, 0, 1);
            person(g, x, y, u * 11, dir);
            g.restore();
        }
    }

    const Feed = {
        init(W, H) {
            this.W = W; this.H = H;
            this.cw = Math.round(H * 16 / 9); this.cx = Math.round((W - this.cw) / 2);   // letterboxed 16:9 content
            const mk = (w, h) => { const c = document.createElement('canvas'); c.width = w; c.height = h; return c; };
            this.hi = mk(this.cw, H);
            { const g = this.hi.getContext('2d'); g.scale(this.cw / WW, H / WH); worldStatic(g, prng(5)); }
            this.lo = mk(640, 360); this.loStatic = mk(640, 360);
            { const g = this.loStatic.getContext('2d'); g.scale(640 / WW, 360 / WH); worldStatic(g, prng(5)); }
            this.grain = [];
            for (let n = 0; n < 4; n++) {
                const c = mk(256, 256), g = c.getContext('2d'), d = g.createImageData(256, 256), r = prng(90 + n);
                for (let i = 0; i < d.data.length; i += 4) { const v = r() * 255; d.data[i] = d.data[i + 1] = d.data[i + 2] = v; d.data[i + 3] = 20; }
                g.putImageData(d, 0, 0); this.grain.push(c);
            }
        },
        overlay(g, sx, sy, secs) {                   // the camera's own burned-in date/time
            g.save(); g.scale(sx, sy);
            g.fillStyle = 'rgba(0,0,0,.42)'; g.fillRect(18, 16, 262, 38);
            g.fillStyle = '#fff'; g.font = '600 25px "Segoe UI"'; g.textBaseline = 'middle';
            g.fillText(`2026-07-14  ${clk(secs)}`, 28, 36);
            g.restore();
        },
        draw(g, mode, secs, flash = 0) {
            g.fillStyle = '#000'; g.fillRect(0, 0, this.W, this.H);
            if (mode === 'none') return;
            const u = secs - EV0;
            if (mode === 'lo') {
                const lg = this.lo.getContext('2d');
                lg.drawImage(this.loStatic, 0, 0);
                lg.save(); lg.scale(640 / WW, 360 / WH); dynamic(lg, u, 5); lg.restore();
                this.overlay(lg, 640 / WW, 360 / WH, Math.floor(secs * 5) / 5);
                lg.drawImage(this.grain[Math.floor(secs * 5) % 4], 0, 0, 256, 256, 0, 0, 640, 360);
                g.imageSmoothingEnabled = true; g.imageSmoothingQuality = 'low';
                g.drawImage(this.lo, this.cx, 0, this.cw, this.H);
            } else {
                g.drawImage(this.hi, this.cx, 0);
                g.save(); g.translate(this.cx, 0); g.scale(this.cw / WW, this.H / WH); dynamic(g, u, 15); g.restore();
                g.save(); g.translate(this.cx, 0); this.overlay(g, this.cw / WW, this.H / WH, secs); g.restore();
                const gi = this.grain[Math.floor(secs * 15) % 4];
                for (let y = 0; y < this.H; y += 256) for (let x = this.cx; x < this.cx + this.cw; x += 256) g.drawImage(gi, x, y);
            }
            const vg = g.createRadialGradient(this.W / 2, this.H / 2, this.H * .35, this.W / 2, this.H / 2, this.cw * .62);
            vg.addColorStop(0, 'rgba(0,0,0,0)'); vg.addColorStop(1, 'rgba(0,0,0,.38)');
            g.fillStyle = vg; g.fillRect(this.cx, 0, this.cw, this.H);
            if (flash > 0) { g.fillStyle = `rgba(0,0,0,${flash})`; g.fillRect(0, 0, this.W, this.H); }
        },
    };

    // ------------------------------------------------------------------------------------------------ DOM refs
    const E = {};
    for (const id of ['stage', 'bg', 'winWrap', 'win', 'toolbar', 'cardTitle', 'cardSub', 'trust', 'trustHead', 'trustDetail',
        'exportBtn', 'camCount', 'tree', 'footSpan', 'npCam', 'npModel', 'npDate', 'npSeg', 'videoBox', 'video', 'tlWin',
        'lgExport', 'spanLbl', 'fullDay', 'zoomOut', 'zoomIn', 'ov', 'det', 'playBtn', 'playGlyph', 'clock', 'clockSub',
        'seeking', 'seekArc', 'speed', 'markIn', 'markOut', 'selLbl', 'mountLbl', 'recIdx', 'cardview', 'waiting', 'waitMsg',
        'busy', 'busyText', 'exportDlg', 'xdCam', 'xdModel', 'xdRange', 'xdDur', 'xdGo', 'boot', 'bootRing', 'bootSpin',
        'bootStage', 'bootLabel', 'bootBar', 'bootDetail', 'bootPct', 'bootFoot', 'pp', 'ppSub', 'ppDots', 'ppCaret', 'ppOk',
        'msgBox', 'msgText', 'msgOk', 'cursor', 'ripples', 'annot', 'topScrim', 'caption', 'capH', 'capS', 'capKBar', 'capKT',
        'intro', 'sdCard',
        'drive', 'fmtDlg', 'fmtBtn', 'titleCard', 'features', 'endCard', 'fade', 'controls', 'startOverlay', 'btnPlay',
        'tRead', 'track', 'trackFill']) E[id] = $(id);

    // ------------------------------------------------------------------------------------------------ tree rows
    const CARET = '<svg class="caret" viewBox="0 0 6 8"><path d="M0,0 L6,4 L0,8 Z"/></svg>';
    function camRow(name, model, badge, o = {}) {
        return `<div class="row cam${o.expanded ? ' expanded' : ''}${o.selected ? ' selected' : ''}">${CARET}
            <div class="grow"><div class="name">${name}</div><div class="model">${model}</div></div>
            ${o.lens ? `<span class="lensTag">${o.lens}</span>` : ''}<span class="badge">${badge}</span></div>`;
    }
    const dateRow = (label, clips, o = {}) =>
        `<div class="row date${o.expanded ? ' expanded' : ''}${o.selected ? ' selected' : ''}" style="margin-left:20px">${CARET}
         <div class="grow label">${label}</div><span class="clips">${clips}</span></div>`;
    const clipRow = (range, dur, o = {}) =>
        `<div class="row clip${o.selected ? ' selected' : ''}" style="margin-left:40px" data-k="${o.key ?? ''}"><i class="dot"></i>
         <div class="grow range mono">${range}</div><span class="dur">${dur}</span></div>`;

    let treeInner = null, clipEls = [];
    function buildTree(rowsHtml) {
        E.tree.innerHTML = `<div id="treeInner">${rowsHtml}</div>`;
        treeInner = $('treeInner');
        clipEls = [...E.tree.querySelectorAll('.row.clip')];
    }

    // ------------------------------------------------------------------------------------------------ canvases
    const DPR = 2;
    let det, ov, vid, DW = 1074;
    function sizeCanvases() {
        DW = E.det.clientWidth || 1074;
        E.det.width = DW * DPR; E.det.height = 83 * DPR;
        css(E.ov, 'height', '22px'); css(E.ov, 'margin', '-2px 0 7px');
        E.ov.width = DW * DPR; E.ov.height = 22 * DPR;
        det = E.det.getContext('2d'); ov = E.ov.getContext('2d'); vid = E.video.getContext('2d');
        const vw = E.videoBox.clientWidth - 2, vh = E.videoBox.clientHeight - 2;
        E.video.width = Math.round(vw * DPR); E.video.height = Math.round(vh * DPR);
        Feed.init(E.video.width, E.video.height);
    }
    function paintTimeline(st) {
        det.setTransform(DPR, 0, 0, DPR, 0, 0); drawDetail(det, DW, st);
        ov.setTransform(DPR, 0, 0, DPR, 0, 0); drawOverview(ov, DW, st);
    }

    // ------------------------------------------------------------------------------------------------ REF mode
    if (REF) {
        css(E.stage, 'background', 'transparent');
        for (const id of ['bg', 'intro', 'titleCard', 'annot', 'topScrim', 'caption', 'features', 'endCard', 'fade', 'pp', 'msgBox', 'cursor'])
            shown(E[id], false);
        css(E.win, 'boxShadow', 'none');
        text(E.cardTitle, 'axis-card-v4.img · 0 GB'); text(E.cardSub, '7 recordings · Axis edge storage');
        text(E.trustHead, 'Read-only image'); text(E.trustDetail, 'Opened from a file — the source is never modified.');
        text(E.camCount, '3 cameras'); text(E.footSpan, 'Jan 14 – Mar 2, 2025');
        buildTree(camRow('Camera 3456', 'AC:CC:8E:12:34:56', '2', { expanded: 1, selected: 1 })
            + dateRow('Tue, Jan 14, 2025', '2 clips', { expanded: 1, selected: 1 })
            + clipRow('08:30 – 08:30', '0m 06s') + clipRow('09:15 – 09:15', '0m 02s', { selected: 1 })
            + camRow('Camera 8877', 'B8:A4:4F:99:88:77', '1') + camRow('Camera FF33', 'DD:11:EE:22:FF:33', '4', { lens: '4 lenses' }));
        text(E.npCam, 'Camera 3456'); text(E.npModel, 'Lavf58.76.100'); text(E.npDate, 'Tue, Jan 14, 2025'); text(E.npSeg, '2 clips · 0m 09s footage');
        text(E.tlWin, '09:00 – 09:30 · 30 min window'); text(E.spanLbl, '30 min'); shown(E.lgExport, false);
        text(E.clock, '09:15:00'); text(E.clockSub, 'of Jan 14 · Paused'); E.playGlyph.innerHTML = '&#xE768;'; cls(E.playGlyph, 'paused', 1);
        text(E.selLbl, 'No range set'); text(E.mountLbl, 'Image · ext4 · Axis volume'); text(E.recIdx, '7 recordings indexed');
        for (const id of ['waiting', 'busy', 'exportDlg', 'boot']) shown(E[id], false);
        window.__duration = 0;
        window.__ready = true;
        window.__seek = async () => {
            sizeCanvases();
            const segs = [{ start: H(8, 30), end: H(8, 30, 6), kind: 'continuous' }, { start: H(9, 15), end: H(9, 15, 2), kind: 'continuous' }];
            paintTimeline({ center: H(9, 15), span: 1800, segs });
            vid.fillStyle = '#000'; vid.fillRect(0, 0, E.video.width, E.video.height);
            await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
        };
        return;
    }

    // ------------------------------------------------------------------------------------------------ static demo content
    const MAC0 = 'AC:CC:8E:4F:2A:19', MODEL0 = 'AXIS P3288-LVE Dome Camera';
    text(E.cardTitle, 'USB SD Card Reader · 31.9 GB');
    text(E.cardSub, `${TOTAL} recordings · Axis edge storage`);
    text(E.trustHead, 'Unlocked · Read-only');
    text(E.trustDetail, 'Decrypted in memory with your passphrase; the card is never modified.');
    text(E.camCount, '3 cameras');
    text(E.footSpan, 'Jul 12 – Jul 14, 2026');
    text(E.npCam, 'Camera 2A19'); text(E.npModel, MODEL0); text(E.npDate, 'Tue, Jul 14, 2026');
    text(E.npSeg, `${dayClips} clips · ${fmtDur(footage)} footage`);
    text(E.mountLbl, 'Locked read-only · encrypted ext4 · Axis volume');
    text(E.recIdx, `${TOTAL} recordings indexed`);
    text(E.ppSub, 'USB SD Card Reader');
    text(E.bootFoot, 'USB SD Card Reader — click anywhere to skip');
    text(E.xdCam, 'Camera 2A19'); text(E.xdModel, MODEL0);
    text(E.xdRange, `${clk(MARK_IN)} – ${clk(MARK_OUT)}`); text(E.xdDur, `${fmtDur(MARK_OUT - MARK_IN)} · Jul 14`);
    E.msgText.innerHTML = 'Exported 3 clips (214 MB) to:<br>C:\\Users\\Demo\\Videos\\Axis exports';

    const heroIdx = recs.findIndex(r => r.start === EV0);
    buildTree(camRow('Camera 2A19', MODEL0, String(dayClips + JUL12), { expanded: 1, selected: 1 })
        + dateRow('Sun, Jul 12, 2026', `${JUL12} clips`)
        + dateRow('Tue, Jul 14, 2026', `${dayClips} clips`, { expanded: 1, selected: 1 })
        + recs.map((r, i) => clipRow(`${hm(r.start)} – ${hm(r.end)}`, fmtDur(r.end - r.start), { key: i })).join('')
        + camRow('Camera 7C04', 'B8:A4:4F:31:7C:04', String(CAM2))
        + camRow('Camera FF33', 'E8:27:25:0A:FF:33', String(CAM3), { lens: '4 lenses' }));

    // features + annotations
    const FEATURES = [
        ['&#xE72E;', '#2fbf7a', 'Read-only by design', 'Locks the card, hides it from Windows'],
        ['&#xE8D7;', '#6ea8ff', 'Encrypted cards', 'LUKS1 unlock · passphrase never stored'],
        ['&#xE714;', '#6ea8ff', 'H.264 · H.265 · AV1', 'Built-in player — no codecs to buy'],
        ['&#xE81C;', '#ff6b6b', 'Color-coded timeline', 'Continuous · motion · manual'],
        ['&#xE898;', '#c792ff', 'Lossless export', 'Trimmed MP4 or MKV — every stream'],
        ['&#xE7B8;', '#f7cf4a', 'One standalone .exe', 'FFmpeg included · free & open source'],
    ];
    E.features.innerHTML = FEATURES.map(([ic, col, a, b], i) =>
        `<div class="feat" id="feat${i}" style="left:${120 + (i % 3) * 572}px;top:${372 + Math.floor(i / 3) * 186}px">
           <div class="fi mdl" style="color:${col};background:${col}22">${ic}</div><div><b>${a}</b><span>${b}</span></div></div>`).join('');
    E.annot.innerHTML = `
        <div class="callout" id="coTrust"></div>
        <div class="tag" id="tagLo">Continuous stream<small>640 × 360 · 5 fps</small></div>
        <div class="tag red" id="tagHi">Motion stream<small>3840 × 2160 · 15 fps</small></div>
        <div class="tag" id="tagFiles" style="font-size:17px;line-height:1.7"></div>`;
    const coTrust = $('coTrust'), tagLo = $('tagLo'), tagHi = $('tagHi'), tagFiles = $('tagFiles');
    tagFiles.innerHTML = [['#3b82f6', `2026-07-14_12-02-30_Camera 2A19_<b>continuous</b>.mp4`],
                          ['#ef4444', `2026-07-14_12-02-47_Camera 2A19_<b>motion</b>.mp4`],
                          ['#eab308', `2026-07-14_12-04-20_Camera 2A19_<b>manual</b>.mp4`]]
        .map(([c, f]) => `<div><span style="display:inline-block;width:11px;height:11px;border-radius:3px;background:${c};margin-right:10px"></span>${f}</div>`).join('');
    const introCursor = E.cursor.cloneNode(true); introCursor.id = 'cursor2'; E.intro.appendChild(introCursor);

    // ------------------------------------------------------------------------------------------------ layout measurement
    let R = {};                               // named rects in app (window) coordinates
    function measure() {
        const saved = E.winWrap.style.transform, savedStage = E.stage.style.transform;
        E.winWrap.style.transform = 'none'; E.stage.style.transform = 'none';     // measure in unscaled window pixels
        for (const id of ['boot', 'busy', 'exportDlg', 'waiting']) E[id].style.display = '';
        E.pp.style.display = ''; E.msgBox.style.display = '';
        // Borderless dialogs are centred on their owner (WindowStartupLocation=CenterOwner).
        for (const el of [E.pp, E.msgBox]) {
            el.style.left = `${Math.round((1440 - el.offsetWidth) / 2)}px`;
            el.style.top = `${Math.round((900 - el.offsetHeight) / 2)}px`;
        }
        { const fb = E.fmtBtn.getBoundingClientRect(), fd = E.fmtDlg.getBoundingClientRect();
          R.fmtBtnRel = [fb.left - fd.left + fb.width / 2, fb.top - fd.top + fb.height / 2]; }
        const o = E.win.getBoundingClientRect();
        const rect = el => { const b = el.getBoundingClientRect(); return { x: b.left - o.left, y: b.top - o.top, w: b.width, h: b.height }; };
        for (const id of ['trust', 'exportBtn', 'videoBox', 'det', 'ov', 'playBtn', 'speed', 'markIn', 'markOut', 'zoomOut', 'zoomIn',
            'xdGo', 'ppOk', 'msgOk', 'pp', 'msgBox', 'tree']) R[id] = rect(E[id]);
        R.bootInner = rect(E.boot.querySelector('.bootInner'));
        R.xdlg = rect(E.exportDlg.querySelector('.xdlg'));
        R.speed4 = rect(E.speed.children[3]);
        R.cardIco = rect(E.toolbar.querySelector('.cardIco'));
        R.clipRow0 = rect(clipEls[0]);
        const ti = rect(treeInner);
        R.treeInnerTop = ti.y; R.treeContentH = ti.h;
        R.rowBoxes = [...treeInner.children].map(el => { const b = rect(el); return { el, top: b.y - ti.y, h: b.h }; });
        E.winWrap.style.transform = saved; E.stage.style.transform = savedStage;
    }
    const center = r => [r.x + r.w / 2, r.y + r.h / 2];
    const grow = (r, a, b = a) => ({ x: r.x - a, y: r.y - b, w: r.w + 2 * a, h: r.h + 2 * b });
    // How far the sidebar list can scroll: content height minus the list's visible height (its box less 7px padding x2).
    function treeMaxScroll() { return Math.max(0, R.treeContentH - (R.tree.h - 14)); }

    // ------------------------------------------------------------------------------------------------ virtual camera
    const REGION = { x: 80, y: 266, w: 1760, h: 784 };
    function fit(r, s = null) {
        const sc = s ?? Math.min(REGION.w / r.w, REGION.h / r.h, 2.2);
        return { s: sc, fx: r.x + r.w / 2, fy: r.y + r.h / 2 };
    }
    let SHOTS, MOVES;
    function defineShots() {
        SHOTS = {
            FULL: fit({ x: 0, y: 0, w: 1440, h: 900 }),
            BOOT: fit(grow(R.bootInner, 90, 60)),
            PP: fit(grow(R.pp, 60, 40)),
            TRUST: fit({ x: 0, y: 30, w: 740, h: 250 }),
            SB_TOP: fit({ x: 0, y: 96, w: 820, h: 500 }),
            SB_BOT: fit({ x: 0, y: 392, w: 820, h: 500 }),
            SB_MID: fit({ x: 0, y: 250, w: 820, h: 500 }),
            TL_VIDEO: fit({ x: 322, y: 110, w: 1118, h: 770 }),
            VIDEO: fit(grow(R.videoBox, 18, 14)),
            TIMELINE: fit({ x: 322, y: 628, w: 1118, h: 250 }),
            DIALOG: fit(grow(R.xdlg, 60, 50)),
            BACKDROP: fit({ x: 0, y: 0, w: 1440, h: 900 }, .78),
        };
        MOVES = [[11.5, 0, 'BOOT'], [12.95, .4, 'PP'], [15.05, .35, 'BOOT'], [18.75, .75, 'FULL'], [20.0, .8, 'TRUST'],
            [23.6, .7, 'SB_TOP'], [24.7, 1.25, 'SB_BOT'], [26.35, .85, 'SB_MID'], [27.2, .7, 'FULL'], [28.9, .75, 'TL_VIDEO'], [31.65, .45, 'VIDEO'], [35.6, .8, 'TL_VIDEO'],
            [39.8, .5, 'TIMELINE'], [42.85, .45, 'FULL'], [43.45, .45, 'DIALOG'], [47.75, .55, 'TIMELINE'], [51.6, .9, 'BACKDROP']];
    }
    function camAt(t) {
        let cur = SHOTS[MOVES[0][2]];
        for (let i = 1; i < MOVES.length; i++) {
            const [ts, d, name] = MOVES[i];
            if (t < ts) break;
            const next = SHOTS[name], k = ease(prog(ts, ts + d, t));
            cur = { s: Math.exp(lerp(Math.log(cur.s), Math.log(next.s), k)), fx: lerp(cur.fx, next.fx, k), fy: lerp(cur.fy, next.fy, k) };
        }
        return { s: cur.s, x: REGION.x + REGION.w / 2 - cur.fx * cur.s, y: REGION.y + REGION.h / 2 - cur.fy * cur.s };
    }
    const toStage = (cam, r) => ({ x: cam.x + r.x * cam.s, y: cam.y + r.y * cam.s, w: r.w * cam.s, h: r.h * cam.s });

    // ------------------------------------------------------------------------------------------------ timing
    const T_PLAY = 29.8, T_DROP = 32.0, RATE = 4, PLAY_FROM = EV0 - (T_DROP - T_PLAY) * RATE;
    const T_PAUSE = 40.05, PAUSED_AT = PLAY_FROM + (T_PAUSE - T_PLAY) * RATE;

    function player(t) {       // playhead (footage seconds), zoom span, transport state
        let c = recs[recs.length - 1].start, span = 1800, playing = false, rate = 1, selIn = null, selOut = null;
        if (t >= 28.0) c = PLAY_FROM;
        if (t >= 28.45) span = 900;
        if (t >= 28.7) span = 300;
        if (t >= 29.15) rate = 4;
        if (t >= T_PLAY) { playing = t < T_PAUSE; c = PLAY_FROM + (Math.min(t, T_PAUSE) - T_PLAY) * RATE; }
        if (t >= 40.35) c = lerp(PAUSED_AT, MARK_IN, ease(prog(40.35, 41.0, t)));
        if (t >= 41.2) selIn = MARK_IN;
        if (t >= 41.55) span = 900;
        if (t >= 41.8) c = lerp(MARK_IN, MARK_OUT, ease(prog(41.8, 42.5, t)));
        if (t >= 42.7) selOut = MARK_OUT;
        for (const [ts, s] of [[48.3, 1800], [48.55, 3600], [48.8, 10800], [49.05, 21600], [49.3, 86400]]) if (t >= ts) span = s;
        return { c, span, playing, rate, selIn, selOut };
    }

    // cursor (app coords) + clicks
    let CURSOR, CLICKS;
    function defineCursor() {
        const ovX = s => R.ov.x + (s / 86400) * R.ov.w, detY = R.det.y + 40;
        const P = (t, xy) => [t, xy[0], xy[1]];
        const last = R.rowBoxes[R.rowBoxes.length - 1];
        const lastY = R.treeInnerTop + last.top + last.h / 2 - treeMaxScroll();      // "Camera FF33" once flung to the bottom
        CURSOR = [
            [13.7, null], P(13.75, [920, 640]), P(14.75, center(R.ppOk)), P(15.1, [center(R.ppOk)[0] + 6, center(R.ppOk)[1] + 4]), [15.15, null],
            [24.1, null], P(24.15, [236, 250]), P(24.6, [238, 300]), P(25.95, [205, lastY - 36]), P(26.3, [210, lastY]), P(27.15, [240, 500]),
            P(27.9, [ovX(PLAY_FROM) + 3, R.ov.y + 9]), P(28.35, [R.det.x + DW * .62, detY]), P(28.95, center(R.speed4)),
            P(29.6, center(R.playBtn)), P(30.1, [center(R.playBtn)[0] + 60, center(R.playBtn)[1] + 90]), [30.15, null],
            [39.75, null], P(39.8, [center(R.playBtn)[0] + 50, center(R.playBtn)[1] + 40]), P(40.05, center(R.playBtn)),
            P(40.3, [R.det.x + DW * .4, detY]), P(41.0, [R.det.x + DW * .4 + (PAUSED_AT - MARK_IN) / 300 * DW, detY]),
            P(41.2, center(R.markIn)), P(41.45, [R.det.x + DW * .7, detY]), P(41.8, [R.det.x + DW * .7, detY]),
            P(42.5, [R.det.x + DW * .7 - (MARK_OUT - MARK_IN) / 900 * DW, detY]), P(42.7, center(R.markOut)),
            P(43.35, center(R.exportBtn)), P(44.45, center(R.xdGo)), P(46.9, [center(R.msgOk)[0] + 80, center(R.msgOk)[1] + 60]),
            P(47.55, center(R.msgOk)), P(48.0, [R.det.x + DW * .5 + 40, detY]), P(49.5, [R.det.x + DW * .5 + 46, detY + 4]), [49.9, null],
        ];
        CLICKS = [[14.85, ...center(R.ppOk)], [28.0, ovX(PLAY_FROM) + 3, R.ov.y + 9], [29.1, ...center(R.speed4)],
            [29.75, ...center(R.playBtn)], [40.05, ...center(R.playBtn)], [41.2, ...center(R.markIn)], [42.7, ...center(R.markOut)],
            [43.4, ...center(R.exportBtn)], [44.5, ...center(R.xdGo)], [47.6, ...center(R.msgOk)]];
    }
    function cursorAt(t) {
        let prev = null;
        for (const k of CURSOR) {
            if (k[0] > t) {
                if (!prev || prev[1] === null || k[1] === null) return prev && prev[1] !== null ? { x: prev[1], y: prev[2] } : null;
                const e = ease(prog(prev[0], k[0], t));
                return { x: lerp(prev[1], k[1], e), y: lerp(prev[2], k[2], e) };
            }
            prev = k;
        }
        return null;
    }
    const DRAGS = [[40.35, 41.0], [41.8, 42.5]];

    // ------------------------------------------------------------------------------------------------ captions
    // k = kicker, acc = accent (plain <em> takes it; a classed <em> keeps its own colour). Consecutive captions that
    // share a kicker form one chapter: the kicker (and, in style C, the chapter number) holds steady across them.
    const ACC = { blu: '#6ea8ff', grn: '#5fe0a1', pur: '#c792ff', red: '#ff6b6b' };
    const CAPTIONS = [
        { a: 0.4, b: 2.05, k: 'The problem', acc: 'red', h: 'Axis cameras record to a <em class="blu">microSD card</em>.' },
        { a: 2.2, b: 4.05, k: 'The problem', acc: 'red', h: 'But it\'s <em>ext4</em> — Windows can\'t read it.' },
        { a: 4.2, b: 6.0, k: 'The problem', acc: 'red', h: '…so Windows offers to <em>format</em> it.' },
        { a: 6.1, b: 7.85, k: 'The problem', acc: 'red', h: 'One wrong click and the footage is <em>gone</em>.' },
        { a: 12.35, b: 15.8, k: 'Unlock', acc: 'blu', h: 'Encrypted card? <em>Unlock</em> it with the camera\'s passphrase.',
          s: 'Decrypted in memory — the passphrase is never stored.' },
        { a: 16.0, b: 19.8, k: 'Read-only', acc: 'grn', h: 'Locked <em>read-only</em> from the very first byte.',
          s: 'No drive letter. No format prompt. Nothing is ever written.' },
        { a: 20.0, b: 23.8, k: 'Read-only', acc: 'grn', h: 'It shows you the <em>protection</em> — not just a promise.',
          s: 'The badge reflects whether every volume really locked.' },
        { a: 24.0, b: 27.4, k: 'Browse', acc: 'blu', h: 'Every camera, lens and day — <em>indexed in seconds</em>.',
          s: `${TOTAL} recordings across 3 cameras.` },
        { a: 27.6, b: 31.85, k: 'Timeline', acc: 'blu',
          h: '<em class="blu">Continuous</em>, <em class="red">motion</em> &amp; <em class="yel">manual</em> — color-coded like Axis.',
          s: 'Right on the timeline.' },
        { a: 32.2, b: 35.8, k: 'Playback', acc: 'blu', h: 'Playback <em>follows the action</em>.',
          s: 'A motion event? It switches to the 4K stream automatically.' },
        { a: 36.0, b: 39.8, k: 'Playback', acc: 'blu', h: 'Plays <em>H.264, H.265 &amp; AV1</em> — no codecs to buy.',
          s: 'Scrub, step clip to clip, 0.5× – 8×.' },
        { a: 40.0, b: 43.8, k: 'Export', acc: 'pur', h: 'Mark <em>in</em>… mark <em>out</em>.', s: 'Any range — even across recordings.' },
        { a: 44.0, b: 47.8, k: 'Export', acc: 'pur', h: 'Export <em>lossless MP4 or MKV</em>.',
          s: 'Overlapping streams? You get every one, clearly labeled.' },
        { a: 48.0, b: 51.8, k: 'Overview', acc: 'blu', h: 'From <em>5 minutes</em> to the <em>whole day</em>.',
          s: 'Every event on the card, at a glance.' },
        { a: 52.3, b: 59.7, k: 'Axis SD Card Reader', acc: 'blu', h: 'Read-only. Offline. <em>Open source.</em>' },
    ];
    const CHAPTERS = [];
    for (const c of CAPTIONS) {
        const last = CHAPTERS[CHAPTERS.length - 1];
        if (last && last.k === c.k && c.a - last.b < .5) last.b = c.b;
        else CHAPTERS.push({ k: c.k, a: c.a, b: c.b });
        c.ch = CHAPTERS[CHAPTERS.length - 1];
        c.hw = splitWords(c.h);
    }

    // Wraps every word of a caption (inside or outside <em>) in a mask span so it can rise in on its own.
    function splitWords(src) {
        const tpl = document.createElement('template');
        tpl.innerHTML = src;
        const walk = node => {
            for (const n of [...node.childNodes]) {
                if (n.nodeType !== Node.TEXT_NODE) { walk(n); continue; }
                const frag = document.createDocumentFragment();
                for (const part of n.textContent.split(/(\s+)/)) {
                    if (!part) continue;
                    if (/^\s+$/.test(part)) { frag.append(part); continue; }
                    const w = document.createElement('span'), wi = document.createElement('span');
                    w.className = 'w'; wi.className = 'wi'; wi.textContent = part; w.append(wi); frag.append(w);
                }
                n.replaceWith(frag);
            }
        };
        walk(tpl.content);
        return tpl.innerHTML;
    }

    // The kicker's rule draws in and its label slides after it (once per chapter); the headline's words rise out of
    // their masks one by one and the sub follows; on the way out the words slip back up into their masks.
    const easeIn = k => k * k * k;
    let capShown = -1, capWords = [];
    function animateCaption(c, t) {
        const ch = c.ch, kIn = easeOut(prog(ch.a, ch.a + .5, t)), kOut = prog(ch.b - .3, ch.b, t);
        css(E.capKBar, 'transform', `scaleX(${(kIn * (1 - kOut)).toFixed(3)})`);
        opa(E.capKT, prog(ch.a + .1, ch.a + .4, t) * (1 - kOut));
        css(E.capKT, 'transform', `translateX(${((1 - kIn) * -12).toFixed(1)}px)`);
        capWords.forEach((w, j) => {
            const s = c.a + .06 + j * .045, e = c.b - .32 + j * .012;
            const y = (1 - easeOut(prog(s, s + .55, t))) * 112 - easeIn(prog(e, e + .24, t)) * 112;
            css(w, 'transform', `translateY(${y.toFixed(1)}%)`);
        });
        const sk = easeOut(prog(c.a + .3, c.a + .8, t)), out = prog(c.b - .28, c.b, t);
        opa(E.capS, sk * (1 - out)); css(E.capS, 'transform', `translateY(${((1 - sk) * 10).toFixed(1)}px)`);
    }

    // ------------------------------------------------------------------------------------------------ background
    const bgG = E.bg.getContext('2d');
    const noiseTile = (() => {
        const c = document.createElement('canvas'); c.width = c.height = 256;
        const g = c.getContext('2d'), d = g.createImageData(256, 256), r = prng(3);
        for (let i = 0; i < d.data.length; i += 4) { const v = r() * 255; d.data[i] = d.data[i + 1] = d.data[i + 2] = v; d.data[i + 3] = 9; }
        g.putImageData(d, 0, 0); return c;
    })();
    function drawBg(t) {
        const g = bgG, kick = Music.kickEnergy(t);
        let gr = g.createLinearGradient(0, 0, 0, 1080); gr.addColorStop(0, '#04070d'); gr.addColorStop(1, '#081020');
        g.fillStyle = gr; g.fillRect(0, 0, 1920, 1080);
        const glow = (x, y, r, rgb, a) => { const rg = g.createRadialGradient(x, y, 0, x, y, r); rg.addColorStop(0, `rgba(${rgb},${a})`); rg.addColorStop(1, `rgba(${rgb},0)`); g.fillStyle = rg; g.fillRect(0, 0, 1920, 1080); };
        const burst = Math.max(env(t, 8, 10, .02, 1.6), env(t, 32, 34, .02, 1.8), env(t, 60, 63, .02, 2.5)) * .12;
        glow(960 + Math.sin(t * .13) * 320, 1120, 1100, '44,110,255', .2 + kick * .07 + burst);
        glow(260 + Math.sin(t * .09) * 90, 120, 760, '130,70,255', .09 + burst * .5);
        glow(1720, 300 + Math.sin(t * .11) * 80, 620, '0,190,255', .05);
        g.strokeStyle = `rgba(110,160,255,${.045 + kick * .035})`; g.lineWidth = 1;
        const vx = 960, vy = 620;
        for (let i = -12; i <= 12; i++) { g.beginPath(); g.moveTo(vx + i * 16, vy); g.lineTo(vx + i * 260, 1080); g.stroke(); }
        const off = (t * .35) % 1;
        for (let i = 0; i < 10; i++) { const z = (i + off) / 10, y = vy + (1080 - vy) * z * z; g.globalAlpha = z; g.beginPath(); g.moveTo(0, y); g.lineTo(1920, y); g.stroke(); }
        g.globalAlpha = 1;
        g.fillStyle = g.createPattern(noiseTile, 'repeat'); g.save(); g.translate((t * 97) % 256, (t * 61) % 256); g.fillRect(-256, -256, 1920 + 512, 1080 + 512); g.restore();
    }

    // ------------------------------------------------------------------------------------------------ the frame
    let lastTreeScroll = null;
    function frame(t) {
        drawBg(t);

        // ---- intro (problem) 0–8
        const introOn = t < 8.2;
        shown(E.intro, introOn);
        if (introOn) {
            const fadeAll = 1 - prog(7.25, 7.95, t);
            const inK = back(prog(0.05, 0.75, t)), move = ease(prog(2.0, 2.7, t));
            const sx = lerp(810, 380, move), sy = 360 + Math.sin(t * 2.1) * 8 * (1 - move * .5);
            css(E.sdCard, 'transform', `translate(${sx}px,${sy}px) scale(${lerp(.72, 1, inK)}) rotate(${lerp(-10, -4, inK) + move * 4}deg)`);
            opa(E.sdCard, prog(0.05, 0.4, t) * fadeAll);
            const sh = prog(1.1, 1.9, t);
            $('sdShine').setAttribute('opacity', String(sh > 0 && sh < 1 ? 1 : 0));
            $('lgShine').setAttribute('x1', String(lerp(-1, 1, sh))); $('lgShine').setAttribute('x2', String(lerp(0, 2, sh)));
            const dIn = ease(prog(2.2, 2.75, t)), dOut = prog(4.0, 4.3, t);
            css(E.drive, 'transform', `translate(${lerp(1260, 1120, dIn)}px,${486}px)`); opa(E.drive, dIn * (1 - dOut));
            // The real dialog is tiny at 1080p, so it's shown at 1.32x (like a zoomed screen recording).
            const DS = 1.32, DX = 985, DY = 318, fIn = easeOut(prog(4.0, 4.35, t));
            css(E.fmtDlg, 'transformOrigin', '0 0');
            css(E.fmtDlg, 'transform', `translate(${DX}px,${DY + (1 - fIn) * 22}px) scale(${DS})`);
            opa(E.fmtDlg, prog(4.0, 4.2, t) * fadeAll);
            cls(E.fmtBtn, 'hot', t > 5.35);
            css(E.fmtBtn, 'boxShadow', t > 5.35 ? `0 0 0 ${3 + Math.sin(t * 12) * 2}px rgba(196,43,28,.35)` : 'none');
            // intro cursor (stage coords)
            const tgt = [DX + R.fmtBtnRel[0] * DS, DY + R.fmtBtnRel[1] * DS];
            const ck = ease(prog(4.45, 5.3, t));
            const cx = lerp(1790, tgt[0] - 12, ck) + (t > 5.35 ? Math.sin(t * 3) * 3 : 0), cy = lerp(1070, tgt[1] - 2, ck);
            css(introCursor, 'transform', `translate(${cx}px,${cy}px) scale(1.5)`); opa(introCursor, prog(4.4, 4.55, t) * fadeAll);
        }

        // ---- title card 8–12
        const titleOn = t >= 7.9 && t < 12.1;
        shown(E.titleCard, titleOn);
        if (titleOn) {
            const up = ease(prog(11.1, 11.9, t));
            opa(E.titleCard, prog(7.95, 8.2, t) * (1 - up));
            css(E.titleCard, 'transform', `translateY(${-up * 120}px)`);
            const logo = E.titleCard.querySelector('.logoBig'), h1 = E.titleCard.querySelector('h1'), p = E.titleCard.querySelector('p');
            const lk = back(prog(8.0, 8.55, t));
            css(logo, 'transform', `scale(${lerp(.55, 1, lk)}) rotate(${lerp(-14, 0, lk)}deg)`); opa(logo, prog(8.0, 8.2, t));
            css(h1, 'transform', `translateY(${(1 - easeOut(prog(8.15, 8.75, t))) * 34}px)`); opa(h1, prog(8.15, 8.55, t));
            css(p, 'transform', `translateY(${(1 - easeOut(prog(8.4, 9.0, t))) * 24}px)`); opa(p, prog(8.4, 8.85, t));
        }

        // ---- app window 11.5–60
        const appOn = t >= 11.45 && t < 60.2;
        shown(E.winWrap, appOn);
        const cam = camAt(t);
        if (appOn) {
            const inK = easeOut(prog(11.5, 12.25, t)), outK = prog(59.4, 60.1, t);
            css(E.winWrap, 'transform', `translate(${cam.x}px,${cam.y + (1 - inK) * 70}px) scale(${cam.s})`);
            opa(E.winWrap, inK * (1 - outK));
            css(E.winWrap, 'filter', t > 51.6 ? `brightness(${lerp(1, .38, ease(prog(51.6, 52.6, t)))}) blur(${lerp(0, 3, ease(prog(51.6, 52.6, t)))}px)` : 'none');
            appFrame(t);
        }

        // ---- annotations (stage coords, tracking the camera)
        {
            const trustS = toStage(cam, grow(R.trust, 6, 5));
            const on = appOn ? env(t, 20.8, 23.7, .3, .3) : 0;
            css(coTrust, 'left', trustS.x + 'px'); css(coTrust, 'top', trustS.y + 'px'); css(coTrust, 'width', trustS.w + 'px'); css(coTrust, 'height', trustS.h + 'px');
            css(coTrust, 'borderColor', '#5fe0a1'); css(coTrust, 'boxShadow', `0 0 30px rgba(60,220,140,${.35 + .2 * Math.sin(t * 6)}), inset 0 0 18px rgba(60,220,140,.2)`);
            opa(coTrust, on);
            const v = toStage(cam, R.videoBox);
            css(tagLo, 'left', (v.x + 26) + 'px'); css(tagLo, 'top', (v.y + v.h - 96) + 'px');
            opa(tagLo, appOn ? env(t, 30.0, 32.0, .3, .12) : 0);
            css(tagHi, 'left', (v.x + 26) + 'px'); css(tagHi, 'top', (v.y + v.h - 96) + 'px');
            opa(tagHi, appOn ? env(t, 32.12, 39.6, .25, .3) : 0);
            css(tagHi, 'transform', `scale(${lerp(.85, 1, back(prog(32.12, 32.5, t)))})`); css(tagHi, 'transformOrigin', '0 100%');
            const d = toStage(cam, R.xdlg);
            css(tagFiles, 'left', (960 - 360) + 'px'); css(tagFiles, 'top', Math.min(900, d.y + d.h + 24) + 'px');
            opa(tagFiles, appOn ? env(t, 46.55, 47.7, .25, .2) : 0);
        }

        // ---- captions
        {
            const i = CAPTIONS.findIndex(c => t >= c.a && t < c.b), c = CAPTIONS[i];
            if (c) {
                if (i !== capShown) {
                    capShown = i;
                    html(E.capH, c.hw); html(E.capS, c.s ?? ''); shown(E.capS, !!c.s); text(E.capKT, c.k);
                    E.caption.style.setProperty('--acc', ACC[c.acc]);
                    capWords = [...E.capH.querySelectorAll('.wi')];
                }
                opa(E.caption, 1);
                animateCaption(c, t);
            } else opa(E.caption, 0);
            const scrim = appOn && cam.y < 250 ? prog(250, 170, cam.y) : 0;
            opa(E.topScrim, t < 51.6 ? scrim : 0);
        }

        // ---- features 52–60
        const featOn = t >= 52 && t < 60.2;
        shown(E.features, featOn);
        if (featOn) {
            const out = prog(59.4, 60.0, t);
            FEATURES.forEach((_, i) => {
                const el = $('feat' + i), t0 = 52.4 + i * .5, k = back(prog(t0, t0 + .45, t));
                opa(el, prog(t0, t0 + .25, t) * (1 - out));
                css(el, 'transform', `translateY(${(1 - k) * 40}px) scale(${lerp(.94, 1, k)})`);
            });
        }

        // ---- end card 60–66
        const endOn = t >= 59.9;
        shown(E.endCard, endOn);
        if (endOn) {
            opa(E.endCard, prog(60.0, 60.35, t));
            const logo = E.endCard.querySelector('.logoBig'), k = back(prog(60.0, 60.6, t));
            css(logo, 'transform', `scale(${lerp(.6, 1, k)})`);
            const h1 = E.endCard.querySelector('h1'); css(h1, 'transform', `translateY(${(1 - easeOut(prog(60.15, 60.75, t))) * 30}px)`); opa(h1, prog(60.15, 60.5, t));
            const url = E.endCard.querySelector('.url'); opa(url, prog(60.55, 60.9, t)); css(url, 'transform', `translateY(${(1 - easeOut(prog(60.55, 61.1, t))) * 20}px)`);
            opa(E.endCard.querySelector('.meta'), prog(60.9, 61.3, t));
            opa(E.endCard.querySelector('.fine'), prog(61.3, 61.8, t));
        }

        opa(E.fade, Math.max(1 - prog(0, .6, t), prog(65.1, 65.95, t)));
    }

    function appFrame(t) {
        const P = player(t);
        const boot1 = t < 12.95, pp = t >= 12.95 && t < 15.15, boot2 = t >= 15.15 && t < 18.85, cardOpen = t >= 18.85;

        // views
        cls(E.toolbar, 'hidden', !cardOpen);
        shown(E.cardview, cardOpen);
        shown(E.waiting, !cardOpen);
        text(E.waitMsg, 'Scanning for Axis camera cards…');
        shown(E.boot, boot1 || boot2);
        if (boot1 || boot2) {
            let label, frac, detail, done = false;
            if (boot1) { label = 'Detecting SD card…'; frac = .15; detail = 'USB SD Card Reader · encrypted'; }
            else if (t < 15.8) { label = 'Verifying read-only mount…'; frac = .25; detail = 'locking volume'; }
            else if (t < 17.6) { const k = prog(15.8, 17.5, t); label = 'Indexing recordings…'; frac = lerp(.45, .9, k); detail = `${Math.round(TOTAL * easeOut(k))} recordings found`; }
            else if (t < 18.2) { label = 'Preparing player…'; frac = .95; detail = 'starting video engine'; }
            else { label = 'Ready to review'; frac = 1; detail = `${TOTAL} recordings`; done = true; }
            text(E.bootLabel, label); text(E.bootDetail, detail); text(E.bootPct, `${Math.round(frac * 100)}%`);
            css(E.bootBar, 'width', `calc(${frac * 100}% + 2px)`);
            cls(E.bootStage, 'isDone', done);
            css(E.bootSpin, 'transform', `rotate(${(t / .8) * 360}deg)`);
            const pk = ((t - 11.5) % 2.2) / 2.2;
            css(E.bootRing, 'transform', `scale(${lerp(1, 1.35, pk)})`); opa(E.bootRing, lerp(.9, 0, pk));
            shown(E.bootFoot, boot2);
        }

        // passphrase dialog
        shown(E.pp, pp);
        if (pp) {
            opa(E.pp, prog(12.95, 13.1, t) * (1 - prog(15.0, 15.15, t)));
            const n = Math.floor(clamp((t - 13.35) / .085, 0, 14));
            text(E.ppDots, '•'.repeat(n));
            opa(E.ppCaret, Math.floor(t * 2.2) % 2 === 0 || (t > 13.35 && t < 14.6) ? 1 : 0);
            cls(E.ppOk, 'press', t > 14.83 && t < 15.0);
        }

        // player-driven labels
        const active = bestAt(P.c);
        const seeking = t >= 32.0 && t < 32.4;
        text(E.clock, clk(P.c));
        text(E.clockSub, `of Jul 14 · ${P.playing ? `Playing ${P.rate}×` : active ? 'Paused' : 'No footage'}`);
        E.playGlyph.innerHTML = P.playing ? '&#xE769;' : '&#xE768;';
        cls(E.playGlyph, 'paused', !P.playing);
        cls(E.seeking, 'on', seeking);
        if (seeking) E.seekArc.setAttribute('transform', `rotate(${(t / .85) * 360} 8 8)`);
        const full = P.span >= 86400;
        text(E.tlWin, full ? 'Full-day view · 00:00 – 24:00' : `${hm(P.c - P.span / 2)} – ${hm(P.c + P.span / 2)} · ${spanLabel(P.span)} window`);
        text(E.spanLbl, spanLabel(P.span));
        cls(E.fullDay, 'on', full);
        const hasSel = P.selIn != null && P.selOut != null;
        shown($('lgExport'), hasSel);
        text(E.selLbl, hasSel ? `${fmtDur(P.selOut - P.selIn)} selected` : P.selIn != null ? `In ${hm(P.selIn)} · set out` : 'No range set');
        cls(E.exportBtn, 'disabled', !hasSel);
        cls(E.exportBtn, 'hover', hasSel && t > 43.2 && t < 43.5);
        [...E.speed.children].forEach((el, i) => cls(el, 'on', [.5, 1, 2, 4, 8][i] === P.rate));
        cls(E.speed.children[3], 'hover', t > 28.95 && t < 29.35);
        cls(E.playBtn, 'hover', (t > 29.55 && t < 29.95) || (t > 39.95 && t < 40.25));
        cls(E.markIn, 'hover', t > 41.05 && t < 41.4);
        cls(E.markOut, 'hover', t > 42.55 && t < 42.9);
        cls(E.xdGo, 'hover', t > 44.3 && t < 44.65);

        // tree: selection follows the active recording. The browse beat flings the list to the bottom (the other
        // cameras, incl. the 4-lens one) and back up to the motion event; hover tracks the row under the cursor.
        const ai = recs.indexOf(active);
        clipEls.forEach((el, i) => cls(el, 'selected', i === ai));
        const maxScroll = treeMaxScroll();
        const heroScroll = Math.min(maxScroll, Math.max(0, heroIdx - 6) * (R.clipRow0.h + 2));
        let scroll = 0;
        if (t >= 24.6) scroll = maxScroll * ease(prog(24.6, 25.95, t));
        if (t >= 26.35) scroll = lerp(maxScroll, heroScroll, ease(prog(26.35, 27.2, t)));
        if (scroll !== lastTreeScroll) { css(treeInner, 'transform', `translateY(${-scroll}px)`); lastTreeScroll = scroll; }
        const cur = t > 24.15 && t < 27.3 ? cursorAt(t) : null;
        const yIn = cur ? cur.y - R.treeInnerTop + scroll : -1;
        for (const rb of R.rowBoxes) cls(rb.el, 'hover', !!cur && yIn >= rb.top && yIn < rb.top + rb.h && !rb.el.classList.contains('selected'));

        // export dialog, busy overlay, message box
        const dlg = t >= 43.45 && t < 44.62;
        shown(E.exportDlg, dlg);
        if (dlg) { const k = prog(43.45, 43.62, t); opa(E.exportDlg, k); css(E.exportDlg.firstElementChild, 'transform', `scale(${lerp(.97, 1, easeOut(k))})`); }
        const busy = t >= 44.62 && t < 46.55;
        shown(E.busy, busy);
        if (busy) {
            const k = prog(44.62, 46.5, t), clip = Math.min(3, Math.floor(k * 3) + 1), f = (k * 3) % 1;
            const phase = f < .18 ? 'Copying source…' : 'Writing…';
            const pct = Math.round((f < .18 ? .05 + .15 * (f / .18) : .2 + .78 * ((f - .18) / .82)) * 100);
            text(E.busyText, `Exporting clip ${clip}/3 · ${phase} ${pct}%`);
        }
        const msg = t >= 46.55 && t < 47.75;
        shown(E.msgBox, msg);
        if (msg) opa(E.msgBox, prog(46.55, 46.7, t));

        // timeline + video
        paintTimeline({ center: P.c, span: P.span, segs: recs, selIn: P.selIn, selOut: P.selOut });
        // continuous = the low-profile stream; motion/manual = the high-profile one. A stream switch shows a
        // couple of black frames while the new recording opens (the seek spinner covers the same moment).
        const mode = !active ? 'none' : active.kind === 'continuous' ? 'lo' : 'hi';
        Feed.draw(vid, t < 19.4 ? 'none' : mode, P.c, t >= 32 && t < 32.14 ? 1 - prog(32.07, 32.14, t) : 0);

        // cursor + ripples
        const c = cursorAt(t);
        shown(E.cursor, !!c);
        if (c) {
            const drag = DRAGS.some(([a, b]) => t >= a && t <= b);
            css(E.cursor, 'transform', `translate(${c.x - 1.5}px,${c.y - 1.5}px) scale(${drag ? .92 : 1})`);
        }
        E.ripples.innerHTML = CLICKS.filter(([tc]) => t >= tc && t < tc + .5).map(([tc, x, y]) => {
            const k = prog(tc, tc + .5, t), r = 8 + k * 26;
            return `<div class="ripple" style="left:${x}px;top:${y}px;width:${r * 2}px;height:${r * 2}px;opacity:${(1 - k) * .9}"></div>`;
        }).join('');
    }
    const spanLabel = s => s <= 300 ? '5 min' : s <= 900 ? '15 min' : s <= 1800 ? '30 min' : s <= 3600 ? '1 hour' : s <= 10800 ? '3 hours' : s <= 21600 ? '6 hours' : 'Full day';

    // ------------------------------------------------------------------------------------------------ boot
    let ready = false;
    async function init() {
        await document.fonts.ready;
        E.win.style.transform = 'none';
        sizeCanvases();
        measure();
        defineShots();
        defineCursor();
        ready = true;
    }
    const initP = init();

    window.__duration = DURATION;
    window.__ready = initP.then(() => true);
    window.__seek = async t => {
        await initP;
        frame(t);
        await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
    };
    window.__renderAudio = () => Music.renderWavBase64();

    // ------------------------------------------------------------------------------------------------ live preview
    if (!RENDER) {
        const fitStage = () => {
            const s = Math.min(innerWidth / 1920, innerHeight / 1080);
            E.stage.style.transform = `translate(${(innerWidth - 1920 * s) / 2}px,${(innerHeight - 1080 * s) / 2}px) scale(${s})`;
        };
        addEventListener('resize', fitStage); fitStage();
        let actx = null, t0 = 0, offset = 0, playing = false, idle = 0;
        const now = () => playing ? offset + (actx.currentTime - t0) : offset;
        function start(from) {
            if (actx) actx.close();
            actx = new AudioContext();
            const out = actx.createGain(); out.connect(actx.destination);
            Music.build(actx, out, actx.currentTime + .08, from);
            t0 = actx.currentTime + .08; offset = from; playing = true; E.btnPlay.textContent = 'Pause';
        }
        function stop() { offset = now(); playing = false; if (actx) { actx.close(); actx = null; } E.btnPlay.textContent = 'Play'; }
        E.startOverlay.onclick = () => { E.startOverlay.style.display = 'none'; start(0); };
        E.btnPlay.onclick = () => playing ? stop() : start(offset >= DURATION - .05 ? 0 : offset);
        E.track.onclick = ev => { const b = E.track.getBoundingClientRect(); const s = (ev.clientX - b.left) / b.width * DURATION; playing ? start(s) : (offset = s); };
        addEventListener('keydown', e => { if (e.code === 'Space') { e.preventDefault(); E.btnPlay.click(); } });
        addEventListener('mousemove', () => { idle = performance.now(); });
        let lastDrawn = -1;
        const loop = () => {
            if (ready) {
                let t = now();
                if (t >= DURATION) { t = DURATION; if (playing) stop(); offset = DURATION; }
                if (t !== lastDrawn) { frame(t); lastDrawn = t; }      // paused: nothing to redraw
                E.trackFill.style.width = `${t / DURATION * 100}%`;
                E.tRead.textContent = `${Math.floor(t / 60)}:${pad(t % 60)}`;
                E.controls.style.opacity = playing && performance.now() - idle > 2200 ? 0 : 1;
            }
            requestAnimationFrame(loop);
        };
        initP.then(() => { frame(0); requestAnimationFrame(loop); });
    }
})();
