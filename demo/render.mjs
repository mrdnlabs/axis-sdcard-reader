// Renders demo/index.html deterministically: headless Edge is driven over the DevTools protocol, one frame at a
// time (the page exposes window.__seek(t)), and frames are piped straight into ffmpeg. The soundtrack is rendered
// offline by the page's own Web Audio graph (window.__renderAudio) so picture and music are sample-locked.
//
// Zero npm dependencies: Node 22's built-in WebSocket speaks CDP directly.
//
//   node demo/render.mjs                                   full video -> demo/out/axis-sd-card-reader-demo.mp4
//   node demo/render.mjs --still 12.5,33 --out demo/out/stills
//   node demo/render.mjs --still 0 --size 1440x900 --query ref=1 --out demo/out/ref
//   node demo/render.mjs --audio --out demo/out/music.wav
//   node demo/render.mjs --from 30 --to 40                 partial render (for quick review)
//   node demo/render.mjs --at 34 --eval "document.title"   evaluate an expression at time t (layout debugging)

import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { once } from 'node:events';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const opts = parseArgs(process.argv.slice(2));

const EDGE = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
].find(existsSync);
if (!EDGE) throw new Error('No Edge/Chrome found for headless rendering');

const [width, height] = (opts.size ?? '1920x1080').split('x').map(Number);
const fps = Number(opts.fps ?? 30);
const url = pathToFileURL(join(here, 'index.html')).href + '?render=1' + (opts.query ? '&' + opts.query : '');

const browser = await launch();
try {
    const page = await browser.newPage(width, height);
    await page.goto(url);
    const duration = await page.eval('window.__duration');

    if (opts.still !== undefined) {
        const outDir = resolve(opts.out ?? join(here, 'out', 'stills'));
        mkdirSync(outDir, { recursive: true });
        for (const t of String(opts.still).split(',').map(Number)) {
            await page.seek(t);
            const file = join(outDir, `still_${t.toFixed(2).padStart(6, '0')}s.png`);
            writeFileSync(file, await page.screenshot());
            console.log('still', t, '->', file);
        }
    } else if (opts.eval) {
        await page.seek(Number(opts.at ?? 0));
        console.log(JSON.stringify(await page.eval(opts.eval), null, 1));
    } else if (opts.audio) {
        const out = resolve(opts.out ?? join(here, 'out', 'music.wav'));
        mkdirSync(dirname(out), { recursive: true });
        writeFileSync(out, Buffer.from(await page.eval('window.__renderAudio()'), 'base64'));
        console.log('audio ->', out);
    } else {
        await renderVideo(page, duration);
    }
} finally {
    await browser.close();
}

async function renderVideo(page, duration) {
    const from = Number(opts.from ?? 0);
    const to = Math.min(Number(opts.to ?? duration), duration);
    const out = resolve(opts.out ?? join(here, 'out', 'axis-sd-card-reader-demo.mp4'));
    mkdirSync(dirname(out), { recursive: true });

    // Soundtrack first (sample-accurate, rendered by the page's own graph), trimmed to the rendered range.
    const wav = join(dirname(out), 'soundtrack.wav');
    writeFileSync(wav, Buffer.from(await page.eval('window.__renderAudio()'), 'base64'));

    const frames = Math.round((to - from) * fps);
    const ff = spawn('ffmpeg', [
        '-y', '-loglevel', 'error',
        '-f', 'image2pipe', '-framerate', String(fps), '-c:v', 'png', '-i', '-',
        '-ss', String(from), '-t', String(to - from), '-i', wav,
        '-c:v', 'libx264', '-preset', 'slow', '-crf', '15', '-pix_fmt', 'yuv420p', '-profile:v', 'high',
        '-tune', 'animation', '-g', String(fps * 2),
        '-c:a', 'aac', '-b:a', '256k', '-shortest', '-movflags', '+faststart', out,
    ], { stdio: ['pipe', 'inherit', 'inherit'] });

    const started = Date.now();
    for (let i = 0; i < frames; i++) {
        await page.seek(from + i / fps);
        const png = await page.screenshot();
        if (!ff.stdin.write(png)) await once(ff.stdin, 'drain');
        if (i % fps === 0 || i === frames - 1) {
            const pct = ((i + 1) / frames * 100).toFixed(0).padStart(3);
            const spf = (Date.now() - started) / (i + 1) / 1000;
            process.stdout.write(`\r  frame ${i + 1}/${frames}  ${pct}%  (${spf.toFixed(2)} s/frame)   `);
        }
    }
    ff.stdin.end();
    const [code] = await once(ff, 'close');
    process.stdout.write('\n');
    if (code !== 0) throw new Error(`ffmpeg exited ${code}`);
    console.log('video ->', out, `(${((Date.now() - started) / 1000).toFixed(0)} s)`);
}

