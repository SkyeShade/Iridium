// Mark definitions for gen.cjs. Items are drawn back to front.
// prism: x,y base centre; ang degrees from vertical (negative leans left); len; w; taper (base width / tip width);
//   faces [u1,u2]; sh shoulder v at the 4 edges; apex chisel ridge [[u,v],[u,v]]; fill 3 schemes; tip 3 colours.
const ring = (cx, cy, rx, ry, rs, rot = 0) => rs.map((r, i) => { const a = rot + i / rs.length * Math.PI * 2; return [cx + Math.cos(a) * rx * r, cy + Math.sin(a) * ry * r]; });

// Finished take A: the same asymmetric five-prism fan, prisms ~25% thicker, darker mirror faces.
const FULL = {
  note: ': a fan of mirror-chrome iridium crystals', fit: [416, 384], off: [0, 4], maskR: 200, outline: 9, shadow: [9, 13, 0.3],
  items: [
    { x: 232, y: 372, ang: -55, len: 262, w: 72, faces: [-0.1, 0.3], sh: [0.8, 0.87, 0.85, 0.75], apex: [[-0.1, 1], [0.12, 0.98]], fill: ['lit', 'mirror', 'shade'], tip: ['W', 'L1', 'D2'] },
    { x: 262, y: 382, ang: -26, len: 350, w: 116, faces: [-0.06, 0.3], sh: [0.74, 0.83, 0.8, 0.68], apex: [[-0.12, 1], [0.16, 0.97]], fill: ['litIris', 'mirror', 'shade2'], tip: ['L2', 'D2', 'K'], tilt: [24, -16, 20] },
    { x: 350, y: 380, ang: 22, len: 276, w: 78, faces: [-0.16, 0.22], sh: [0.78, 0.86, 0.84, 0.76], apex: [[-0.05, 1], [0.1, 0.99]], fill: ['steel', 'mirror2', 'lit2'], tip: ['W', 'L1', 'D2'] },
    { x: 380, y: 394, ang: 49, len: 168, w: 60, faces: [-0.15, 0.25], sh: [0.7, 0.8, 0.78, 0.7], apex: [[0, 1], [0, 1]], fill: ['lit2', 'steel', 'shade'], tip: ['W', 'M', 'D1'] },
    { x: 308, y: 398, ang: -3, len: 256, w: 88, faces: [-0.14, 0.24], sh: [0.72, 0.82, 0.8, 0.7], apex: [[-0.1, 1], [0.14, 0.98]], fill: ['mirror3', 'lit', 'shade'], tip: ['L2', 'W', 'D3'], tilt: [-22, 22, -18] },
    { ring: ring(310, 410, 112, 46, [1, 0.9, 1.05, 0.85, 1, 0.92, 1.08, 0.88, 1]), cx: 310, cy: 410, k: 0.55, dx: -8, dy: -8, cols: ['D2', 'K', 'D3', 'M', 'L2', 'W', 'L1', 'D3', 'K'], top: 'L1' },
  ],
};

// Small sizes: three bold two-tone prisms, no base.
const SMALL = {
  note: ', small-size variant (16-32 px): three two-tone prisms', fit: [400, 400], off: [0, 0], maskR: 200, outline: 20, shadow: [0, 0, 0],
  items: [
    { x: 296, y: 404, ang: 37, len: 270, w: 112, taper: 0.72, split: 0.1, sh: [0.7, 0.78, 0.74], apex: 0.1, cols: ['L2', 'K', 'W', 'D3'] },
    { x: 226, y: 400, ang: -33, len: 420, w: 140, taper: 0.7, split: 0.1, sh: [0.74, 0.82, 0.76], apex: 0.1, cols: ['W', 'K', 'L2', 'D3'] },
    { x: 264, y: 424, ang: 3, len: 320, w: 124, taper: 0.72, split: 0.1, sh: [0.72, 0.8, 0.74], apex: 0.1, cols: ['W', 'K', 'L2', 'D3'] },
  ],
};

module.exports = { FULL, SMALL };
