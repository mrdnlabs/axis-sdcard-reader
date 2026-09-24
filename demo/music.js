// Original soundtrack, synthesized in Web Audio — no samples, no licensing. Upbeat tech/house at 120 BPM in
// C major (vi-IV-I-V: Am-F-C-G), arranged to the edit: scene changes land on bar lines and the chorus settles
// in when playback switches to the 4K motion stream (bar 16 = 32 s). The chorus is deliberately understated —
// no lead line, just the groove under the captions. Everything is scheduled from one seeded, deterministic
// graph, so it renders identically live (AudioContext) and offline (OfflineAudioContext).
'use strict';
(() => {
    const BPM = 120, BEAT = 60 / BPM, BAR = BEAT * 4;
    const DURATION = 66;              // 33 bars
    const SR = 44100;

    const midiHz = m => 440 * Math.pow(2, (m - 69) / 12);
    function prng(seed) {
        let a = seed | 0;
        return () => { a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a);
                       t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; };
    }

    // ---- harmony -------------------------------------------------------------------------------------
    const CHORDS = {
        Am: { bass: 45, pad: [57, 60, 64, 69], arp: [69, 72, 76, 81] },
        F:  { bass: 41, pad: [57, 60, 65, 69], arp: [65, 69, 72, 77] },
        C:  { bass: 48, pad: [55, 60, 64, 67], arp: [67, 72, 76, 79] },
        G:  { bass: 43, pad: [55, 59, 62, 67], arp: [67, 71, 74, 79] },
    };
    const LOOP = ['Am', 'F', 'C', 'G'];
    const chordAt = bar => bar >= 30 ? 'C' : LOOP[bar % 4];

    const ARP_PATTERN = [0, 1, 2, 3, 2, 1, 0, 1, 2, 3, 2, 1, 0, 1, 2, 3];

    // ---- arrangement (by bar) ------------------------------------------------------------------------
    // The chorus (bars 16-23, 26-29) is carried by percussion: a 16th-note shaker, a syncopated rim and small tom
    // fills at phrase ends. Its chords sit back: no lead, no open hats, and a quieter, darker pad with a slower
    // swell under a softer arp.
    const chorus = b => (b >= 16 && b <= 23) || (b >= 26 && b <= 29);
    const has = {
        kick:   b => (b >= 4 && b <= 29),
        clap:   b => (b >= 6 && b <= 23) || (b >= 26 && b <= 29),
        hat:    b => b >= 2 && b <= 29,
        ohat:   b => b >= 8 && b <= 15,
        shaker: chorus,
        rim:    chorus,
        bass:   b => b >= 4 && b <= 29,
        pad:    b => b <= 32,
        arp:    b => b <= 31,
        pump:   b => b >= 4 && b <= 29,
    };

    // Visual sync helper: 0..1 "kick energy" (for background pulses).
    function kickEnergy(t) {
        const bar = Math.floor(t / BAR);
        if (!has.kick(bar) || (bar === 15 && t % BAR >= BEAT * 2)) return 0;
        const since = t - Math.floor(t / BEAT) * BEAT;
        return Math.exp(-since / 0.11);
    }

    // ---- the graph -----------------------------------------------------------------------------------
    function build(ctx, dest, when = 0, offset = 0) {
        const rnd = prng(20260714);
        const at = x => when + x - offset;        // song time -> context time
        const live = x => x >= offset - 0.02;     // skip events before the seek offset

        // shared noise (seeded, so offline and live renders match)
        const noise = ctx.createBuffer(1, SR * 2, SR);
        { const d = noise.getChannelData(0); for (let i = 0; i < d.length; i++) d[i] = rnd() * 2 - 1; }

        // reverb: synthetic stereo impulse response
        const ir = ctx.createBuffer(2, Math.floor(SR * 2.6), SR);
        for (let c = 0; c < 2; c++) {
            const d = ir.getChannelData(c);
            for (let i = 0; i < d.length; i++) {
                const k = i / d.length;
                d[i] = (rnd() * 2 - 1) * Math.pow(1 - k, 3.2) * (i < 180 ? i / 180 : 1);
            }
        }

        // ---- buses
        const master = ctx.createGain(); master.gain.value = 0.9;
        const air = ctx.createBiquadFilter(); air.type = 'highshelf'; air.frequency.value = 7000; air.gain.value = 2.5;
        const comp = ctx.createDynamicsCompressor();
        comp.threshold.value = -16; comp.knee.value = 10; comp.ratio.value = 3.2;
        comp.attack.value = 0.005; comp.release.value = 0.2;
        master.connect(air).connect(comp).connect(dest);

        const verbIn = ctx.createGain(); verbIn.gain.value = 1;
        const verbHp = ctx.createBiquadFilter(); verbHp.type = 'highpass'; verbHp.frequency.value = 250;
        const verb = ctx.createConvolver(); verb.normalize = true; verb.buffer = ir;
        const verbOut = ctx.createGain(); verbOut.gain.value = 0.32;
        verbIn.connect(verbHp).connect(verb).connect(verbOut).connect(master);

        const dly = ctx.createDelay(1); dly.delayTime.value = BEAT * 0.75;   // dotted 8th
        const dlyFb = ctx.createGain(); dlyFb.gain.value = 0.34;
        const dlyLp = ctx.createBiquadFilter(); dlyLp.type = 'lowpass'; dlyLp.frequency.value = 3200;
        const dlyOut = ctx.createGain(); dlyOut.gain.value = 0.22;
        dly.connect(dlyLp).connect(dlyFb).connect(dly);
        dlyLp.connect(dlyOut).connect(master);

        const pump = ctx.createGain(); pump.gain.value = 1; pump.connect(master);
        // Levels balanced by measurement (band RMS via ffmpeg astats): lows ~5 dB over mids, hats present.
        const padBus = ctx.createGain(); padBus.gain.value = 0.135;
        const padLp = ctx.createBiquadFilter(); padLp.type = 'lowpass'; padLp.Q.value = 0.8;
        padBus.connect(padLp).connect(pump);
        padLp.connect(verbIn);

        const arpBus = ctx.createGain(); arpBus.gain.value = 0.13;
        const arpLp = ctx.createBiquadFilter(); arpLp.type = 'lowpass'; arpLp.Q.value = 2.5;
        arpBus.connect(arpLp).connect(pump);
        arpLp.connect(dly); arpLp.connect(verbIn);

        const bassBus = ctx.createGain(); bassBus.gain.value = 0.21; bassBus.connect(pump);
        const drumBus = ctx.createGain(); drumBus.gain.value = 1; drumBus.connect(master);

        // ---- filter automation (song time)
        padLp.frequency.setValueAtTime(320, at(Math.max(0, offset)));
        const padCut = [[0, 320], [8, 1700], [30, 2100], [32, 1300], [47, 1350], [48, 1700], [52, 1300], [59.5, 1400],
                        [60, 3600], [66, 900]];
        for (const [s, f] of padCut) if (at(s) >= when) padLp.frequency.exponentialRampToValueAtTime(f, at(s));
        const arpCut = [[0, 700], [8, 2200], [16, 3000], [30, 2600], [32, 1800], [47, 1800], [48, 1600], [52, 1800], [66, 1200]];
        arpLp.frequency.setValueAtTime(700, at(Math.max(0, offset)));
        for (const [s, f] of arpCut) if (at(s) >= when) arpLp.frequency.exponentialRampToValueAtTime(f, at(s));

        // ---- instruments
        const src = (t, dur) => {
            const s = ctx.createBufferSource(); s.buffer = noise;
            s.start(at(t), rnd() * 1.5, dur); return s;
        };

        function kick(t, v = 1) {
            if (!live(t)) return;
            const o = ctx.createOscillator(); o.type = 'sine';
            o.frequency.setValueAtTime(165, at(t));
            o.frequency.exponentialRampToValueAtTime(58, at(t + 0.07));
            o.frequency.exponentialRampToValueAtTime(46, at(t + 0.35));
            const g = ctx.createGain();
            g.gain.setValueAtTime(0.0001, at(t));
            g.gain.exponentialRampToValueAtTime(0.78 * v, at(t + 0.004));
            g.gain.exponentialRampToValueAtTime(0.36 * v, at(t + 0.09));
            g.gain.exponentialRampToValueAtTime(0.0001, at(t + 0.32));
            o.connect(g).connect(drumBus); o.start(at(t)); o.stop(at(t + 0.36));
            const c = src(t, 0.02); const hp = ctx.createBiquadFilter(); hp.type = 'highpass'; hp.frequency.value = 2500;
            const cg = ctx.createGain(); cg.gain.setValueAtTime(0.12 * v, at(t)); cg.gain.exponentialRampToValueAtTime(0.0001, at(t + 0.015));
            c.connect(hp).connect(cg).connect(drumBus);
        }

        function clap(t, v = 1) {
            if (!live(t)) return;
            const n = src(t, 0.35);
            const bp = ctx.createBiquadFilter(); bp.type = 'bandpass'; bp.frequency.value = 1350; bp.Q.value = 0.8;
            const g = ctx.createGain(); const gv = g.gain;
            gv.setValueAtTime(0.0001, at(t));
            for (const d of [0, 0.011, 0.022]) {
                gv.setValueAtTime(0.42 * v, at(t + d)); gv.exponentialRampToValueAtTime(0.08 * v, at(t + d + 0.009));
            }
            gv.setValueAtTime(0.4 * v, at(t + 0.033)); gv.exponentialRampToValueAtTime(0.0001, at(t + 0.24));
            n.connect(bp).connect(g); g.connect(drumBus);
            const send = ctx.createGain(); send.gain.value = 0.55; g.connect(send).connect(verbIn);
        }

        function hat(t, v = 1, open = false, pan = 0.18) {
            if (!live(t)) return;
            const len = open ? 0.3 : 0.05;
            const n = src(t, len + 0.02);
            const hp = ctx.createBiquadFilter(); hp.type = 'highpass'; hp.frequency.value = open ? 6200 : 7200;
            const g = ctx.createGain();
            g.gain.setValueAtTime(0.0001, at(t));
            g.gain.exponentialRampToValueAtTime((open ? 0.26 : 0.42) * v, at(t + 0.002));
            g.gain.exponentialRampToValueAtTime(0.0001, at(t + len));
            const p = ctx.createStereoPanner(); p.pan.value = pan;
            n.connect(hp).connect(g).connect(p).connect(drumBus);
        }

        function shaker(t, v = 1, pan = -0.3) {
            if (!live(t)) return;
            const n = src(t, 0.1);
            const bp = ctx.createBiquadFilter(); bp.type = 'bandpass'; bp.frequency.value = 7600; bp.Q.value = 0.9;
            const g = ctx.createGain();
            g.gain.setValueAtTime(0.0001, at(t));
            g.gain.exponentialRampToValueAtTime(0.3 * v, at(t + 0.014));     // slower attack than a hat: a shake, not a tick
            g.gain.exponentialRampToValueAtTime(0.0001, at(t + 0.085));
            const p = ctx.createStereoPanner(); p.pan.value = pan;
            n.connect(bp).connect(g).connect(p).connect(drumBus);
        }

        function rim(t, v = 1, pan = 0.25) {
            if (!live(t)) return;
            const o = ctx.createOscillator(); o.type = 'triangle';
            o.frequency.setValueAtTime(1850, at(t)); o.frequency.exponentialRampToValueAtTime(1500, at(t + 0.04));
            const g = ctx.createGain();
            g.gain.setValueAtTime(0.0001, at(t)); g.gain.exponentialRampToValueAtTime(0.26 * v, at(t + 0.002));
            g.gain.exponentialRampToValueAtTime(0.0001, at(t + 0.06));
            const n = src(t, 0.03);
            const bp = ctx.createBiquadFilter(); bp.type = 'bandpass'; bp.frequency.value = 3200; bp.Q.value = 1.4;
            const ng = ctx.createGain(); ng.gain.setValueAtTime(0.22 * v, at(t)); ng.gain.exponentialRampToValueAtTime(0.0001, at(t + 0.025));
            const p = ctx.createStereoPanner(); p.pan.value = pan;
            o.connect(g).connect(p); n.connect(bp).connect(ng).connect(p); p.connect(drumBus);
            const send = ctx.createGain(); send.gain.value = 0.3; p.connect(send).connect(verbIn);
            o.start(at(t)); o.stop(at(t + 0.08));
        }

        function tom(t, hz, v = 1) {
            if (!live(t)) return;
            const o = ctx.createOscillator(); o.type = 'sine';
            o.frequency.setValueAtTime(hz * 1.5, at(t)); o.frequency.exponentialRampToValueAtTime(hz, at(t + 0.06));
            const g = ctx.createGain();
            g.gain.setValueAtTime(0.0001, at(t)); g.gain.exponentialRampToValueAtTime(0.45 * v, at(t + 0.004));
            g.gain.exponentialRampToValueAtTime(0.0001, at(t + 0.28));
            const p = ctx.createStereoPanner(); p.pan.value = hz > 150 ? 0.3 : -0.3;
            o.connect(g).connect(p).connect(drumBus); o.start(at(t)); o.stop(at(t + 0.3));
        }

        function bass(t, dur, m, v = 1) {
            if (!live(t)) return;
            const saw = ctx.createOscillator(); saw.type = 'sawtooth'; saw.frequency.value = midiHz(m);
            const sub = ctx.createOscillator(); sub.type = 'sine'; sub.frequency.value = midiHz(m - 12);
            const lp = ctx.createBiquadFilter(); lp.type = 'lowpass'; lp.Q.value = 4;
            lp.frequency.setValueAtTime(180, at(t)); lp.frequency.exponentialRampToValueAtTime(1100, at(t + 0.015));
            lp.frequency.exponentialRampToValueAtTime(320, at(t + dur));
            const g = ctx.createGain();
            g.gain.setValueAtTime(0.0001, at(t)); g.gain.exponentialRampToValueAtTime(0.9 * v, at(t + 0.008));
            g.gain.setValueAtTime(0.75 * v, at(t + dur - 0.03)); g.gain.exponentialRampToValueAtTime(0.0001, at(t + dur + 0.02));
            const sg = ctx.createGain(); sg.gain.value = 0.55;
            saw.connect(lp).connect(g); sub.connect(sg).connect(g); g.connect(bassBus);
            saw.start(at(t)); sub.start(at(t)); saw.stop(at(t + dur + 0.05)); sub.stop(at(t + dur + 0.05));
        }

        function padChord(t, dur, notes, v = 1, attack = 0.22) {
            if (!live(t + dur - 0.05)) return;
            const t0 = Math.max(t, offset);
            notes.forEach((m, i) => {
                for (const det of [-9, 9]) {
                    const o = ctx.createOscillator(); o.type = 'sawtooth'; o.frequency.value = midiHz(m); o.detune.value = det + (i - 1.5) * 1.5;
                    const g = ctx.createGain();
                    g.gain.setValueAtTime(0.0001, at(t0));
                    g.gain.exponentialRampToValueAtTime(0.5 * v, at(t0 + attack));
                    g.gain.setValueAtTime(0.5 * v, at(t + dur - 0.1));
                    g.gain.exponentialRampToValueAtTime(0.0001, at(t + dur + 0.5));
                    const p = ctx.createStereoPanner(); p.pan.value = det < 0 ? -0.45 : 0.45;
                    o.connect(g).connect(p).connect(padBus);
                    o.start(at(t0)); o.stop(at(t + dur + 0.55));
                }
            });
        }

        function pluck(t, m, v = 1, pan = 0) {
            if (!live(t)) return;
            const o = ctx.createOscillator(); o.type = 'square'; o.frequency.value = midiHz(m);
            const o2 = ctx.createOscillator(); o2.type = 'sawtooth'; o2.frequency.value = midiHz(m); o2.detune.value = 7;
            const lp = ctx.createBiquadFilter(); lp.type = 'lowpass'; lp.Q.value = 3;
            lp.frequency.setValueAtTime(4200, at(t)); lp.frequency.exponentialRampToValueAtTime(700, at(t + 0.14));
            const g = ctx.createGain();
            g.gain.setValueAtTime(0.0001, at(t)); g.gain.exponentialRampToValueAtTime(0.7 * v, at(t + 0.003));
            g.gain.exponentialRampToValueAtTime(0.0001, at(t + 0.2));
            const p = ctx.createStereoPanner(); p.pan.value = pan;
            o.connect(lp); o2.connect(lp); lp.connect(g).connect(p).connect(arpBus);
            o.start(at(t)); o2.start(at(t)); o.stop(at(t + 0.24)); o2.stop(at(t + 0.24));
        }

        function riser(t, dur, v = 1) {
            if (!live(t + dur)) return;
            const t0 = Math.max(t, offset);
            const n = ctx.createBufferSource(); n.buffer = noise; n.loop = true; n.start(at(t0)); n.stop(at(t + dur + 0.05));
            const bp = ctx.createBiquadFilter(); bp.type = 'bandpass'; bp.Q.value = 1.6;
            bp.frequency.setValueAtTime(300, at(t0)); bp.frequency.exponentialRampToValueAtTime(7500, at(t + dur));
            const g = ctx.createGain(); g.gain.setValueAtTime(0.0001, at(t0));
            g.gain.exponentialRampToValueAtTime(0.28 * v, at(t + dur - 0.02)); g.gain.exponentialRampToValueAtTime(0.0001, at(t + dur + 0.04));
            n.connect(bp).connect(g); g.connect(master); g.connect(verbIn);
        }

        function impact(t, v = 1) {
            if (!live(t)) return;
            const o = ctx.createOscillator(); o.type = 'sine';
            o.frequency.setValueAtTime(70, at(t)); o.frequency.exponentialRampToValueAtTime(32, at(t + 1.4));
            const g = ctx.createGain(); g.gain.setValueAtTime(0.0001, at(t));
            g.gain.exponentialRampToValueAtTime(0.7 * v, at(t + 0.01)); g.gain.exponentialRampToValueAtTime(0.0001, at(t + 1.6));
            o.connect(g).connect(master); o.start(at(t)); o.stop(at(t + 1.7));
            const n = src(t, 2.2); const hp = ctx.createBiquadFilter(); hp.type = 'highpass'; hp.frequency.value = 3200;
            const ng = ctx.createGain(); ng.gain.setValueAtTime(0.22 * v, at(t)); ng.gain.exponentialRampToValueAtTime(0.0001, at(t + 2.0));
            n.connect(hp).connect(ng); ng.connect(master); ng.connect(verbIn);
        }

        // ---- sequence
        for (let bar = 0; bar < 33; bar++) {
            const b0 = bar * BAR;
            const ch = CHORDS[chordAt(bar)];

            // side-chain pump on every beat of the groove
            if (has.pump(bar)) {
                for (let beat = 0; beat < 4; beat++) {
                    const t = b0 + beat * BEAT;
                    if (bar === 15 && beat >= 2) continue;
                    if (live(t)) { pump.gain.setValueAtTime(0.34, at(t)); pump.gain.setTargetAtTime(1, at(t + 0.015), 0.075); }
                }
            }

            if (has.pad(bar)) {
                const v = bar < 4 ? 0.75 : bar >= 30 ? 1.15 : chorus(bar) ? 0.55 : 1;
                if (bar === 30) padChord(b0, BAR * 3 - 0.4, ch.pad, v);
                else if (bar < 30) padChord(b0, BAR, ch.pad, v, chorus(bar) ? 0.6 : 0.22);
            }

            for (let step = 0; step < 16; step++) {
                const t = b0 + step * BEAT / 4;
                const beat = step / 4;

                if (has.kick(bar) && step % 4 === 0 && !(bar === 15 && step >= 8)) kick(t);
                if (has.clap(bar) && (step === 4 || step === 12)) clap(t, chorus(bar) ? 0.8 : 1);
                if (has.shaker(bar)) shaker(t, step % 4 === 2 ? 1 : step % 2 ? 0.55 : 0.4);
                if (has.rim(bar) && (step === 3 || step === 6 || step === 11 || (bar % 2 === 1 && step === 14))) {
                    rim(t, step === 6 ? 0.9 : 0.75);
                }
                if (chorus(bar) && bar % 4 === 3 && step >= 13) tom(t, [190, 150, 115][step - 13], 0.6 + (step - 13) * 0.12);
                if (has.hat(bar)) {
                    const groove = bar >= 8;
                    if (step % 4 === 2) hat(t, groove ? 1 : 0.7, false, 0.2);
                    else if (groove && step % 2 === 1) hat(t, 0.42, false, -0.2);
                    else if (!groove && bar >= 2 && step % 2 === 1) hat(t, 0.28, false, -0.15);
                }
                if (has.ohat(bar) && step % 4 === 2 && bar >= 8) hat(t + 0.001, 0.55, true, 0.25);
                if (has.bass(bar) && step % 4 === 2 && !(bar === 15 && step >= 8)) bass(t, BEAT / 2 - 0.02, ch.bass);
                if (has.arp(bar)) {
                    const v = bar < 4 ? 0.5 + bar * 0.08 : bar >= 24 && bar <= 25 ? 0.6 : chorus(bar) ? 0.55 : 1;
                    if (bar >= 30 && step > 11) continue;
                    pluck(t, ch.arp[ARP_PATTERN[step]], v, step % 2 ? 0.35 : -0.35);
                }
                void beat;
            }
        }

        // fills, risers, impacts (the ones into the chorus are kept gentle: it arrives, it doesn't drop)
        for (let s = 0; s < 8; s++) clap(7 * BAR + 3 * BEAT + s * BEAT / 8, 0.35 + s * 0.06);   // bar 7 fill
        for (let s = 0; s < 8; s++) clap(15 * BAR + 2 * BEAT + s * BEAT / 4, 0.18 + s * 0.04);  // bar 15 roll
        riser(3 * BAR, BAR, 0.8);
        riser(15 * BAR, BAR, 0.5);
        riser(25 * BAR, BAR, 0.55);
        riser(29 * BAR, BAR, 0.8);
        impact(4 * BAR, 0.8);
        impact(16 * BAR, 0.5);
        impact(26 * BAR, 0.45);
        impact(30 * BAR, 1.1);
        kick(30 * BAR, 1.1);

        // master fade at the very end
        master.gain.setValueAtTime(0.9, at(Math.max(offset, 61)));
        master.gain.linearRampToValueAtTime(0.0001, at(65.8));
        return master;
    }

    // Offline render -> 16-bit stereo WAV (base64), peak-normalised to -1 dBFS.
    async function renderWavBase64() {
        const ctx = new OfflineAudioContext(2, Math.ceil(SR * DURATION), SR);
        build(ctx, ctx.destination, 0, 0);
        const buf = await ctx.startRendering();
        const L = buf.getChannelData(0), R = buf.getChannelData(1);
        let peak = 1e-9;
        for (let i = 0; i < L.length; i++) peak = Math.max(peak, Math.abs(L[i]), Math.abs(R[i]));
        const gain = 0.891 / peak;
        const n = L.length, bytes = 44 + n * 4;
        const dv = new DataView(new ArrayBuffer(bytes));
        const str = (o, s) => { for (let i = 0; i < s.length; i++) dv.setUint8(o + i, s.charCodeAt(i)); };
        str(0, 'RIFF'); dv.setUint32(4, bytes - 8, true); str(8, 'WAVE'); str(12, 'fmt ');
        dv.setUint32(16, 16, true); dv.setUint16(20, 1, true); dv.setUint16(22, 2, true); dv.setUint32(24, SR, true);
        dv.setUint32(28, SR * 4, true); dv.setUint16(32, 4, true); dv.setUint16(34, 16, true); str(36, 'data'); dv.setUint32(40, n * 4, true);
        for (let i = 0, o = 44; i < n; i++, o += 4) {
            dv.setInt16(o, Math.max(-32767, Math.min(32767, Math.round(L[i] * gain * 32767))), true);
            dv.setInt16(o + 2, Math.max(-32767, Math.min(32767, Math.round(R[i] * gain * 32767))), true);
        }
        const blob = new Blob([dv.buffer], { type: 'audio/wav' });
        const url = await new Promise(res => { const fr = new FileReader(); fr.onload = () => res(fr.result); fr.readAsDataURL(blob); });
        return String(url).slice(String(url).indexOf(',') + 1);
    }

    window.Music = { BPM, BEAT, BAR, DURATION, build, renderWavBase64, kickEnergy };
})();
