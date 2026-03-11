#!/usr/bin/env node
/**
 * TeamZaps screenshot automation using Playwright + Telegram Web.
 *
 * Setup (once):
 *   cd tools/screenshots
 *   npm install
 *   npx playwright install chromium
 *
 * Usage:
 *   node screenshot.js
 *
 * Workflow:
 *   1. A browser window opens. Log in if prompted (scan QR code).
 *   2. Navigate to the bot's DM chat and press Enter in this terminal.
 *   3. For each step: click the target message in the browser,
 *      then press Enter — the screenshot is saved automatically.
 *
 * Output: docs/screenshots/*.png (overwrites existing files)
 */

import { chromium } from 'playwright';
import fs from 'fs';
import path from 'path';
import readline from 'readline';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const OUTPUT_DIR = path.resolve(__dirname, '../../docs/screenshots');
const SESSION_DIR = path.resolve(__dirname, '.session');

const STEPS = [
  'step-01-session-started',
  'step-02a-joined-private',
  'step-02b-group-status',
  'step-03-lottery-entered',
  'step-04-order-placed',
  'step-05a-group-status',
  'step-05b-invoice-private',
  'step-06-invoice-received',
  'step-07-winner-drawn',
  'step-08-winner-invoice',
  'step-09-completed',
];

function ask(prompt) {
  return new Promise(resolve => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    rl.question(prompt, answer => { rl.close(); resolve(answer.trim()); });
  });
}

/** Injects a click tracker that highlights the last clicked message element. */
async function injectClickTracker(page) {
  await page.evaluate(() => {
    window.__tzEl = null;
    window.__tzPrev = null;
    document.addEventListener('click', e => {
      // Walk up to the first ancestor that is at least 60px tall and 200px wide
      // — a good heuristic for a complete message bubble, independent of class names.
      let el = e.target;
      while (el && el !== document.body) {
        const r = el.getBoundingClientRect();
        if (r.height >= 60 && r.width >= 200) break;
        el = el.parentElement;
      }
      if (!el || el === document.body) el = e.target;

      if (window.__tzPrev) window.__tzPrev.style.outline = '';
      window.__tzEl = el;
      window.__tzPrev = el;
      el.style.outline = '3px solid orange';
    }, true);
  });
}

/**
 * Advances __tzEl to the next non-label message.
 * Walks UP the DOM from __tzEl until it finds a level with 4+ children
 * (i.e. the messages list container), then picks the next non-label sibling.
 * Works regardless of Telegram Web's CSS class names or data attributes.
 */
async function advanceSelection(page) {
  return page.evaluate(() => {
    if (!window.__tzEl) return false;
    if (window.__tzPrev) window.__tzPrev.style.outline = '';

    let current = window.__tzEl;

    // A label message has a <code> child whose text starts with "step-",
    // or its innerText contains "step-" (fallback for plain-text rendering).
    const isLabel = el => {
      const code = el.querySelector('code');
      if (code && code.textContent.trim().startsWith('step-')) return true;
      return (el.innerText ?? '').includes('step-');
    };

    for (let level = 0; level < 12; level++) {
      const parent = current.parentElement;
      if (!parent || parent === document.body || parent === document.documentElement) break;

      const siblings = Array.from(parent.children);
      if (siblings.length >= 4) {
        const idx = siblings.indexOf(current);
        for (let i = idx + 1; i < siblings.length; i++) {
          const text = (siblings[i].innerText ?? '').trim();
          if (text && !isLabel(siblings[i])) {
            siblings[i].style.outline = '3px solid orange';
            siblings[i].scrollIntoView({ behavior: 'instant', block: 'nearest' });
            window.__tzEl = siblings[i];
            window.__tzPrev = siblings[i];
            return true;
          }
        }
      }
      current = parent;
    }
    return false;
  });
}

/**
 * After a manual click, walk UP from __tzEl until we reach the element that
 * lives in a parent with 4+ children — the same level advanceSelection uses.
 * This ensures the bbox logic finds the keyboard the same way on step 1 as 2–11.
 */
async function normalizeSelection(page) {
  await page.evaluate(() => {
    let el = window.__tzEl;
    // Clear outline on the originally-clicked element before we re-point the handle
    if (el) el.style.outline = '';

    for (let level = 0; level < 12; level++) {
      const parent = el?.parentElement;
      if (!parent || parent === document.body || parent === document.documentElement) break;
      if (Array.from(parent.children).length >= 4) {
        window.__tzEl = el;
        window.__tzPrev = el;
        return;
      }
      el = parent;
    }
  });
}


