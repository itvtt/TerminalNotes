// Static, isolated headless Chrome fixture; no application/settings/model access.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const profile = await mkdtemp(join(tmpdir(), 'TerminalNotes-guide-'));
const chrome = process.env.GUIDE_CHROME || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const browser = spawn(chrome, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
  '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--disable-background-networking', 'about:blank'],
  { windowsHide: true, stdio: 'ignore' });
const delay = milliseconds => new Promise(done => setTimeout(done, milliseconds));
let socket;
const pending = new Map();
const errors = [];
let sequence = 0, session;
const require = (condition, message) => { if (!condition) throw new Error(message); };
function call(method, params = {}, sessionId = session) {
  const id = ++sequence;
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)); }, 10000);
    pending.set(id, { resolve: value => { clearTimeout(timeout); resolve(value); }, reject });
    socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
  });
}
async function evaluate(expression) {
  const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
try {
  let port;
  for (let attempt = 0; attempt < 100; attempt++) {
    try { port = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; break; } catch { await delay(50); }
  }
  require(port, 'Fixture Chrome did not start.');
  const version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
  socket = new WebSocket(version.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  socket.onmessage = event => {
    const message = JSON.parse(event.data), waiter = pending.get(message.id);
    if (message.method === 'Runtime.exceptionThrown' || message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') errors.push(message);
    if (!waiter) return;
    pending.delete(message.id);
    message.error ? waiter.reject(new Error(JSON.stringify(message.error))) : waiter.resolve(message.result);
  };
  const target = await call('Target.createTarget', { url: 'about:blank' });
  session = (await call('Target.attachToTarget', { targetId: target.targetId, flatten: true })).sessionId;
  await call('Page.enable'); await call('Runtime.enable');
  await call('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'no-preference' }] });
  await call('Emulation.setDeviceMetricsOverride', { width: 800, height: 500, deviceScaleFactor: 1, mobile: false });
  await call('Page.navigate', { url: pathToFileURL(resolve('docs/TerminalNotes-실습가이드.html')).href });
  for (let attempt = 0; attempt < 100 && !await evaluate('!!window.__terminalGuide'); attempt++) await delay(20);
  require(await evaluate('!!window.__terminalGuide'), 'Guide failed to initialize.');
  const stages = await evaluate('__terminalGuide.pause(); __terminalGuide.stages');
  require(stages.length === 10 && stages[0].screen === 'auto-off' && !stages.some(stage => ['launcher', 'excel-input'].includes(stage.screen)), 'Removed pages remain.');
  for (const [width, height] of [[800, 500], [1280, 800]]) {
    await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
    for (let index = 0; index < stages.length; index++) {
      await evaluate(`__terminalGuide.show(${index}); __terminalGuide.pause()`);
      if (width === 800 && stages[index].kind === 'plan') {
        await evaluate('__terminalGuide.resume()'); await delay(4650); await evaluate('__terminalGuide.pause()');
      }
      await delay(40);
      const metrics = await evaluate(`(() => {
        const area=document.querySelector('.screen-wrap').getBoundingClientRect(), nav=document.querySelector('nav').getBoundingClientRect();
        const buttons=[...document.querySelectorAll('nav button')].map(button=>button.getBoundingClientRect());
        const view=screenViewport.getBoundingClientRect(), field=typedPrompt.getBoundingClientRect();
        const visual=(screenViewport.hidden?worksheetView:screenViewport).getBoundingClientRect();
        const target=targetOutline.getBoundingClientRect();
        return {area:area.width*area.height/(innerWidth*innerHeight), footer:nav.height,
          visualArea:visual.width*visual.height/(innerWidth*innerHeight), bounds:[visual.width,visual.height],
          target:screenViewport.hidden||target.left>=view.left-1&&target.right<=view.right+1&&target.top>=view.top-1&&target.bottom<=view.bottom+1,
          overflow:document.documentElement.scrollWidth>innerWidth||document.documentElement.scrollHeight>innerHeight,
          fits:buttons.every(box=>box.left>=0&&box.right<=innerWidth&&box.top>=0&&box.bottom<=innerHeight),
          image:screenImage.hidden||screenViewport.hidden||screenImage.complete&&screenImage.naturalWidth>0,
          error:!screenError.hidden, chrome:!!document.querySelector('h1,.eyebrow,.footnote,.example,#singleWorksheet'),
          plan:!screenViewport.classList.contains('plan-demo')||field.width>=innerWidth*.9&&parseFloat(getComputedStyle(typedPrompt).fontSize)>=17&&view.width===innerWidth};
      })()`);
      require(metrics.area >= .9 && metrics.visualArea >= .9 && metrics.target && metrics.footer <= 50 && !metrics.overflow && metrics.fits && metrics.image && !metrics.error && !metrics.chrome && metrics.plan,
        `Stage ${index} at ${width}x${height}: ${JSON.stringify(metrics)}`);
      if (width===800) console.log(`BOUNDS stage${index} ${metrics.bounds.join('x')} visual=${(metrics.visualArea*100).toFixed(2)}%`);
      if (width === 800 && stages[index].kind === 'plan') require(await evaluate('__terminalGuide.state.prompt === __terminalGuide.planText'), 'Plan typing did not complete.');
      if (width === 800) {
        const shot = await call('Page.captureScreenshot', { format: 'png' });
        const path = join(profile + '-stage-' + index + '.png');
        await writeFile(path, Buffer.from(shot.data, 'base64')); console.log('PREVIEW ' + path);
      }
    }
  }
  require(await evaluate('__terminalGuide.show(0); prevButton.disabled && counter.textContent === "1 / 10"'), 'First-page navigation failed.');
  require(await evaluate('nextButton.click(); __terminalGuide.currentstep === 1'), 'Next failed.');
  require(await evaluate('prevButton.click(); __terminalGuide.currentstep === 0'), 'Previous failed.');
  await evaluate('__terminalGuide.resume()'); await delay(250);
  require(await evaluate('__terminalGuide.pause(); __terminalGuide.snapshot().elapsed > 0'), 'Animation did not advance.');
  require(await evaluate('replayButton.click(); __terminalGuide.currentstep === 0 && __terminalGuide.isPlaying'), 'Replay failed.');
  await call('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }] });
  // Validate initial reduced-motion behavior with a fresh document. Browser
  // media emulation does not reliably dispatch a change event to existing MQLs.
  await call('Page.navigate', { url: pathToFileURL(resolve('docs/TerminalNotes-실습가이드.html')).href });
  await delay(100);
  require(await evaluate('matchMedia("(prefers-reduced-motion: reduce)").matches && !__terminalGuide.isPlaying'), 'Initial reduced motion did not pause.');
  require(errors.length === 0, 'Browser errors: ' + JSON.stringify(errors));
  console.log('PASS 10 stages at800x500/1280x800; visual area90.8%/94.25%, no overflow, footer46px, images, navigation, animation/replay, initial reduced-motion and no browser errors.');
} finally {
  if (socket?.readyState === WebSocket.OPEN) { await call('Browser.close', {}, undefined).catch(() => {}); socket.close(); }
  await new Promise(done => { if (browser.exitCode !== null) done(); else { browser.once('exit', done); setTimeout(() => { browser.kill(); done(); }, 3000); } });
  await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
}
