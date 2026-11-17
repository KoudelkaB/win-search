#!/usr/bin/env node
// Writes the notices of the crates built into the logexplorer program: every crate with its
// version, license and project, then the license texts as the crates ship them (each text
// once, with the crates it comes from). The packages carry the file next to the other
// notices (licenses/logexplorer-THIRD-PARTY-NOTICES.txt, see search/search.csproj).
//
// Taken from the very crates of the build - Cargo.lock, for one target - so it cannot fall
// behind them. It fails, and the release with it, when a crate comes under a license that is
// not in ACCEPTED below, or ships no license text and tools/license-texts has none for it:
// the counterpart of tools/check-third-party-notices.py for the NuGet packages.
//
//   node tools/third-party-notices.cjs [--target TRIPLE] [--out FILE]
//
// The target is the one of the machine by default, the file target/logexplorer-THIRD-PARTY-NOTICES.txt.
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

// Licenses a crate of the program may come under. All of them allow shipping a closed or
// MIT-licensed binary with their notice; MPL-2.0 asks for the source of its own files,
// which the listed project is.
const ACCEPTED = new Set([
    'MIT', 'Apache-2.0', 'Apache-2.0 WITH LLVM-exception', 'BSD-2-Clause', 'BSD-3-Clause', '0BSD', 'ISC', 'Zlib',
    'Unlicense', 'BSL-1.0', 'CC0-1.0', 'Unicode-3.0', 'OFL-1.1', 'Ubuntu-font-1.0', 'MPL-2.0',
]);
// For a crate that ships no license text: the standard text of its license, in this order
// of preference where the crate gives a choice
const STANDARD = ['MIT', 'Apache-2.0', 'BSL-1.0'];

const root = path.resolve(__dirname, '..');
const args = process.argv.slice(2);
const option = name => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : null; };
const run = (program, parameters) => execFileSync(program, parameters, { cwd: root, encoding: 'utf8', maxBuffer: 1 << 28 });
const target = option('--target') || /^host: (.+)$/m.exec(run('rustc', ['-vV']))[1].trim();
const out = path.resolve(option('--out') || path.join(root, 'target', 'logexplorer-THIRD-PARTY-NOTICES.txt'));

// --- The crates of the program: what the package needs to build for the target, without
// the crates of its tests and of build scripts (they are not in the program)
const metadata = JSON.parse(run('cargo', ['metadata', '--format-version', '1', '--locked', '--filter-platform', target]));
const packages = new Map(metadata.packages.map(p => [p.id, p]));
const nodes = new Map(metadata.resolve.nodes.map(n => [n.id, n]));
const program = metadata.packages.find(p => p.name === 'logexplorer' && p.source === null);
const used = new Set([program.id]);
for (const queue = [program.id]; queue.length;) {
    for (const dependency of nodes.get(queue.pop()).deps) {
        if (!dependency.dep_kinds.some(k => k.kind === null) || used.has(dependency.pkg)) continue;
        used.add(dependency.pkg);
        queue.push(dependency.pkg);
    }
}
const crates = [...used].map(id => packages.get(id)).filter(p => p.source !== null)
    .sort((a, b) => a.name.localeCompare(b.name, 'en') || a.version.localeCompare(b.version, 'en'));