async function screenshotClicked(page, outPath) {
  // Just clear the outline — advanceSelection already scrolled, and for step 1
  // the user just clicked so the element is already visible.
  const ok = await page.evaluate(() => {
    const el = window.__tzEl;
    if (!el) return false;
    el.style.outline = '';
    if (window.__tzPrev) window.__tzPrev.style.outline = '';
    return true;
  });
  if (!ok) throw new Error('no message selected — click a message first');

  await page.waitForTimeout(150); // let the outline removal repaint

  const bbox = await page.evaluate(() => {
    // BFS to find the shallowest solid-background element — this gives us the
    // correct X position and width (the bubble, not the full-width row).
    const isSolid = el => {
      const bg = window.getComputedStyle(el).backgroundColor;
      return bg && bg !== 'rgba(0, 0, 0, 0)' && bg !== 'transparent';
    };

    const queue = [window.__tzEl];
    let bubble = window.__tzEl;
    while (queue.length) {
      const node = queue.shift();
      if (isSolid(node)) { bubble = node; break; }
      for (const child of node.children) queue.push(child);
    }

    // The keyboard is typically a sibling of the bubble inside a shared wrapper.
    // Use the bubble's parent's bounding box for the full height (text + keyboard),
    // but only if the parent is narrower than 90% of the viewport.
    const viewW = window.innerWidth;
    let container = bubble.parentElement ?? bubble;
    const cr = container.getBoundingClientRect();
    const use = (cr.width < viewW * 0.9) ? container : bubble;

    const r = use.getBoundingClientRect();
    if (!r || r.width < 1 || r.height < 1) return null;
    return { x: r.x, y: r.y, width: r.width, height: r.height };
  });
  if (!bbox) throw new Error('element has zero size or scrolled out of view');

  const pad = 4;
  await page.screenshot({
    path: outPath,
    clip: {
      x: Math.max(0, bbox.x - pad),
      y: Math.max(0, bbox.y - pad),
      width:  bbox.width  + pad * 2,
      height: bbox.height + pad * 2,
    },
  });
}

async function main() {
  fs.mkdirSync(SESSION_DIR, { recursive: true });
  fs.mkdirSync(OUTPUT_DIR, { recursive: true });

  console.log(`Output:  ${OUTPUT_DIR}\n`);

  const ctx = await chromium.launchPersistentContext(SESSION_DIR, {
    headless: false,
    viewport: { width: 1280, height: 900 },
    args: ['--no-sandbox', '--disable-dev-shm-usage'],
  });

  const page = ctx.pages()[0] ?? await ctx.newPage();
  await page.goto('https://web.telegram.org/a/', { waitUntil: 'domcontentloaded' });

  await ask('Log in and navigate to the bot chat, then press Enter...\n');
  await injectClickTracker(page);
  console.log('Click tracker active — selected messages are highlighted in orange.\n');

  let saved = 0, failed = 0;

  for (let i = 0; i < STEPS.length; i++) {
    const name = STEPS[i];
    const outPath = path.join(OUTPUT_DIR, `${name}.png`);

    if (i === 0) {
      // First message: user must select it manually.
      // If they click the 📸 label instead, auto-advance to the next message.
      while (true) {
        const answer = await ask(`[1/${STEPS.length}]  ${name}\n  Click the first message (or its 📸 label), then press Enter  (or "skip"):\n  > `);
        if (answer.toLowerCase() === 'skip') { failed++; break; }
        // If user clicked the label, silently advance to the actual content message
        const isLabel = await page.evaluate(() => {
          const el = window.__tzEl;
          if (!el) return false;
          const code = el.querySelector('code');
          if (code && code.textContent.trim().startsWith('step-')) return true;
          return (el.innerText ?? '').includes('step-');
        });
        if (isLabel) await advanceSelection(page);
        else await normalizeSelection(page);
        try {
          await screenshotClicked(page, outPath);
          console.log(`  ✓ saved ${name}.png\n`);
          saved++;
          break;
        } catch (err) {
          console.log(`  ✗ ${err.message} — try again\n`);
        }
      }
    } else {
      // Subsequent messages: auto-advance to next sibling, skip label bubbles
      process.stdout.write(`  [${i + 1}/${STEPS.length}]  ${name} ... `);
      const advanced = await advanceSelection(page);
      if (!advanced) {
        // DOM ran out of siblings (virtual scroll) — fall back to manual click
        console.log('sibling not found, manual selection needed');
        while (true) {
          const answer = await ask(`  Click the message for ${name}, then press Enter  (or "skip"):\n  > `);
          if (answer.toLowerCase() === 'skip') { failed++; break; }
          try {
            await screenshotClicked(page, outPath);
            console.log(`  ✓ saved ${name}.png\n`);
            saved++;
            break;
          } catch (err) {
            console.log(`  ✗ ${err.message} — try again\n`);
          }
        }
        await injectClickTracker(page).catch(() => {});
        continue;
      }

      await page.waitForTimeout(250); // let layout settle after scroll
      try {
        await screenshotClicked(page, outPath);
        console.log('✓');
        saved++;
      } catch (err) {
        console.log(`✗  ${err.message}`);
        failed++;
      }
    }
  }

  await ctx.close();
  console.log(`\n${saved} saved, ${failed} failed → ${OUTPUT_DIR}`);
}

main().catch(err => { console.error(err); process.exit(1); });
