// analiza.mjs – wykresy SVG i tabele Markdown z wyników Runnera.
//
// Użycie: node analiza\analiza.mjs <folder wyników> [folder wyjściowy]
//   <folder wyników>   – np. bin\wyniki_20261110_070500 (pliki K1_/K2_/K3_podsumowanie.csv)
//   [folder wyjściowy] – domyślnie <folder wyników>\analiza
// Tworzy: wykres_*.svg (do dokumentacji) i tabele.md (kluczowe liczby, mediany, C++ = 1,00).
import fs from 'node:fs';
import path from 'node:path';

const [, , inDir, outArg] = process.argv;
if (!inDir) {
  console.error('Użycie: node analiza.mjs <folder wyników> [folder wyjściowy]');
  process.exit(1);
}
const outDir = outArg ?? path.join(inDir, 'analiza');
fs.mkdirSync(outDir, { recursive: true });

const LANGS = ['C++', 'C#', 'Java'];
// Paleta kategoryczna (3 pierwsze sloty – rozróżnialne także przy zaburzeniach widzenia barw).
const COLOR = { 'C++': '#2a78d6', 'C#': '#eb6834', Java: '#1baf7a' };
const INK = '#0b0b0b', INK2 = '#52514e', GRID = '#e4e3df', SURFACE = '#ffffff';

// ---------- CSV (separator ;, przecinek dziesiętny, BOM) ----------
function readCsv(file) {
  const p = path.join(inDir, file);
  if (!fs.existsSync(p)) return [];
  const lines = fs.readFileSync(p, 'utf8').replace(/^﻿/, '').split(/\r?\n/).filter(Boolean);
  const head = lines[0].split(';');
  return lines.slice(1).map(l => {
    const cells = l.split(';');
    const row = {};
    head.forEach((h, i) => {
      const v = cells[i] ?? '';
      const n = Number(v.replace(',', '.'));
      row[h] = v !== '' && !Number.isNaN(n) ? n : v;
    });
    return row;
  });
}
const k1 = readCsv('K1_podsumowanie.csv');
const k2 = readCsv('K2_podsumowanie.csv');
const k3 = readCsv('K3_podsumowanie.csv');

const pl = (x, d = 2) => (x === undefined || x === null || Number.isNaN(x)) ? '—'
  : x.toLocaleString('pl-PL', { minimumFractionDigits: d, maximumFractionDigits: d });
const find = (rows, q) => rows.find(r => Object.entries(q).every(([k, v]) => r[k] === v));
const k3v = (q, metric) => find(k3, { ...q, Metryka: metric })?.['Wartość'];

