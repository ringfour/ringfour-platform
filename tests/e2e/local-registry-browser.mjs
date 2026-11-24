import { chromium } from '../../apps/console/node_modules/playwright/index.mjs';
const origin = process.env.REGISTRY_UI_TEST_ORIGIN;
const registry = process.env.REGISTRY_TEST_ORIGIN;
const browser = await chromium.launch({headless: true});
try {
  const page = await browser.newPage();
  const requests = [], failures = [], errors = [];
  page.on('request', request => requests.push(request.url()));
  page.on('requestfailed', request => failures.push(request.failure()?.errorText ?? 'request failed'));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.goto(origin, {waitUntil: 'networkidle'});
  await page.getByText('ringfour/acceptance', {exact: false}).first().waitFor({timeout: 20000});
  await page.screenshot({path: '/tmp/ringfour-local-registry-catalog.png', fullPage: true});
  const tagPage = await page.locator('a[href*="/taglist/ringfour/acceptance"]').first().getAttribute('href');
  await page.goto(new URL(tagPage, origin).href, {waitUntil: 'networkidle'});
  await page.getByText('v1', {exact: true}).first().waitFor({timeout: 15000});
  const body = await page.locator('body').innerText();
  if (!body.includes('v1')) throw new Error('Published tag not rendered');
  if (requests.some(url => url.startsWith(registry + '/'))) throw new Error('Browser bypassed same-origin proxy');
  if (!requests.some(url => url.startsWith(origin + '/v2/'))) throw new Error('No same-origin registry requests');
  if ([...failures, ...errors].some(text => /cors|cross.origin|access-control/i.test(text))) throw new Error('Browser CORS failure');
  await page.screenshot({path: '/tmp/ringfour-local-registry-ui.png', fullPage: true});
  console.log(JSON.stringify({browser_catalog_and_tag: true, same_origin: true, cors_errors: false}));
} finally { await browser.close(); }
