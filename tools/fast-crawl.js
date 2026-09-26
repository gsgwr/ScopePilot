const fs = require('fs');
const path = require('path');
const { chromium } = require('./playwright-runtime/node_modules/playwright');

const runDirectory = process.argv[2];
if (!runDirectory) throw new Error('run directory is required');
const engagement = JSON.parse(fs.readFileSync(path.join(runDirectory, 'engagement.json'), 'utf8'));
const allowed = new Set(engagement.allowedOrigins.map(normalizeOrigin));
const startedAt = new Date();
const crawlMinutes = Math.max(1, Math.floor(engagement.limits.MaxMinutes * 0.7));
const deadline = Date.now() + crawlMinutes * 60_000;
const maxPages = Math.max(1, engagement.limits.MaxPages);
const maxRequests = Math.max(1, engagement.limits.MaxRequests);
const pageQueue = [normalizeUrl(engagement.StartUrl)];
const queued = new Set(pageQueue);
const attempted = new Set();
const visited = new Set();
const skipped = [];
const pages = [];
const requests = new Map();
let failureCode = null;
const forbiddenPattern = /(?:logout|log-out|signout|sign-out|delete|remove|destroy|unsubscribe|checkout|purchase|payment|approve|reject|cancel|cart\/add|action=(?:delete|remove|logout|purchase|approve|reject))/i;
const nonPageExtension = /\.(?:avif|bmp|css|csv|docx?|eot|gif|ico|jpe?g|js|json|mp3|mp4|mpeg|pdf|png|pptx?|svg|tar|tiff?|ttf|txt|wav|webm|webp|woff2?|xlsx?|xml|zip)(?:$|\?)/i;

