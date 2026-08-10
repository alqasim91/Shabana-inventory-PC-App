import { test, expect, type ConsoleMessage, type Page } from '@playwright/test';

/**
 * The one test that exercises what a customer actually touches.
 *
 * The API smoke test in CI proves the STACK works - Caddy, PostgREST, GoTrue,
 * the JWT, RLS. It says nothing about the Arabic UI on top, which is where a
 * bug would now most plausibly hide. This drives a real browser through the
 * only two screens a shop owner cannot get past on their own: first-run setup,
 * and login.
 *
 * Runs against an install the CI job has already provisioned, in the state a
 * customer's machine is in the moment the installer finishes: migrated,
 * services up, nobody registered yet.
 */

/**
 * Every API call the page makes, with the response body for anything that is
 * not a 2xx. Without this a failed screen reports only "the URL never
 * changed", and the reason - a 403 from a Caddy matcher, a PostgREST error
 * code, a refused RPC - stays invisible in a browser nobody can open.
 */
function watchNetwork(page: Page): string[] {
  const calls: string[] = [];
  page.on('response', async (res) => {
    const url = res.url();
    if (!/\/rest\/v1\/|\/auth\/v1\//.test(url)) return;
    // EVERY call, not just failures. "Which requests happened at all" is the
    // question that separates "the server said no" from "the button did
    // nothing", and only one of those is a backend problem.
    let body = '';
    try { body = (await res.text()).slice(0, 300); } catch { body = '(unreadable)'; }
    calls.push(`${res.status()} ${res.request().method()} ${url.replace(/^https?:\/\/[^/]+/, '')}\n    ${body}`);
  });
  return calls;
}

/** Console errors, collected so a silently-broken screen still fails the run. */
function watchConsole(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', (msg: ConsoleMessage) => {
    if (msg.type() === 'error') errors.push(msg.text());
  });
  page.on('pageerror', (err) => errors.push(String(err)));
  return errors;
}

test('first run: setup, then log in', async ({ page }) => {
  const consoleErrors = watchConsole(page);
  const netCalls = watchNetwork(page);

  // Report what actually went wrong, rather than a bare "URL never changed".
  const explain = async (what: string) =>
    `${what}\nURL: ${page.url()}\n` +
    `API calls made:\n${netCalls.join('\n') || '  (none)'}\n` +
    `Submit button disabled: ${await page.locator('form button[type="submit"]').isDisabled().catch(() => '?')}\n` +
    `Field values: ${JSON.stringify(await page.locator('form input').evaluateAll(
      (els) => els.map((e) => (e as HTMLInputElement).value)))}\n` +
    `Console errors:\n${consoleErrors.join('\n') || '  (none)'}\n` +
    `Visible text:\n${(await page.locator('body').innerText()).slice(0, 1200)}`;

  // --- The install is unclaimed, so the app must offer setup ---------------
  // Entering at the root, the way the desktop shortcut does - not by deep
  // linking to /setup, which would skip the redirect that decides this.
  await page.goto('/');
  await expect(page).toHaveURL(/\/setup/, { timeout: 30_000 });

  // Field order on the setup form: business name, admin full name, admin
  // username, site name (text), and the two passwords.
  const texts = page.locator('form input[type="text"]');
  await expect(texts).toHaveCount(4);
  await texts.nth(0).fill('شبانة لتجارة الألوميتال');
  await texts.nth(1).fill('قاسم شبانة');
  await texts.nth(2).fill('admin');
  await texts.nth(3).fill('الفرع الرئيسي');

  const passwords = page.locator('form input[type="password"]');
  await expect(passwords).toHaveCount(2);
  await passwords.nth(0).fill('ui-smoke-password');
  await passwords.nth(1).fill('ui-smoke-password');

  // The button is disabled until pc_needs_setup() answers. Waiting for it to
  // be enabled makes "the app never decided whether it needs setup" fail here,
  // with that sentence, instead of as an unexplained timeout further down.
  const submit = page.locator('form button[type="submit"]');
  try {
    await expect(submit).toBeEnabled({ timeout: 20_000 });
  } catch {
    throw new Error(await explain('Setup submit button never became enabled.'));
  }
  await submit.click();

  // On success the setup screen sends the owner to their org's login page.
  try {
    await expect(page).toHaveURL(/\/shabana\/login/, { timeout: 30_000 });
  } catch {
    throw new Error(await explain('Setup form did not complete.'));
  }

  // --- The business name reaches the login screen -------------------------
  // Proves the org row was really written and is being read back, not just
  // that a redirect fired.
  await expect(page.getByText('شبانة لتجارة الألوميتال')).toBeVisible({ timeout: 20_000 });

  // --- Log in with the account setup just created --------------------------
  // The org comes from the URL on a PC install, so the only text field is the
  // username.
  await page.locator('form input[type="text"]').last().fill('admin');
  await page.locator('form input[type="password"]').fill('ui-smoke-password');
  await page.locator('form button[type="submit"]').click();

  // Landing anywhere that is not the login page means GoTrue issued a token,
  // the session stuck, and the protected route let us through.
  try {
    await expect(page).not.toHaveURL(/\/login/, { timeout: 30_000 });
  } catch {
    throw new Error(await explain('Login did not get past the login page.'));
  }

  // --- And the app behind the login actually renders -----------------------
  // The dashboard reads real rows through RLS. If auth.uid() were wrong the
  // page would render empty rather than error, so assert on content that only
  // exists when the queries came back.
  await expect(page.getByText('الفرع الرئيسي').first()).toBeVisible({ timeout: 30_000 });

  // Ignore the noise every app produces (favicon 404s, dev warnings); fail on
  // anything that looks like a real fault.
  const real = consoleErrors.filter(
    (e) => !/favicon|manifest|sourcemap|DevTools/i.test(e),
  );
  expect(real, `console errors:\n${real.join('\n')}`).toHaveLength(0);
});
