// Iridium icon, finished take A: asymmetric five-prism chrome fan on the indigo tile.
// Usage: node gen.cjs   -> ../iridium-icon.svg, ../iridium-icon-maskable.svg, ../iridium-icon-small.svg
const fs = require('fs'), path = require('path');

const r2 = n => (Math.round(n * 100) / 100).toString();
const pt = p => r2(p[0]) + ',' + r2(p[1]);
const pts = a => a.map(pt).join(' ');

// Metal palette: near-black steel blue -> blown-out chrome.
const PAL = {
  K: '#0b111f', D1: '#141c2e', D2: '#202b44', D3: '#33435f', M: '#5a7196', L1: '#93a9c9', L2: '#cfdcee', W: '#ffffff',
  V: '#a99bff', T: '#86e6de',
};
const NAVY = '#121747';

// Face reflections: [offset, colour] along the prism axis (0 = base, 1 = shoulder). Repeated offsets are hard edges.
const SCHEMES = {
  // lit steel faces: light blue-grey with blown-out bands and one dark reflection
  lit: [[0, 'M'], [0.2, 'L1'], [0.2, 'W'], [0.46, 'L2'], [0.46, 'D1'], [0.55, 'D2'], [0.55, 'W'], [0.8, 'L2'], [1, 'L1']],
  lit2: [[0, 'D3'], [0.24, 'M'], [0.24, 'L2'], [0.62, 'W'], [0.62, 'D2'], [0.7, 'D3'], [0.7, 'L1'], [1, 'L2']],
  litIris: [[0, 'D3'], [0.16, 'M'], [0.16, 'L1'], [0.44, 'L2'], [0.44, 'W'], [0.55, 'W'], [0.55, 'V'], [0.62, 'T'], [0.62, 'D1'], [0.72, 'D2'], [0.72, 'L1'], [1, 'L2']],
  // mirror-black faces with hard white bands
  mirror: [[0, 'D2'], [0.16, 'K'], [0.4, 'K'], [0.4, 'W'], [0.47, 'L2'], [0.47, 'K'], [0.66, 'D1'], [0.66, 'L1'], [0.71, 'L2'], [0.71, 'D1'], [1, 'K']],
  mirror2: [[0, 'K'], [0.27, 'D1'], [0.27, 'L2'], [0.35, 'W'], [0.35, 'K'], [0.72, 'D1'], [0.72, 'D3'], [0.8, 'M'], [0.8, 'K'], [1, 'D1']],
  mirror3: [[0, 'D1'], [0.34, 'K'], [0.53, 'K'], [0.53, 'W'], [0.62, 'W'], [0.62, 'D2'], [0.68, 'D2'], [0.68, 'K'], [1, 'D1']],
  // shaded side faces
  shade: [[0, 'K'], [0.3, 'D1'], [0.3, 'D3'], [0.42, 'D2'], [0.42, 'K'], [0.82, 'D1'], [0.82, 'D3'], [1, 'D2']],
  shade2: [[0, 'D2'], [0.5, 'D1'], [0.5, 'M'], [0.58, 'D3'], [0.58, 'K'], [1, 'D1']],
  steel: [[0, 'D2'], [0.22, 'D3'], [0.22, 'M'], [0.6, 'L1'], [0.6, 'D1'], [0.69, 'D2'], [0.69, 'M'], [1, 'D3']],
};

