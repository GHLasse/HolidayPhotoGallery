// Renders the installer artwork (sunset, map, suitcase, island) into PNG layers in installer/art/png.
// The installer stacks and animates these layers itself (see the [Code] section of HolidayPhotoGallery.iss).
// Everything is drawn at twice its size, so it stays sharp on high-DPI screens.
//
// Run after changing the artwork:  node installer/art/render-art.mjs
// (needs Playwright with Chromium: npm install playwright, npx playwright install chromium)

import { chromium } from 'playwright';
import { mkdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const out = join(dirname(fileURLToPath(import.meta.url)), 'png');
const SCALE = 2;

const PLANE = 'M12 1.5c.8 0 1.4 1 1.4 2.3V9l8.6 5.2V16l-8.6-2.6V19l2.4 1.8V22.5L12 21.4 8.2 22.5V20.8L10.6 19V13.4L2 16V14.2L10.6 9V3.8C10.6 2.5 11.2 1.5 12 1.5z';

const DEFS = `
  <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFB27A"/><stop offset=".5" stop-color="#FF6B6B"/><stop offset="1" stop-color="#C2508F"/></linearGradient>
  <linearGradient id="sea" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2D7FD3"/><stop offset="1" stop-color="#173F86"/></linearGradient>
  <linearGradient id="sun" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFF3B0"/><stop offset="1" stop-color="#FFC94D"/></linearGradient>
  <linearGradient id="teal" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#38C3B5"/><stop offset="1" stop-color="#16788F"/></linearGradient>
  <linearGradient id="warm" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFD9A8"/><stop offset="1" stop-color="#FF9E7A"/></linearGradient>`;

// Wave dashes repeat every PERIOD px, so a strip PERIOD wider than the view can scroll forever.
const PERIOD = 120;
function waves(width, rows) {
  let d = '';
  for (const [y, dashes] of rows)
    for (let k = -PERIOD; k < width + PERIOD; k += PERIOD)
      for (const [a, b] of dashes) d += `M${k + a} ${y}h${b - a}`;
  return `<path d="${d}" stroke="#FFFFFF" stroke-opacity=".5" stroke-width="4" stroke-linecap="round" fill="none"/>`;
}
const seaStrip = (width, height, rows) =>
  `<rect width="${width}" height="${height}" fill="url(#sea)"/>${waves(width, rows)}`;

function planeAt(x, y, angle, size, color = '#FFFFFF') {
  // The plane path points up; angle 0 = flying right.
  const s = size / 24;
  return `<g transform="translate(${x} ${y}) rotate(${angle + 90}) scale(${s}) translate(-12 -12)"><path d="${PLANE}" fill="${color}"/></g>`;
}

const pin = `<path d="M0 0 C -8 -14 -22 -26 -22 -42 A 22 22 0 0 1 22 -42 C 22 -26 8 -14 0 0 Z" fill="#FF6B6B" stroke="#FFFFFF" stroke-width="3"/><circle cx="0" cy="-42" r="8" fill="#FFFFFF"/>`;

const assets = [];
const add = (name, w, h, body, alpha = false) => assets.push({ name, w, h, body, alpha });

// ---- 1 · Welcome: sky with sun and contrail; the sea strip and the plane are separate layers.
add('welcome-sky', 280, 508, `
  <rect width="280" height="508" fill="url(#sky)"/>
  <circle cx="140" cy="350" r="78" fill="#FFD46B" opacity=".25"/>
  <circle cx="140" cy="350" r="58" fill="url(#sun)"/>
  <path d="M-10 250 Q 110 228 196 140" stroke="#FFFFFF" stroke-opacity=".85" stroke-width="4" stroke-linecap="round" fill="none"/>`);
add('welcome-sea', 280 + PERIOD, 158, seaStrip(280 + PERIOD, 158,
  [[30, [[10, 50], [70, 110]]], [60, [[30, 90]]], [94, [[0, 30], [62, 100]]], [128, [[40, 100]]]]));
add('welcome-plane', 64, 64, planeAt(32, 32, -38, 53), true);

// ---- 2 · Map: background, the flat map (folded by the installer), the pin and its pulse.
add('map-bg', 280, 508, `<rect width="280" height="508" fill="url(#warm)"/>`);
add('map-flat', 228, 260, `<g transform="translate(-26 -131)">
  <rect x="0" y="100" width="280" height="320" fill="#BFE1F7"/>
  <path d="M20 300 C 60 260 70 200 120 190 C 160 182 150 140 200 130 L 270 120 L 270 250 C 230 250 220 290 240 330 C 256 362 230 410 200 410 L 20 410 Z" fill="#FFE2B8"/>
  <path d="M20 308 C 58 270 74 212 122 200 C 162 190 156 150 204 140 L 270 130 L 270 244 C 224 246 210 292 230 332 C 246 364 222 410 196 410 L 20 410 Z" fill="url(#teal)"/>
  <path d="M70 170 C 84 160 100 168 96 180 C 92 192 74 190 70 182 Z" fill="url(#teal)"/>
  <path d="M26 201h228M26 271h228M26 341h228" stroke="#FFFFFF" stroke-opacity=".4" stroke-width="1"/>
  <g transform="translate(62 356)">
    <circle r="17" fill="#FFFBF6" opacity=".92"/>
    <path d="M0 -13 L4 0 L0 13 L-4 0 Z" fill="#FF6B6B"/>
    <path d="M0 13 L4 0 L-4 0 Z" fill="#14213D" opacity=".6"/>
    <circle r="1.6" fill="#14213D"/>
  </g></g>`);
// Pin: 52x54 box, tip at (26, 50).
add('map-pin', 52, 72, `<g transform="translate(26 68)">${pin}</g>`, true);
// Pulse ring around the pin's tip: 16 frames, 72x32 box centred on the tip.
for (let f = 0; f < 16; f++) {
  const p = f / 16, s = 0.4 + 1.4 * p, o = 0.9 * (1 - p);
  add(`map-pulse-${String(f).padStart(2, '0')}`, 72, 32, `
    <ellipse cx="36" cy="16" rx="8" ry="3" fill="#0B2A5C" opacity=".3"/>
    <ellipse cx="36" cy="16" rx="${18 * s}" ry="${7 * s}" fill="none" stroke="#14213D" stroke-opacity="${(0.5 * o).toFixed(3)}" stroke-width="2"/>`, true);
}

// ---- 3 · Packing: suitcase with a luggage tag; 0 to 3 ticks.
function suitcase(ticks) {
  const tick = (y, show) => show ? `<path d="M206 ${y} l5 5 l9 -10" stroke="#16788F" stroke-width="3" fill="none" stroke-linecap="round" stroke-linejoin="round"/>` : '';
  return `
  <rect width="280" height="508" fill="url(#warm)"/>
  <rect x="0" y="400" width="280" height="108" fill="#F2B58C"/>
  <ellipse cx="140" cy="402" rx="104" ry="10" fill="#B8664A" opacity=".3"/>
  <path d="M112 186 v-22 a12 12 0 0 1 12 -12 h32 a12 12 0 0 1 12 12 v22" stroke="#0F5A6C" stroke-width="10" fill="none" stroke-linecap="round"/>
  <rect x="44" y="184" width="192" height="214" rx="22" fill="url(#teal)"/>
  <rect x="44" y="184" width="192" height="214" rx="22" fill="none" stroke="#0F5A6C" stroke-opacity=".35" stroke-width="2"/>
  <rect x="84" y="184" width="14" height="214" fill="#0F5A6C" opacity=".35"/>
  <rect x="182" y="184" width="14" height="214" fill="#0F5A6C" opacity=".35"/>
  <circle cx="140" cy="268" r="28" fill="#FFFFFF" opacity=".95"/>
  <clipPath id="badge"><circle cx="140" cy="268" r="26"/></clipPath>
  <g clip-path="url(#badge)"><circle cx="140" cy="276" r="13" fill="url(#sun)"/><rect x="110" y="276" width="60" height="20" fill="#2D7FD3"/></g>
  <circle cx="140" cy="268" r="28" fill="none" stroke="#FFFFFF" stroke-width="4"/>
  <g transform="translate(116 340) rotate(-8)">
    <rect x="-26" y="-14" width="52" height="28" rx="6" fill="#FF6B6B"/>
    ${planeAt(0, 0, 0, 20)}
  </g>
  <circle cx="76" cy="404" r="9" fill="#14213D"/>
  <circle cx="204" cy="404" r="9" fill="#14213D"/>
  <path d="M196 212 C 214 214 222 226 220 240" stroke="#14213D" stroke-width="2" fill="none"/>
  <rect x="196" y="238" width="68" height="92" rx="8" fill="#FFFBF6" stroke="#E2D5C6" stroke-width="2"/>
  <circle cx="230" cy="250" r="4" fill="#E2D5C6"/>
  ${tick(270, ticks > 0)}${tick(292, ticks > 1)}${tick(314, ticks > 2)}
  <path d="M226 270h28M226 292h22M226 314h26" stroke="#B9AE9F" stroke-width="3" stroke-linecap="round"/>`;
}
for (let n = 0; n <= 3; n++) add(`ready-${n}`, 280, 508, suitcase(n));
// Tick for the packing list beside the suitcase (loaded as PNG by the page itself).
add('ready-check', 22, 22, `<circle cx="11" cy="11" r="11" fill="#16788F"/><path d="M6.5 11.2l3 3 6-6.4" stroke="#FFFFFF" stroke-width="2.2" fill="none" stroke-linecap="round" stroke-linejoin="round"/>`, true);
add('ready-empty', 22, 22, `<circle cx="11" cy="11" r="9.8" fill="none" stroke="#9AA3B2" stroke-width="2.2"/>`, true);

// ---- 4 · Installing: sky, rising sun, contrail (revealed up to the plane), sea strip, plane at several angles.
const FLIGHT = 'M -30 250 Q 330 60 690 150';
add('fly-sky', 718, 250, `
  <rect width="718" height="250" fill="url(#sky)"/>
  <path d="${FLIGHT}" pathLength="100" stroke="#FFFFFF" stroke-opacity=".35" stroke-width="2" stroke-dasharray="1.2 1.6" fill="none"/>`);
add('fly-sun', 190, 190, `
  <circle cx="95" cy="95" r="92" fill="#FFD46B" opacity=".22"/>
  <circle cx="95" cy="95" r="66" fill="url(#sun)"/>`, true);
add('fly-trail', 718, 250, `<path d="${FLIGHT}" stroke="#FFFFFF" stroke-opacity=".9" stroke-width="4" stroke-linecap="round" fill="none"/>`, true);
add('fly-sea', 718 + PERIOD, 90, seaStrip(718 + PERIOD, 90,
  [[24, [[30, 90], [100, 115]]], [50, [[10, 60], [80, 110]]], [74, [[50, 100]]]]));
// The flight path turns from about -28° to +14°: one frame per 2°, 52x52 box centred on the plane.
for (let a = -30; a <= 16; a += 2)
  add(`fly-plane-${String(a + 30).padStart(2, '0')}`, 52, 52, planeAt(26, 26, a, 44), true);

// ---- 5 · Finished: sky with the setting sun, sea strip, ripples, the island and a swaying palm.
add('island-sky', 280, 300, `
  <rect width="280" height="300" fill="url(#sky)"/>
  <circle cx="70" cy="300" r="74" fill="#FFD46B" opacity=".25"/>
  <circle cx="70" cy="300" r="50" fill="url(#sun)"/>`);
add('island-sea', 280 + PERIOD, 208, seaStrip(280 + PERIOD, 208,
  [[26, [[60, 110]]], [160, [[10, 70], [90, 110]]], [194, [[30, 90]]]]));
// Ripples: 24 frames of a 4 s cycle, two rings half a cycle apart; box y 356..452 of the scene.
for (let f = 0; f < 24; f++) {
  const ring = (p) => {
    const s = 0.7 + 0.75 * p;
    return `<ellipse cx="160" cy="48" rx="${112 * s}" ry="${26 * s}" fill="none" stroke="#FFFFFF" stroke-opacity="${(0.42 * (1 - p)).toFixed(3)}" stroke-width="2"/>`;
  };
  const p = f / 24;
  add(`island-ripple-${String(f).padStart(2, '0')}`, 280, 96, ring(p) + ring((p + 0.5) % 1), true);
}
// Island and sun reflection, scene y 300..420.
add('island-sand', 280, 120, `<g transform="translate(0 -300)">
  <path d="M44 316h52M58 330h24" stroke="#FFE08A" stroke-opacity=".6" stroke-width="4" stroke-linecap="round"/>
  <path d="M70 404 C 90 370 230 370 250 404 Z" fill="#FFE2B8"/>
  <ellipse cx="160" cy="404" rx="92" ry="10" fill="#E9C08E"/></g>`, true);
// Palm: 13 frames from -3° to +3° around the foot of the trunk (206, 392); box x 120..260, y 240..400.
for (let i = 0; i <= 12; i++) {
  const a = -3 + i * 0.5;
  add(`island-palm-${String(i).padStart(2, '0')}`, 140, 160, `<g transform="translate(-120 -240) rotate(${a} 206 392)">
    <path d="M206 392 C 208 360 200 330 190 306" stroke="#7A4A2A" stroke-width="6" stroke-linecap="round" fill="none"/>
    <path d="M190 306 C 170 296 150 304 140 318 C 160 306 176 308 190 306 Z M190 306 C 196 286 216 280 232 288 C 214 290 200 296 190 306 Z M190 306 C 206 296 230 304 238 320 C 220 308 204 306 190 306 Z M190 306 C 176 290 176 270 188 260 C 184 278 186 292 190 306 Z" fill="url(#teal)"/>
    <circle cx="186" cy="312" r="4" fill="#7A4A2A"/><circle cx="194" cy="314" r="4" fill="#7A4A2A"/></g>`, true);
}

rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });
const browser = await chromium.launch();
const page = await browser.newPage({ deviceScaleFactor: SCALE });
for (const a of assets) {
  await page.setViewportSize({ width: a.w, height: a.h });
  await page.setContent(`<html><body style="margin:0;background:transparent">
    <svg xmlns="http://www.w3.org/2000/svg" width="${a.w}" height="${a.h}" viewBox="0 0 ${a.w} ${a.h}" style="display:block"><defs>${DEFS}</defs>${a.body}</svg></body></html>`);
  await page.locator('svg').screenshot({ path: join(out, `${a.name}.png`), omitBackground: a.alpha });
}
await browser.close();
console.log(`${assets.length} layers written to ${out}`);