let browser;
async function main() {
try {
  browser = await chromium.launch({ channel: 'msedge', headless: true, proxy: { server: 'http://127.0.0.1:8080' } });
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  await context.route('**/*', async route => {
    const request = route.request();
    if (request.isNavigationRequest() && request.frame() === request.frame().page().mainFrame()) {
      try {
        if (!allowed.has(new URL(request.url()).origin)) return route.abort('blockedbyclient');
      } catch { return route.abort('blockedbyclient'); }
    }
    return route.continue();
  });
  context.on('response', response => {
    if (requests.size >= maxRequests) return;
    const request = response.request();
    let url;
    try { url = new URL(response.url()); } catch { return; }
    if (!allowed.has(url.origin)) return;
    const key = `${request.method()} ${url.href}`;
    if (!requests.has(key)) {
      requests.set(key, {
        method: request.method(), url: url.href, statusCode: response.status(),
        contentType: response.headers()['content-type'] || '', source: 'PlaywrightFastCrawler',
        role: '未認証', pageTitle: '', observedAt: new Date().toISOString()
      });
    }
  });

  const page = await context.newPage();
  while (pageQueue.length && pages.length < maxPages && requests.size < maxRequests && Date.now() < deadline) {
    const url = pageQueue.shift();
    if (!url || attempted.has(url) || visited.has(url)) continue;
    if (!isSafePageUrl(url)) { skipped.push({ url, reason: 'non-page or potentially state-changing URL' }); continue; }
    attempted.add(url);
    try {
      const response = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 20_000 });
      await page.waitForTimeout(100);
      const finalUrl = normalizeUrl(page.url());
      if (!allowed.has(new URL(finalUrl).origin)) { skipped.push({ url, reason: 'redirected outside allowed origin' }); continue; }
      if (visited.has(finalUrl)) continue;
      visited.add(finalUrl);
      queued.add(finalUrl);
      const title = await page.title().catch(() => '');
      const links = await page.locator('a[href]').evaluateAll(elements => elements.map(a => a.href));
      const forms = await page.locator('form').evaluateAll(forms => forms.map(form => ({
        method: (form.method || 'GET').toUpperCase(), action: new URL(form.getAttribute('action') || location.href, location.href).href,
        fields: [...form.elements].map(element => element.name).filter(Boolean)
      })));
      pages.push({ url: finalUrl, statusCode: response?.status() ?? null, title, forms, discoveredLinks: links.length });
      const documentKey = `GET ${finalUrl}`;
      if (requests.has(documentKey)) {
        const documentRequest = requests.get(documentKey);
        documentRequest.pageTitle = title;
        documentRequest.pageInspected = true;
        documentRequest.formCount = forms.length;
        documentRequest.formFieldNames = [...new Set(forms.flatMap(form => form.fields))].slice(0, 50);
      }
      for (const link of links) {
        let candidate;
        try { candidate = normalizeUrl(link); } catch { continue; }
        if (allowed.has(new URL(candidate).origin) && !queued.has(candidate) && !attempted.has(candidate) && !visited.has(candidate)) {
          queued.add(candidate);
          pageQueue.push(candidate);
        }
      }
      persist();
      process.stdout.write(`PAGE ${pages.length}/${maxPages} REQUESTS ${requests.size}/${maxRequests} QUEUED ${pageQueue.length} ${finalUrl}\n`);
    } catch (error) {
      const reason = String(error.message || error);
      skipped.push({ url, reason });
      process.stdout.write(`SKIP ${url} ${reason.split('\n')[0]}\n`);
      if (pages.length === 0 && isProxyFailure(reason)) {
        failureCode = 'proxy-unavailable';
        process.stdout.write('ERROR Burp Proxy 127.0.0.1:8080 に接続できないため、開始URLを取得できません。\n');
        break;
      }
    }
  }
  persist();
  fs.writeFileSync(path.join(runDirectory, 'fast-crawl-summary.json'), JSON.stringify({
    status: failureCode ? 'failed' : 'completed', failureCode, startedAt, finishedAt: new Date(), visitedPageCount: pages.length,
    discoveredUrlCount: queued.size, observedRequestCount: requests.size,
    remainingQueueCount: pageQueue.length, skippedCount: skipped.length,
    stoppedBy: Date.now() >= deadline ? 'time-limit' : pages.length >= maxPages ? 'page-limit' : requests.size >= maxRequests ? 'request-limit' : 'queue-empty',
    pages, skipped: skipped.slice(0, 1000)
  }, null, 2), 'utf8');
  if (failureCode) {
    process.stdout.write(`FAILED ${failureCode} PAGES ${pages.length} DISCOVERED ${queued.size} REQUESTS ${requests.size}\n`);
    process.exitCode = 2;
  } else {
    process.stdout.write(`COMPLETE PAGES ${pages.length} DISCOVERED ${queued.size} REQUESTS ${requests.size}\n`);
  }
} finally {
  if (browser) await browser.close().catch(() => {});
}
}

function persist() {
  const requestLines = [...requests.values()].map(x => JSON.stringify(x)).join('\n') + (requests.size ? '\n' : '');
  fs.writeFileSync(path.join(runDirectory, 'crawl-pages.jsonl'), pages.map(x => JSON.stringify(x)).join('\n') + (pages.length ? '\n' : ''), 'utf8');
  fs.writeFileSync(path.join(runDirectory, 'fast-observed-requests.jsonl'), requestLines, 'utf8');
  fs.writeFileSync(path.join(runDirectory, 'observed-requests.jsonl'), requestLines, 'utf8');
}

function normalizeOrigin(value) { return new URL(value).origin; }
function normalizeUrl(value) { const url = new URL(value); url.hash = ''; return url.href; }
function isSafePageUrl(value) {
  return !forbiddenPattern.test(value) && !nonPageExtension.test(new URL(value).pathname);
}
function isProxyFailure(message) {
  return /ERR_PROXY_CONNECTION_FAILED|ERR_TUNNEL_CONNECTION_FAILED|proxy\s+connection\s+(?:failed|refused)|ECONNREFUSED/i.test(message);
}

main().catch(error => {
  process.stderr.write(`${error.stack || error}\n`);
  process.exitCode = 1;
});
