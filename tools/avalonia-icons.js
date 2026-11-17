// Writes src/ThixCalendar/Themes/Icons.axaml: the few line icons of ThixCalendar that MatDesign's sprite lacks.
//
//   node tools/avalonia-icons.js
//
// Every other icon is MatDesktop's (MatDesktop/Themes/MatIcons.axaml, copied from MatDesign): <mat:MatIcon Icon="calendar"/>.
// These are drawn in the same grid and line (24 x 24, 2 px, Lucide style) and get keys in MatDesktop's form, MatIcon.<name>,
// so MatIcon finds them like its own (<mat:MatIcon Icon="bell-ring"/>). When MatDesign's sprite gets one of them, it leaves
// this list. rect, circle, line, polyline and polygon are turned into path data.
'use strict';

const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const target = path.join(root, 'src', 'ThixCalendar', 'Themes', 'Icons.axaml');

// Drawn for ThixCalendar in the same grid and line (Lucide style).
const CUSTOM = {
  Window: "<rect x='3' y='4' width='18' height='16' rx='2'/><line x1='3' y1='9' x2='21' y2='9'/>",
  Video: "<path d='m16 13 5.2 3.5a.5.5 0 0 0 .8-.4V7.9a.5.5 0 0 0-.8-.4L16 11'/><rect x='2' y='6' width='14' height='12' rx='2'/>",
  MapPin: "<path d='M20 10c0 5-5.5 10.2-7.4 11.8a1 1 0 0 1-1.2 0C9.5 20.2 4 15 4 10a8 8 0 0 1 16 0'/><circle cx='12' cy='10' r='3'/>",
  BellRing: "<path d='M10.27 21a2 2 0 0 0 3.46 0'/><path d='M22 8c0-2.3-.8-4.3-2-6'/><path d='M3.26 15.33A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.67C19.41 13.96 18 12.5 18 8A6 6 0 0 0 6 8c0 4.5-1.41 5.96-2.74 7.33'/><path d='M4 2C2.8 3.7 2 5.7 2 8'/>",
  BellOff: "<path d='M10.27 21a2 2 0 0 0 3.46 0'/><path d='M17 17H4a1 1 0 0 1-.74-1.67C4.59 13.96 6 12.5 6 8a6 6 0 0 1 .26-1.74'/><path d='M8.67 3.09A6 6 0 0 1 18 8c0 2.4.43 4.04 1.07 5.27'/><line x1='2' y1='2' x2='22' y2='22'/>",
  Snooze: "<circle cx='12' cy='13' r='8'/><path d='M5 3 2 6'/><path d='m22 6-3-3'/><path d='M10 10h4l-4 5h4'/>",
  CalendarPlus: "<path d='M8 2v4'/><path d='M16 2v4'/><path d='M21 13V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h8'/><path d='M3 10h18'/><path d='M16 19h6'/><path d='M19 16v6'/>",
  Today: "<rect x='3' y='4' width='18' height='18' rx='2'/><path d='M16 2v4'/><path d='M8 2v4'/><path d='M3 10h18'/><circle cx='12' cy='16' r='2'/>",
  Rss: "<path d='M4 11a9 9 0 0 1 9 9'/><path d='M4 4a16 16 0 0 1 16 16'/><circle cx='5' cy='19' r='1'/>",
};

const num = (value) => Number(value);
const fmt = (value) => (Math.round(value * 1000) / 1000).toString();
const attrs = (tag) => {
  const result = {};
  for (const match of tag.matchAll(/([a-zA-Z0-9-]+)=['"]([^'"]*)['"]/g)) result[match[1]] = match[2];
  return result;
};

function rectPath(a) {
  const x = num(a.x || 0), y = num(a.y || 0), w = num(a.width), h = num(a.height);
  const r = Math.min(num(a.rx || a.ry || 0), w / 2, h / 2);
  if (r <= 0) return `M${fmt(x)},${fmt(y)} H${fmt(x + w)} V${fmt(y + h)} H${fmt(x)} Z`;
  return `M${fmt(x + r)},${fmt(y)} H${fmt(x + w - r)} A${fmt(r)},${fmt(r)} 0 0 1 ${fmt(x + w)},${fmt(y + r)} ` +
    `V${fmt(y + h - r)} A${fmt(r)},${fmt(r)} 0 0 1 ${fmt(x + w - r)},${fmt(y + h)} H${fmt(x + r)} ` +
    `A${fmt(r)},${fmt(r)} 0 0 1 ${fmt(x)},${fmt(y + h - r)} V${fmt(y + r)} A${fmt(r)},${fmt(r)} 0 0 1 ${fmt(x + r)},${fmt(y)} Z`;
}

function circlePath(a) {
  const cx = num(a.cx), cy = num(a.cy), r = num(a.r);
  return `M${fmt(cx - r)},${fmt(cy)} A${fmt(r)},${fmt(r)} 0 1 0 ${fmt(cx + r)},${fmt(cy)} A${fmt(r)},${fmt(r)} 0 1 0 ${fmt(cx - r)},${fmt(cy)} Z`;
}

function pointsPath(points, close) {
  const values = points.trim().split(/[\s,]+/).map(num);
  let d = '';
  for (let i = 0; i < values.length; i += 2) d += `${i === 0 ? 'M' : ' L'}${fmt(values[i])},${fmt(values[i + 1])}`;
  return close ? d + ' Z' : d;
}

// SVG path data allows "l.06.06" and "1-2.8": every number is written out with a space here. A path element starts at
// the origin, so its first "m" is absolute (it is joined to the other parts of the icon).
function normalizePath(d) {
  const tokens = d.match(/[a-zA-Z]|[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/g) || [];
  if (tokens[0] === 'm') {
    tokens[0] = 'M';
    // Pairs after a relative moveto are relative linetos: keep them relative.
    if (tokens.length > 3 && !/^[a-zA-Z]$/.test(tokens[3])) tokens.splice(3, 0, 'l');
  }
  return tokens.map((token) => (/^[a-zA-Z]$/.test(token) ? token : fmt(Number(token)))).join(' ').replace(/([a-zA-Z]) /g, '$1');
}

function toPathData(markup) {
  const parts = [];
  for (const match of markup.matchAll(/<(path|rect|circle|line|polyline|polygon)\b([^>]*)\/?>/g)) {
    const a = attrs(match[2]);
    switch (match[1]) {
      case 'path': parts.push(normalizePath(a.d)); break;
      case 'rect': parts.push(rectPath(a)); break;
      case 'circle': parts.push(circlePath(a)); break;
      case 'line': parts.push(`M${a.x1},${a.y1} L${a.x2},${a.y2}`); break;
      case 'polyline': parts.push(pointsPath(a.points, false)); break;
      case 'polygon': parts.push(pointsPath(a.points, true)); break;
    }
  }
  return parts.join(' ');
}

const kebab = (key) => key.replace(/([a-z])([A-Z])/g, "$1-$2").toLowerCase();
const lines = [];
for (const [key, markup] of Object.entries(CUSTOM)) {
  lines.push(`  <StreamGeometry x:Key="MatIcon.${kebab(key)}">${toPathData(markup)}</StreamGeometry>`);
}

const xml = `<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- Generated by tools/avalonia-icons.js: ThixCalendar's own icons in the grid and line of MatDesign's sprite (MatIcon.<name>). Do not edit by hand. -->
${lines.join('\n')}
</ResourceDictionary>
`;
fs.mkdirSync(path.dirname(target), { recursive: true });
fs.writeFileSync(target, xml.replace(/\n/g, '\r\n'));
console.log(`${lines.length} icons written to ${target}`);
