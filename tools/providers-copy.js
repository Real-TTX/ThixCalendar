#!/usr/bin/env node
// Copies ThixCalendar's providers (Microsoft Graph, Google Calendar, ICS, the OAuth token calls) with the models they speak into MatCloud's
// calendar module, which syncs the accounts MatCloud keeps in step on the server with the same code as the desktop app:
//   node tools/providers-copy.js <folder of MatCloud's calendar module project> [--dry]
//   e.g. node tools/providers-copy.js ../MatCloud/src/Apps/MatCloud.Apps.Calendar
// It writes src/ThixCalendar.Core/Models/CalendarModels.cs and src/ThixCalendar.Core/Providers/*.cs into <folder>/ThixCalendar/ with the first line
//   // ThixCalendar.Core providers – a copy from ThixCalendar, do not edit here
// and thixcalendar.version next to them (the SHA-256 of each file as copied, like sha256sum); MatCloud's tests hold the copy to it. The copy needs two
// small stand-ins in the module (ThixCalendar.Core.Infrastructure.Log and AppInfo.BuildNumber).
// Exit code: 0 copied, 64 wrong arguments, 66 the folder is not a project folder.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const args = process.argv.slice(2);
const dry = args.includes('--dry');
const target = args.find(a => !a.startsWith('--'));
if (!target) { console.error('usage: node tools/providers-copy.js <folder of the module project> [--dry]'); process.exit(64); }

const folder = path.resolve(target);
const projects = fs.existsSync(folder) ? fs.readdirSync(folder).filter(f => f.endsWith('.csproj')) : [];
if (projects.length === 0) { console.error(`${folder} is not the folder of a .NET project (no .csproj in it).`); process.exit(66); }

const core = path.join(__dirname, '..', 'src', 'ThixCalendar.Core');
const LF = String.fromCharCode(10);
const header = '// ThixCalendar.Core providers – a copy from ThixCalendar, do not edit here';
const destination = path.join(folder, 'ThixCalendar');
const sha = text => crypto.createHash('sha256').update(text, 'utf8').digest('hex');

const files = ['Models/CalendarModels.cs']
  .concat(fs.readdirSync(path.join(core, 'Providers')).filter(f => f.endsWith('.cs')).sort().map(f => 'Providers/' + f));
const manifest = ['ThixCalendar.Core providers – a copy from ThixCalendar (tools/providers-copy.js)'];
if (!dry && fs.existsSync(destination)) { fs.rmSync(destination, { recursive: true }); }
for (const file of files) {
  const text = header + LF + fs.readFileSync(path.join(core, file), 'utf8').split(String.fromCharCode(13) + LF).join(LF);
  manifest.push(`${sha(text)}  ${file}`);
  if (!dry) {
    fs.mkdirSync(path.dirname(path.join(destination, file)), { recursive: true });
    fs.writeFileSync(path.join(destination, file), text);
  }
  console.log(`${dry ? 'would copy' : 'copied'}  ThixCalendar/${file}`);
}
if (!dry) { fs.writeFileSync(path.join(destination, 'thixcalendar.version'), manifest.join(LF) + LF); }