// ---------- wykres liniowy SVG ----------
function lineChart({ title, xLabel, yLabel, xLog, yLog, series, refLine, xTicks, yTicks, file, legendExtra }) {
  const W = 760, H = 440, m = { l: 70, r: 120, t: 48, b: 58 };
  const pw = W - m.l - m.r, ph = H - m.t - m.b;
  const xs = series.flatMap(s => s.points.map(p => p[0]));
  const ys = series.flatMap(s => s.points.map(p => p[1])).concat(refLine ? refLine.flat().filter((_, i) => i % 2) : []);
  const xMin = Math.min(...xTicks), xMax = Math.max(...xTicks);
  const yMin = yLog ? Math.min(...yTicks) : 0, yMax = Math.max(...yTicks, ...ys);
  const f = (v, lo, hi, log) => log ? (Math.log(v) - Math.log(lo)) / (Math.log(hi) - Math.log(lo)) : (v - lo) / (hi - lo);
  const X = v => m.l + f(v, xMin, xMax, xLog) * pw;
  const Y = v => m.t + ph - f(v, yMin, yMax, yLog) * ph;
  let s = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" font-family="Segoe UI, Arial, sans-serif">`;
  s += `<rect width="${W}" height="${H}" fill="${SURFACE}"/>`;
  s += `<text x="${m.l}" y="26" font-size="17" font-weight="600" fill="${INK}">${title}</text>`;
  for (const t of yTicks) {
    s += `<line x1="${m.l}" x2="${m.l + pw}" y1="${Y(t)}" y2="${Y(t)}" stroke="${GRID}"/>`;
    s += `<text x="${m.l - 8}" y="${Y(t) + 4}" font-size="12" fill="${INK2}" text-anchor="end">${t.label ?? pl(+t, Number.isInteger(+t) ? 0 : 1)}</text>`;
  }
  for (const t of xTicks) {
    s += `<text x="${X(t)}" y="${m.t + ph + 18}" font-size="12" fill="${INK2}" text-anchor="middle">${t.label ?? t}</text>`;
  }
  s += `<line x1="${m.l}" x2="${m.l + pw}" y1="${m.t + ph}" y2="${m.t + ph}" stroke="${INK2}"/>`;
  s += `<text x="${m.l + pw / 2}" y="${H - 14}" font-size="13" fill="${INK2}" text-anchor="middle">${xLabel}</text>`;
  s += `<text transform="translate(18 ${m.t + ph / 2}) rotate(-90)" font-size="13" fill="${INK2}" text-anchor="middle">${yLabel}</text>`;
  if (refLine) {
    s += `<polyline points="${refLine.map(([x, y]) => `${X(x)},${Y(Math.min(y, yMax))}`).join(' ')}" fill="none" stroke="#9a9893" stroke-width="1.5" stroke-dasharray="2 4"/>`;
  }
  for (const se of series) {
    const pts = se.points.map(([x, y]) => `${X(x)},${Y(y)}`).join(' ');
    s += `<polyline points="${pts}" fill="none" stroke="${se.color}" stroke-width="2" ${se.dash ? 'stroke-dasharray="6 4"' : ''}/>`;
    for (const [x, y] of se.points)
      s += `<circle cx="${X(x)}" cy="${Y(y)}" r="4" fill="${se.dash ? SURFACE : se.color}" stroke="${se.dash ? se.color : SURFACE}" stroke-width="2"/>`;
  }
  // legenda: kolor = język, styl linii = wariant
  let ly = m.t + 6;
  for (const l of LANGS) {
    s += `<line x1="${m.l + pw + 16}" x2="${m.l + pw + 40}" y1="${ly}" y2="${ly}" stroke="${COLOR[l]}" stroke-width="3"/>`;
    s += `<text x="${m.l + pw + 46}" y="${ly + 4}" font-size="13" fill="${INK}">${l}</text>`;
    ly += 22;
  }
  ly += 8;
  for (const [label, dash] of legendExtra ?? []) {
    s += `<line x1="${m.l + pw + 16}" x2="${m.l + pw + 40}" y1="${ly}" y2="${ly}" stroke="${INK2}" stroke-width="2" ${dash ? `stroke-dasharray="${dash}"` : ''}/>`;
    s += `<text x="${m.l + pw + 46}" y="${ly + 4}" font-size="12" fill="${INK2}">${label}</text>`;
    ly += 20;
  }
  s += '</svg>';
  fs.writeFileSync(path.join(outDir, file), s);
}

// ---------- wykres słupkowy (poziomy, grupy × języki) ----------
function barChart({ title, unit, groups, file, digits = 0 }) {
  const rowH = 22, gap = 2, groupGap = 18, W = 760, m = { l: 150, r: 90, t: 48, b: 46 };
  const H = m.t + m.b + groups.length * (LANGS.length * (rowH + gap) + groupGap);
  const max = Math.max(...groups.flatMap(g => LANGS.map(l => g.values[l] ?? 0)));
  const pw = W - m.l - m.r;
  let s = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" font-family="Segoe UI, Arial, sans-serif">`;
  s += `<rect width="${W}" height="${H}" fill="${SURFACE}"/>`;
  s += `<text x="16" y="26" font-size="17" font-weight="600" fill="${INK}">${title}</text>`;
  let y = m.t;
  for (const g of groups) {
    s += `<text x="16" y="${y + (LANGS.length * (rowH + gap)) / 2 + 4}" font-size="13" fill="${INK}">${g.label}</text>`;
    for (const l of LANGS) {
      const v = g.values[l];
      if (v !== undefined) {
        const w = Math.max(2, (v / max) * pw);
        s += `<path d="M${m.l},${y} h${w - 4} q4,0 4,4 v${rowH - 8} q0,4 -4,4 h${-(w - 4)} z" fill="${COLOR[l]}"/>`;
        s += `<text x="${m.l + w + 6}" y="${y + rowH / 2 + 4}" font-size="12" fill="${INK}">${l}: ${pl(v, digits)}</text>`;
      }
      y += rowH + gap;
    }
    y += groupGap;
  }
  s += `<line x1="${m.l}" x2="${m.l}" y1="${m.t - 4}" y2="${y - groupGap + 2}" stroke="${INK2}"/>`;
  s += `<text x="${m.l}" y="${H - 16}" font-size="12" fill="${INK2}">${unit}</text>`;
  s += '</svg>';
  fs.writeFileSync(path.join(outDir, file), s);
}