// --- SPDX expressions: ids joined by AND, OR (or the "/" of old manifests) and parentheses.
// Returns the ids of one way to satisfy the expression with accepted licenses, or null.
function choice(expression) {
    const tokens = expression.replace(/\//g, ' OR ').match(/\(|\)|[^\s()]+/g) || [];
    let at = 0;
    const either = () => {
        let found = both();
        while (tokens[at] === 'OR') { at++; const other = both(); found = found || other; }
        return found;
    };
    const both = () => {
        let found = one();
        while (tokens[at] === 'AND') { at++; const other = one(); found = found && other && [...found, ...other]; }
        return found;
    };
    const one = () => {
        if (tokens[at] === '(') { at++; const found = either(); at++; return found; }
        let id = tokens[at++];
        if (tokens[at] === 'WITH') { id += ' WITH ' + tokens[at + 1]; at += 2; }
        return ACCEPTED.has(id) ? [id] : null;
    };
    const found = either();
    return at === tokens.length ? found : null;
}

// --- The texts a crate ships: files named as licenses, and the notes next to its fonts
const NAMED = /^(licen[sc]e|copying|copyright|notice|unlicense|ofl|ufl)|[-_.]licen[sc]e(\.|$)/i;
function shipped(directory, depth = 0) {
    const entries = fs.readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'));
    const fonts = entries.some(e => /\.(ttf|otf)$/i.test(e.name));
    const files = [];
    for (const entry of entries) {
        const full = path.join(directory, entry.name);
        if (entry.isDirectory()) {
            if (depth < 1 && !/^(src|tests|benches|examples|target|\.git)$/.test(entry.name)) files.push(...shipped(full, depth + 1));
        } else if (NAMED.test(entry.name) || (fonts && /\.txt$/i.test(entry.name))) files.push(full);
    }
    return files;
}

const problems = [];
const texts = new Map(); // the text -> the crates and files it comes from
const add = (text, origin) => {
    text = text.replace(/^﻿/, '').replace(/\r\n?/g, '\n').replace(/[ \t]+$/gm, '').trim();
    if (!text) return;
    if (!texts.has(text)) texts.set(text, []);
    texts.get(text).push(origin);
};
const lines = [];
for (const crate of crates) {
    const name = `${crate.name} ${crate.version}`;
    const directory = path.dirname(crate.manifest_path);
    const license = crate.license || (crate.license_file ? `see ${crate.license_file}` : null);
    const chosen = crate.license ? choice(crate.license) : null;
    if (crate.license && !chosen) problems.push(`${name}: the license "${crate.license}" is not among the accepted ones`);
    if (!license) problems.push(`${name}: no license in its manifest`);
    lines.push(`${name} - ${license || 'unknown'}${crate.repository ? ` - ${crate.repository}` : ''}`);

    const files = shipped(directory);
    // sctk-adwaita embeds Cantarell under OFL-1.1, but its crate only ships the
    // code's MIT license file. Preserve the font's embedded copyright as well.
    if (crate.name === 'sctk-adwaita') {
        add(fs.readFileSync(path.join(__dirname, 'license-texts', 'Cantarell-OFL.txt'), 'utf8'),
            `${name} (src/title/Cantarell-Regular.ttf)`);
    }
    if (crate.license_file && !files.includes(path.join(directory, crate.license_file))) files.push(path.join(directory, crate.license_file));
    for (const file of files) add(fs.readFileSync(file, 'utf8'), `${name} (${path.relative(directory, file).replace(/\\/g, '/')})`);
    if (files.length || !chosen) continue;
    // No text in the crate: the standard one, under the name of the crate
    const standard = STANDARD.find(id => chosen.includes(id) || (crate.license.includes(id) && choice(id)));
    const template = standard && path.join(__dirname, 'license-texts', standard + '.txt');
    if (!template || !fs.existsSync(template)) {
        problems.push(`${name}: ships no license text, and tools/license-texts has none for "${crate.license}"`);
        continue;
    }
    const holders = (crate.authors || []).map(a => a.replace(/\s*<[^>]*>/, '')).filter(Boolean).join(', ');
    add(fs.readFileSync(template, 'utf8').replace('{holders}', holders || `the authors of ${crate.name}`),
        `${name} (the crate ships no license file: the standard text of ${standard})`);
}

if (problems.length) {
    for (const problem of problems) console.error(`::error file=logexplorer/tools/third-party-notices.cjs::${problem}`);
    process.exit(1);
}

const rule = '-'.repeat(78);
const document = [
    'File Search Manager - Rust dependency notices',
    '',
    'The built-in Log Explorer uses the third-party Rust libraries listed',
    `below (target ${target}). Each line gives the crate, its version, its license and its`,
    'project; the license texts the crates ship follow, each once, with the crates it',
    'comes from. Where a crate offers a choice of licenses, it is used under the first of',
    'them that permits it (MIT or Apache-2.0).',
    '',
    `Crates (${crates.length})`,
    rule,
    ...lines,
    '',
    'License texts',
    ...[...texts].flatMap(([text, origins]) => [rule, ...origins, '', text, '']),
].join('\n');
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, document);
console.log(`${crates.length} crates, ${texts.size} license texts -> ${out}`);
