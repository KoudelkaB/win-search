// Regenerates core/assets/translations.json from the HTML app, so both apps share one set of
// UI texts: node tools/extract-translations.cjs [path/to/LogExplorer.html]
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = process.argv[2] || path.join(__dirname, '..', '..', 'external', 'singleHtmlApps', 'LogExplorer.html');
const html = fs.readFileSync(source, 'utf8');

const start = html.indexOf('const LOG_EXPLORER_TRANSLATIONS =');
const end = html.indexOf('</script>', start);
if (start < 0 || end < 0) throw new Error('LOG_EXPLORER_TRANSLATIONS not found in ' + source);
const sandbox = {};
vm.runInNewContext(html.slice(start, end).replace('const LOG_EXPLORER_TRANSLATIONS =', 'this.translations ='), sandbox);
const translations = sandbox.translations;

// The English help paragraphs live in the markup, under the keys the translations use
const english = {};
const body = html.slice(html.indexOf('<body>'), start);
for (const match of body.matchAll(/<(p|li|td)\b[^>]*\bdata-i18n-html="([^"]+)"[^>]*>([\s\S]*?)<\/\1>/g)) {
    english[match[2]] = match[3].replace(/\s+/g, ' ').trim();
}
// The fonts bundled with the UI toolkit draw these symbols; the HTML app's emoji would
// depend on the fonts of the system
const SYMBOLS = { '📂': '▤', '☰': '≡', '✕': '×', '⧉': '▣' };
const drawable = (text) => text.replace(/📂|☰|✕|⧉/gu, (symbol) => SYMBOLS[symbol]);
const remap = (entries) => Object.fromEntries(Object.entries(entries).map(([key, value]) => [drawable(key), drawable(value)]));
const ordered = { en: remap(english) };
for (const language of Object.keys(translations).sort()) ordered[language] = remap(translations[language]);

const target = path.join(__dirname, '..', 'core', 'assets', 'translations.json');
fs.writeFileSync(target, JSON.stringify(ordered, null, 1) + '\n');
console.log(`${target}: ${Object.keys(ordered).join(', ')}; ${Object.keys(english).length} help texts`);