const md = [];
const table = (head, rows) => {
  md.push(`| ${head.join(' | ')} |`, `|${head.map(() => '---').join('|')}|`);
  rows.forEach(r => md.push(`| ${r.join(' | ')} |`));
  md.push('');
};

// ================= K1 =================
if (k1.length) {
  const sizes = [...new Set(k1.filter(r => r.Zadanie === 'mem-access').map(r => r.Rozmiar))].sort((a, b) => a - b);
  const series = [];
  for (const v of ['rand', 'seq'])
    for (const l of LANGS) {
      const pts = sizes.map(kb => [kb, find(k1, { Zadanie: 'mem-access', Wariant: v, Rozmiar: kb, Flush: 1, 'Język': l })?.['Wartość_metryki']])
        .filter(p => p[1] !== undefined);
      if (pts.length) series.push({ color: COLOR[l], dash: v === 'seq', points: pts });
    }
  const kbLabel = kb => kb >= 1024 ? `${kb / 1024} MB` : `${kb} KB`;
  const yT = [0.05, 0.1, 1, 10, 100].map(v => Object.assign(new Number(v), { label: pl(v, v < 1 ? 2 : 0) }));
  lineChart({
    title: 'Z1.1 Dostęp do pamięci – ns/dostęp (flush on, skala log.)',
    xLabel: 'rozmiar tablicy (L1 48 KB · L2 1,25 MB · L3 24 MB)', yLabel: 'ns / dostęp',
    xLog: true, yLog: true, series,
    xTicks: sizes.map(kb => Object.assign(new Number(kb), { label: kbLabel(kb) })), yTicks: yT,
    file: 'wykres_K1_mem_access.svg', legendExtra: [['rand (losowy)', null], ['seq (sekw.)', '6 4']],
  });

  md.push('## K1 – Z1.1 ns/dostęp (flush on)', '');
  table(['Rozmiar', ...LANGS.map(l => `${l} seq`), ...LANGS.map(l => `${l} rand`)],
    sizes.map(kb => [kbLabel(kb), ...['seq', 'rand'].flatMap(v => LANGS.map(l => pl(find(k1, { Zadanie: 'mem-access', Wariant: v, Rozmiar: kb, Flush: 1, 'Język': l })?.['Wartość_metryki'])))]));

  md.push('## K1 – Z1.2 GFLOP/s (flush on)', '');
  const ns = [...new Set(k1.filter(r => r.Zadanie === 'matmul').map(r => r.Rozmiar))].sort((a, b) => a - b);
  table(['N', ...LANGS.map(l => `${l} ijk`), ...LANGS.map(l => `${l} ikj`)],
    ns.map(n => [n, ...['ijk', 'ikj'].flatMap(v => LANGS.map(l => pl(find(k1, { Zadanie: 'matmul', Wariant: v, Rozmiar: n, Flush: 1, 'Język': l })?.['Wartość_metryki'])))]));

  md.push('## K1 – Z1.3 obiekty: mediana [ms] / szczyt WS [MB] / GC na iterację / pauzy GC [ms]', '');
  const objs = k1.filter(r => r.Zadanie === 'objects' && r['Język'] === 'C++');
  table(['Konfiguracja', ...LANGS],
    objs.map(o => [`${o.Wariant} ${o.Rozmiar}`, ...LANGS.map(l => {
      const r = find(k1, { Zadanie: 'objects', Wariant: o.Wariant, Rozmiar: o.Rozmiar, 'Język': l });
      return r ? `${pl(r.Mediana_ms, 1)} ms / ${pl(r.Szczyt_WS_MB, 0)} MB / ${pl(r.GC_liczba, 1)} GC / ${pl(r.GC_pauza_ms, 1)} ms` : '—';
    })]));
}

