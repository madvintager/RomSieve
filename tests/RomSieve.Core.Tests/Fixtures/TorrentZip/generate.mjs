// Generates the inputs of the TorrentZip tests (deterministic: the same files at each run) and a Logiqx DAT that
// groups them into games, from which igir writes the reference archives: see README.md.
// Usage: node generate.mjs <outDir>  (writes <outDir>/input and <outDir>/torrentzip.dat)
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

const out = process.argv[2];
const input = path.join(out, 'input');
fs.rmSync(out, { recursive: true, force: true });
fs.mkdirSync(input, { recursive: true });

let seed = 12345;
const rnd = () => { seed = (Math.imul(seed, 1103515245) + 12345) >>> 0; return seed >>> 16; };

function textLike(size) {
  const words = ['ROM', 'data', 'level', 'sprite', 'tile', 'map', 'sound', 'music', 'player', 'enemy', 'boss', 'score', '0000', 'FFFF', '\n', '  '];
  const parts = [];
  let n = 0;
  while (n < size) { const w = words[rnd() % words.length] + (rnd() % 3 === 0 ? String(rnd() % 1000) : '') + ' '; parts.push(w); n += w.length; }
  return Buffer.from(parts.join('')).subarray(0, size);
}
function random(size) { const b = Buffer.alloc(size); for (let i = 0; i < size; i++) b[i] = rnd() & 0xff; return b; }
// Literals mixed with copies of earlier data: fewer matches than literals, yet compressible by half, which is when
// DotNetZip's block truncation heuristic (absent from zlib) ends a block early
function literalsAndCopies(size) {
  const b = Buffer.alloc(size); let n = 0;
  while (n < size) {
    for (let k = 1 + (rnd() % 3); k > 0 && n < size; k--) b[n++] = 0x41 + (rnd() % 26);
    if (n > 64) { const len = 8 + (rnd() % 24); const from = rnd() % (n - 32); for (let i = 0; i < len && n < size; i++) b[n++] = b[from + i]; }
  }
  return b;
}
function pattern(prefix, size) { const b = Buffer.alloc(size); const p = Buffer.from(prefix); for (let i = 0; i < size; i++) b[i] = p[i % p.length]; return b; }

const cueTrack1 = 'Disc (1995)(Sony) (Track 1).bin';
const cueTrack2 = 'Disc (1995)(Sony) (Track 2).bin';
const games = {
  'Compressible (1990)(Test)': { 'Compressible (1990)(Test).bin': textLike(48 * 1024) },
  'Incompressible (1990)(Test)': { 'Incompressible (1990)(Test).bin': random(16 * 1024) },
  'Literals And Copies (1990)(Test)': { 'Literals And Copies (1990)(Test).bin': literalsAndCopies(64 * 1024) },
  'Empty (1990)(Test)': { 'Empty (1990)(Test).bin': Buffer.alloc(0) },
  'Mixed Case (1990)(Test)': {
    'alpha.bin': pattern('alpha', 3000),
    'Beta.bin': pattern('Beta', 3000),
    'GAMMA.bin': pattern('GAMMA', 3000),
    '_under.bin': pattern('_under', 3000),
    'Zeta.bin': pattern('Zeta', 3000),
    '[bracket].bin': pattern('[bracket]', 3000),
  },
  'Non ASCII (1990)(Test)': {
    'Café Crème (Démo).bin': pattern('cafe', 5000),
    'ゲーム (Japan).bin': pattern('game', 5000),
  },
  'Disc (1995)(Sony)': {
    'Disc (1995)(Sony).cue': Buffer.from(`FILE "${cueTrack1}" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00\r\nFILE "${cueTrack2}" BINARY\r\n  TRACK 02 AUDIO\r\n    INDEX 00 00:00:00\r\n    INDEX 01 00:02:00\r\n`),
    [cueTrack1]: Buffer.concat([textLike(8 * 2352), random(2 * 2352)]),
    [cueTrack2]: random(4 * 2352),
  },
};

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
let dat = '<?xml version="1.0"?>\n<datafile>\n\t<header>\n\t\t<name>RomSieve TorrentZip</name>\n\t\t<description>RomSieve TorrentZip</description>\n\t</header>\n';
for (const [game, roms] of Object.entries(games)) {
  dat += `\t<game name="${esc(game)}">\n\t\t<description>${esc(game)}</description>\n`;
  const dir = path.join(input, game);
  fs.mkdirSync(dir, { recursive: true });
  for (const [name, data] of Object.entries(roms)) {
    fs.writeFileSync(path.join(dir, name), data);
    const crc = (zlib.crc32(data) >>> 0).toString(16).padStart(8, '0');
    dat += `\t\t<rom name="${esc(name)}" size="${data.length}" crc="${crc}"/>\n`;
  }
  dat += '\t</game>\n';
}
dat += '</datafile>\n';
fs.writeFileSync(path.join(out, 'torrentzip.dat'), dat);
console.log('ok');