// A prism in local coords: u across (-0.5..0.5 of width), v along (0..1 of length).
// faces: u of the two inner edges; sh: shoulder v at the 4 edges; apex: [[u,v],[u,v]] chisel ridge (equal = point).
function prism(o, idx, defs) {
  const th = o.ang * Math.PI / 180, d = [Math.sin(th), -Math.cos(th)], n = [Math.cos(th), Math.sin(th)];
  const tb = o.taper ?? 0.85;
  const P = (u, v) => { const w = o.w * (tb + (1 - tb) * v); return [o.x + u * w * n[0] + v * o.len * d[0], o.y + u * w * n[1] + v * o.len * d[1]]; };
  const f = [-0.5, o.faces[0], o.faces[1], 0.5], sh = o.sh, [a1, a2] = o.apex;
  const B = f.map(u => P(u, 0)), S = f.map((u, i) => P(u, sh[i])), A1 = P(...a1), A2 = P(...a2);
  const chisel = a1[0] !== a2[0] || a1[1] !== a2[1];
  const sil = [B[0], S[0], A1, ...(chisel ? [A2] : []), S[3], B[3]];
  let s = '';
  const grad = (i, name) => {
    const id = `g${idx}_${i}`, tilt = ((o.tilt ?? [38, -30, 42])[i]) * Math.PI / 180;
    const uc = (f[i] + f[i + 1]) / 2, p0 = P(uc, 0), L = o.len * Math.max(sh[i], sh[i + 1]);
    const dx = d[0] * Math.cos(tilt) - d[1] * Math.sin(tilt), dy = d[0] * Math.sin(tilt) + d[1] * Math.cos(tilt);
    defs.push(`<linearGradient id="${id}" gradientUnits="userSpaceOnUse" x1="${r2(p0[0])}" y1="${r2(p0[1])}" x2="${r2(p0[0] + dx * L)}" y2="${r2(p0[1] + dy * L)}">` +
      SCHEMES[name].map(([off, c]) => `<stop offset="${off}" stop-color="${PAL[c]}"/>`).join('') + `</linearGradient>`);
    return `url(#${id})`;
  };
  // facets share exact vertices; each also carries a hairline stroke of its own paint so no antialiasing seam shows
  const facet = (p, fill) => `<polygon points="${pts(p)}" fill="${fill}" stroke="${fill}" stroke-width="0.6" stroke-linejoin="round"/>`;
  s += `<polygon points="${pts(sil)}" fill="${PAL.D1}" stroke="${PAL.K}" stroke-width="${o.sep ?? 3}" stroke-linejoin="round"/>`;
  for (let i = 0; i < 3; i++) s += facet([B[i], B[i + 1], S[i + 1], S[i]], grad(i, o.fill[i]));
  const tc = o.tip.map(c => PAL[c] || c);
  s += facet([S[0], S[1], A1], tc[0]);
  s += facet(chisel ? [S[1], S[2], A2, A1] : [S[1], S[2], A1], tc[1]);
  s += facet([S[2], S[3], A2], tc[2]);
  // lit edges: inner edges thin, the light-side outer edge strong, shoulder line
  const ln = (a, b, w, op) => `<line x1="${r2(a[0])}" y1="${r2(a[1])}" x2="${r2(b[0])}" y2="${r2(b[1])}" stroke="#fff" stroke-opacity="${op}" stroke-width="${w}"/>`;
  const hl = o.hl ?? 1;
  s += ln(B[1], S[1], 2 * hl, 0.7) + ln(B[2], S[2], 1.6 * hl, 0.4);
  s += ln(S[1], A1, 2 * hl, 0.75) + ln(S[2], A2, 1.6 * hl, 0.45);
  s += `<polyline points="${pts([S[0], S[1], S[2], S[3]])}" fill="none" stroke="#fff" stroke-opacity="0.6" stroke-width="${1.6 * hl}" stroke-linejoin="round"/>`;
  s += `<polyline points="${pts([B[0], S[0], A1, ...(chisel ? [A2] : [])])}" fill="none" stroke="#fff" stroke-opacity="0.9" stroke-width="${2.6 * hl}" stroke-linejoin="round"/>`;
  return { svg: s, sil };
}

// Faceted base: ring = outer points; inner polygon is the ring shrunk towards (cx,cy) and nudged; colours per facet.
function nugget(o) {
  const n = o.ring.length;
  const inner = o.ring.map(p => [o.cx + (p[0] - o.cx) * o.k + o.dx, o.cy + (p[1] - o.cy) * o.k + o.dy]);
  const facet = (p, c) => `<polygon points="${pts(p)}" fill="${PAL[c]}" stroke="${PAL[c]}" stroke-width="0.6" stroke-linejoin="round"/>`;
  let s = `<polygon points="${pts(o.ring)}" fill="${PAL.D1}" stroke="${PAL.K}" stroke-width="3" stroke-linejoin="round"/>`;
  for (let i = 0; i < n; i++) { const j = (i + 1) % n; s += facet([o.ring[i], o.ring[j], inner[j], inner[i]], o.cols[i % o.cols.length]); }
  s += facet(inner, o.top);
  for (let i = 0; i < n; i++) s += `<line x1="${r2(o.ring[i][0])}" y1="${r2(o.ring[i][1])}" x2="${r2(inner[i][0])}" y2="${r2(inner[i][1])}" stroke="#fff" stroke-opacity="0.35" stroke-width="1.4" stroke-linecap="round"/>`;
  s += `<polygon points="${pts(inner)}" fill="none" stroke="#fff" stroke-opacity="0.75" stroke-width="1.8" stroke-linejoin="round"/>`;
  return { svg: s, sil: o.ring };
}