// ================= K2 =================
if (k2.length) {
  const threads = [...new Set(k2.filter(r => r.Zadanie === 'mandelbrot').map(r => r['Wątki']))].sort((a, b) => a - b);
  const series = [];
  for (const v of ['dynamic', 'static'])
    for (const l of LANGS) {
      const pts = threads.map(t => [t, find(k2, { Zadanie: 'mandelbrot', Wariant: v, 'Wątki': t, 'Język': l })?.Przyspieszenie]).filter(p => p[1] !== undefined);
      if (pts.length) series.push({ color: COLOR[l], dash: v === 'static', points: pts });
    }
  const maxT = Math.max(...threads);
  lineChart({
    title: 'Z2.1 Mandelbrot 2000² – przyspieszenie T(1)/T(n)',
    xLabel: 'liczba wątków (6 = rdzenie P, 14 = rdzenie fizyczne, 20 = wątki logiczne)', yLabel: 'przyspieszenie',
    xLog: false, yLog: false, series, refLine: [[1, 1], [Math.min(maxT, 14), Math.min(maxT, 14)]],
    xTicks: threads, yTicks: [0, 2, 4, 6, 8, 10, 12, 14].map(v => new Number(v)),
    file: 'wykres_K2_mandelbrot.svg', legendExtra: [['dynamic', null], ['static', '6 4'], ['ideał (n)', '2 4']],
  });

  md.push('## K2 – Z2.1 Mandelbrot: mediana [ms] / przyspieszenie', '');
  table(['Wątki', ...['static', 'dynamic'].flatMap(v => LANGS.map(l => `${l} ${v}`))],
    threads.map(t => [t, ...['static', 'dynamic'].flatMap(v => LANGS.map(l => {
      const r = find(k2, { Zadanie: 'mandelbrot', Wariant: v, 'Wątki': t, 'Język': l });
      return r ? `${pl(r.Mediana_ms, 0)} / ${pl(r.Przyspieszenie, 2)}×` : '—';
    }))]));

  md.push('## K2 – Z2.2 ns/operację', '');
  const st = [...new Set(k2.filter(r => r.Zadanie === 'sync').map(r => r['Wątki']))].sort((a, b) => a - b);
  for (const v of ['mutex', 'atomic', 'local-shared', 'local-padded']) {
    md.push(`**${v}**`, '');
    table(['Wątki', ...LANGS], st.map(t => [t, ...LANGS.map(l => pl(find(k2, { Zadanie: 'sync', Wariant: v, 'Wątki': t, 'Język': l })?.['Wartość_metryki']))]));
  }
  const syncThreads = st.includes(6) ? 6 : st[Math.floor(st.length / 2)];
  barChart({
    title: `Z2.2 Koszt synchronizacji – ns/operację przy ${syncThreads} wątkach (mniej = lepiej)`, unit: 'ns / operację', digits: 1,
    groups: ['mutex', 'atomic', 'local-shared', 'local-padded'].map(v => ({
      label: v, values: Object.fromEntries(LANGS.map(l => [l, find(k2, { Zadanie: 'sync', Wariant: v, 'Wątki': syncThreads, 'Język': l })?.['Wartość_metryki']]).filter(e => e[1] !== undefined)),
    })),
    file: 'wykres_K2_sync.svg',
  });

  md.push('## K2 – Z2.3 mln komunikatów/s', '');
  const qt = [...new Set(k2.filter(r => r.Zadanie === 'queue').map(r => r['Wątki']))].sort((a, b) => a - b);
  table(['Wątki', ...LANGS], qt.map(t => [t, ...LANGS.map(l => pl(find(k2, { Zadanie: 'queue', 'Wątki': t, 'Język': l })?.['Wartość_metryki']))]));
}

// ---------- czas względem C++ (średnia geometryczna) ----------
function geo(rows, task) {
  return Object.fromEntries(LANGS.map(l => {
    const v = rows.filter(r => r.Zadanie === task && r['Język'] === l && r['Czas_względem_C++'] > 0).map(r => Math.log(r['Czas_względem_C++']));
    return [l, v.length ? Math.exp(v.reduce((a, b) => a + b, 0) / v.length) : undefined];
  }));
}
md.push('## Czas względem C++ (średnia geometryczna, C++ = 1,00)', '');
table(['Zadanie', ...LANGS], [...['mem-access', 'matmul', 'objects'].map(t => [t, geo(k1, t)]), ...['mandelbrot', 'sync', 'queue'].map(t => [t, geo(k2, t)])]
  .filter(([, g]) => g['C++'] !== undefined).map(([t, g]) => [t, ...LANGS.map(l => pl(g[l]))]));