// --- minimal CDP over Node's built-in WebSocket ----------------------------------------------------------

async function launch() {
    const profile = mkdtempSync(join(tmpdir(), 'axis-demo-edge-'));
    const proc = spawn(EDGE, [
        '--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
        '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--mute-audio',
        '--hide-scrollbars', '--force-color-profile=srgb', '--font-render-hinting=none',
        '--allow-file-access-from-files', '--autoplay-policy=no-user-gesture-required',
        // Software compositing: with the GPU, ~1 frame in 30 under the blurred backdrop came back as a tiled
        // surface (the top-left block repeated across the frame). The software path renders at the same speed.
        '--disable-gpu', 'about:blank',
    ], { stdio: 'ignore' });

    const portFile = join(profile, 'DevToolsActivePort');
    for (let i = 0; !existsSync(portFile) || readFileSync(portFile, 'utf8').split('\n').length < 2; i++) {
        if (i > 300) throw new Error('Edge never opened its DevTools port');
        await new Promise(r => setTimeout(r, 50));
    }
    const [port, path] = readFileSync(portFile, 'utf8').trim().split('\n');
    const cdp = await connect(`ws://127.0.0.1:${port}${path}`);

    return {
        async newPage(w, h) {
            const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
            const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
            const s = (m, p) => cdp.send(m, p, sessionId);
            await s('Page.enable');
            await s('Runtime.enable');
            await s('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile: false });
            return {
                async goto(u) {
                    const loaded = cdp.waitFor('Page.loadEventFired', sessionId);
                    await s('Page.navigate', { url: u });
                    await loaded;
                    await this.eval('document.fonts.ready.then(() => window.__ready)');
                },
                async eval(expression) {
                    const r = await s('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
                    if (r.exceptionDetails) {
                        throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
                    }
                    return r.result.value;
                },
                // The page's __seek draws frame t and resolves two animation frames later, once it is composited.
                async seek(t) {
                    await this.eval(`window.__seek(${t})`);
                },
                async screenshot() {
                    const { data } = await s('Page.captureScreenshot', { format: 'png', fromSurface: true });
                    return Buffer.from(data, 'base64');
                },
            };
        },
        async close() {
            const exited = proc.exitCode !== null ? Promise.resolve() : new Promise(r => proc.once('exit', r));
            try { await cdp.send('Browser.close'); } catch { /* already gone */ }
            proc.kill();
            await Promise.race([exited, new Promise(r => setTimeout(r, 5000))]);
            // Edge's helper processes can hold the profile for a moment after the browser exits.
            try { rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 }); } catch { /* best effort */ }
        },
    };
}

async function connect(wsUrl) {
    const ws = new WebSocket(wsUrl);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
    let nextId = 0;
    const pending = new Map();
    const listeners = new Set();
    ws.onmessage = ev => {
        const msg = JSON.parse(ev.data);
        if (msg.id !== undefined) {
            const p = pending.get(msg.id);
            pending.delete(msg.id);
            msg.error ? p.rej(new Error(`${p.method}: ${msg.error.message}`)) : p.res(msg.result);
        } else {
            for (const l of listeners) l(msg);
        }
    };
    return {
        send(method, params = {}, sessionId) {
            const id = ++nextId;
            ws.send(JSON.stringify({ id, method, params, sessionId }));
            return new Promise((res, rej) => pending.set(id, { res, rej, method }));
        },
        waitFor(method, sessionId) {
            return new Promise(res => {
                const l = msg => {
                    if (msg.method === method && (!sessionId || msg.sessionId === sessionId)) {
                        listeners.delete(l);
                        res(msg.params);
                    }
                };
                listeners.add(l);
            });
        },
    };
}

function parseArgs(argv) {
    const o = {};
    for (let i = 0; i < argv.length; i++) {
        const k = argv[i].replace(/^--/, '');
        const next = argv[i + 1];
        o[k] = next === undefined || next.startsWith('--') ? true : (i++, next);
    }
    return o;
}