// Small-size prism: two flat faces (light / dark) split at u = o.split, chisel tip, nothing else.
function simple(o) {
  const th = o.ang * Math.PI / 180, d = [Math.sin(th), -Math.cos(th)], n = [Math.cos(th), Math.sin(th)];
  const tb = o.taper ?? 0.8;
  const P = (u, v) => { const w = o.w * (tb + (1 - tb) * v); return [o.x + u * w * n[0] + v * o.len * d[0], o.y + u * w * n[1] + v * o.len * d[1]]; };
  const m = o.split, B = [P(-0.5, 0), P(m, 0), P(0.5, 0)], S = [P(-0.5, o.sh[0]), P(m, o.sh[1]), P(0.5, o.sh[2])], A = P(o.apex, 1);
  const sil = [B[0], S[0], A, S[2], B[2]];
  const [cl, cd, tl, td] = o.cols.map(c => PAL[c]);
  let s = `<polygon points="${pts(sil)}" fill="${cd}"/>`;
  s += `<polygon points="${pts([B[0], B[1], S[1], A, S[0]])}" fill="${cl}"/>`;
  s += `<polygon points="${pts([S[0], S[1], A])}" fill="${tl}"/>`;
  s += `<polygon points="${pts([S[1], S[2], A])}" fill="${td}"/>`;
  if (o.band) { const [v0, v1, k] = o.band; s += `<polygon points="${pts([P(m, v0), P(0.5, v0 + k), P(0.5, v1 + k), P(m, v1)])}" fill="#fff"/>`; }
  return { svg: s, sil };
}

const TILE = {
  defs: `<linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#7c8bf5"/><stop offset="1" stop-color="#3a44b8"/></linearGradient>` +
    `<radialGradient id="glow" cx="0.42" cy="0.45" r="0.6"><stop offset="0" stop-color="#aab4ff" stop-opacity="0.55"/><stop offset="1" stop-color="#aab4ff" stop-opacity="0"/></radialGradient>`,
  body: shape => shape('url(#bg)') + shape('url(#glow)'),
};

function build(take, maskable) {
  const defs = [];
  const parts = take.items.map((it, i) => it.ring ? nugget(it) : it.split !== undefined ? simple(it) : prism(it, i, defs));
  const all = parts.flatMap(p => p.sil);
  const xs = all.map(p => p[0]), ys = all.map(p => p[1]);
  const bb = [Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)];
  // centre the mark's bounding box and fit it to the tile (maskable: inside the r=204.8 safe circle, outline included)
  const cx = (bb[0] + bb[2]) / 2, cy = (bb[1] + bb[3]) / 2, ow = take.outline;
  const rmax = Math.max(...all.map(p => Math.hypot(p[0] - cx, p[1] - cy)));
  const sc = maskable ? take.maskR / (rmax + ow / 2) : Math.min(take.fit[0] / (bb[2] - bb[0]), take.fit[1] / (bb[3] - bb[1]));
  const k = maskable ? 0.4 : 1, ox = 256 + take.off[0] * k, oy = 256 + take.off[1] * k;
  const tf = `translate(${r2(ox)} ${r2(oy)}) scale(${r2(sc)}) translate(${r2(-cx)} ${r2(-cy)})`;
  const shape = maskable ? f => `<rect width="512" height="512" fill="${f}"/>` : f => `<rect x="16" y="16" width="480" height="480" rx="108" fill="${f}"/>`;
  const sils = parts.map(p => `<polygon points="${pts(p.sil)}"/>`).join('');
  const sh = take.shadow;
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">
  <!-- Iridium app icon${take.note}${maskable ? ' (maskable: full-bleed background, mark inside the central 80% safe zone)' : ''}. Generated by work/gen.cjs. -->
  <defs>
    ${[TILE.defs, ...defs].join('\n    ')}
  </defs>
  ${TILE.body(shape)}
  <g transform="${tf}">
    <g transform="translate(${sh[0]} ${sh[1]})" opacity="${sh[2]}" fill="${NAVY}" stroke="${NAVY}" stroke-width="${ow}" stroke-linejoin="round">${sils}</g>
    <g fill="${NAVY}" stroke="${NAVY}" stroke-width="${ow}" stroke-linejoin="round">${sils}</g>
    ${parts.map(p => p.svg).join('\n    ')}
  </g>
</svg>
`;
  return { svg, bb, sc, r: (rmax + ow / 2) * sc };
}

module.exports = { build, PAL };
if (require.main === module) {
  const { FULL, SMALL } = require('./takes.cjs');
  const root = path.join(__dirname, '..');
  const a = build(FULL, false), b = build(FULL, true), c = build(SMALL, false);
  fs.writeFileSync(path.join(root, 'iridium-icon.svg'), a.svg);
  fs.writeFileSync(path.join(root, 'iridium-icon-maskable.svg'), b.svg);
  fs.writeFileSync(path.join(root, 'iridium-icon-small.svg'), c.svg);
  console.log('full bb', a.bb.map(r2).join(' '), 'scale', r2(a.sc), '| maskable scale', r2(b.sc), 'radius', r2(b.r), '| small scale', r2(c.sc));
}