// ================= K3 =================
if (k3.length) {
  const startMode = find(k3, { Scenariusz: 'startup', Wariant: 'cold' }) ? ['warm', 'cold'] : ['warm'];
  md.push('## K3 – Z3.1 start', '');
  const rows = [];
  for (const w of startMode)
    for (const [label, metric, d] of [['czas do 1. okna – mediana [ms]', 'start_mediana_ms', 0], ['± 95% CI [ms]', 'start_ci95_ms', 0],
      ['szczyt. zestaw roboczy [MB]', 'szczyt_ws_mb', 1], ['szczyt. pamięć prywatna [MB]', 'szczyt_prywatna_mb', 1], ['wątki procesu', 'watki_max', 0], ['rozmiar programu [KB]', 'rozmiar_programu_kb', 0]])
      rows.push([`${w}: ${label}`, ...LANGS.map(l => pl(k3v({ Scenariusz: 'startup', Wariant: w, 'Język': l }, metric), d))]);
  table(['Metryka', ...LANGS], rows);
  barChart({
    title: 'Z3.1 Czas do pierwszego okna [ms] (mniej = lepiej)', unit: 'ms (mediana z 10 startów)',
    groups: [{ label: 'ciepły start', values: Object.fromEntries(LANGS.map(l => [l, k3v({ Scenariusz: 'startup', Wariant: 'warm', 'Język': l }, 'start_mediana_ms')])) }],
    file: 'wykres_K3_start.svg',
  });

  md.push('## K3 – Z3.2 fill / draw', '');
  const fills = [...new Set(k3.filter(r => r.Scenariusz === 'fill').map(r => r.Parametr))].sort((a, b) => a - b);
  table(['Konfiguracja', ...LANGS], [
    ...fills.flatMap(p => [
      [`fill ${p}: mediana [ms]`, ...LANGS.map(l => pl(k3v({ Scenariusz: 'fill', Parametr: p, 'Język': l }, 'mediana_ms'), 1))],
      [`fill ${p}: ± 95% CI [ms]`, ...LANGS.map(l => pl(k3v({ Scenariusz: 'fill', Parametr: p, 'Język': l }, 'ci95_ms'), 1))],
      [`fill ${p}: 1. iteracja [ms]`, ...LANGS.map(l => pl(k3v({ Scenariusz: 'fill', Parametr: p, 'Język': l }, 'pierwsza_iteracja_ms'), 1))]]),
    ['draw: klatki/s', ...LANGS.map(l => pl(k3v({ Scenariusz: 'draw', 'Język': l }, 'klatki_na_s'), 0))],
  ]);

  md.push('## K3 – Z3.3 responsywność: średnie / p95 / maks. [ms], zamrożenia > 100 ms, obrazy', '');
  table(['Wariant', ...LANGS], ['idle', 'ui-thread', 'worker'].map(v => [v, ...LANGS.map(l => {
    const q = { Scenariusz: 'responsive', Wariant: v, 'Język': l };
    return `${pl(k3v(q, 'opoznienie_srednie_ms'))} / ${pl(k3v(q, 'opoznienie_p95_ms'))} / ${pl(k3v(q, 'opoznienie_max_ms'), 1)}; ${pl(k3v(q, 'zamrozenia_powyzej_100ms'), 0)}; ${pl(k3v(q, 'obrazy'), 0)}`;
  })]));
}

// ---------- wyniki niezgodne ----------
const bad = [...k1, ...k2].filter(r => r.Wynik_niezgodny === 1);
md.push(`Niezgodne wyniki kontrolne: ${bad.length ? bad.map(r => `${r.Zadanie} ${r.Wariant} ${r.Rozmiar} ${r['Język']}`).join(', ') : 'brak'}.`, '');

fs.writeFileSync(path.join(outDir, 'tabele.md'), md.join('\n'));
console.log(`Zapisano w ${outDir}: ${fs.readdirSync(outDir).join(', ')}`);
