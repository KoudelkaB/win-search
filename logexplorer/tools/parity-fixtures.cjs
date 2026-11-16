// Runs pipelines through the operations of the HTML app and records the rows they give:
// core/tests/fixtures/parity.json, which core/tests/parity.rs holds the Rust engine to.
//   node tools/parity-fixtures.cjs [path/to/LogExplorer.html]
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = process.argv[2] || path.join(__dirname, '..', '..', 'external', 'singleHtmlApps', 'LogExplorer.html');
const html = fs.readFileSync(source, 'utf8');
const start = html.indexOf('document.addEventListener("DOMContentLoaded"');
const bodyStart = html.indexOf('{', start) + 1;
const bodyEnd = html.indexOf('// Embedding API', bodyStart);
if (start < 0 || bodyEnd < 0) throw new Error('The script of the app was not found in ' + source);

// The functions of the app need a page only to exist, not to work
const element = new Proxy({}, { get: (target, name) => name === 'classList' ? { add() {}, remove() {}, toggle() {}, contains: () => false } : (name in target ? target[name] : () => element), set: () => true });
const context = vm.createContext({
    console, URLSearchParams, TextEncoder, TextDecoder, Set, Map, Intl,
    LOG_EXPLORER_TRANSLATIONS: {},
    location: { search: '' },
    navigator: { languages: ['en'], language: 'en' },
    document: { getElementById: () => element, documentElement: element, body: element, addEventListener() {}, activeElement: null },
    localStorage: { getItem: () => null, setItem() {} },
    requestAnimationFrame: () => 0,
    window: {},
    Node: { TEXT_NODE: 3, ELEMENT_NODE: 1 },
});
vm.runInContext(html.slice(bodyStart, bodyEnd) + `
;globalThis.api = { applyFilter, applyReplace, applySort, applyDiff, applyExtract, applyUnique, applyJoin, buildLineStage,
    materializeStageLines, getStageLineSourceIds, getStageLineRowIds, normalizeOperator, inferSelectionValueType, parseTypedValue };`, context, { filename: 'LogExplorer.html' });
const api = context.api;

function run(testCase) {
    // Files one after another, like "Concat": the rows of file i have source i
    const lines = testCase.files.flat();
    const sourceIds = testCase.files.flatMap((file, index) => file.map(() => index));
    let stage = api.buildLineStage(lines, 'raw', 'Raw', { lineSourceIds: sourceIds, sourceCatalog: testCase.files.map((_, index) => ({ label: 'f' + index, path: 'f' + index })) });
    const stages = [];
    for (const raw of testCase.operators) {
        const operator = api.normalizeOperator({ enabled: true, ...raw });
        let result;
        try {
            result = api['apply' + operator.type[0].toUpperCase() + operator.type.slice(1)](stage, operator, 'h');
        } catch (error) {
            stages.push({ error: error.message });
            break;
        }
        stage = result;
        const out = { lines: [], src: [], rowIds: [] };
        const ids = api.getStageLineSourceIds(stage), rowIds = api.getStageLineRowIds(stage);
        api.materializeStageLines(stage).forEach((line, index) => {
            // A value over several rows is one "line" with breaks in the HTML app; rows here
            for (const part of String(line).split('\n')) {
                out.lines.push(part);
                out.src.push(ids[index] ?? -1);
                out.rowIds.push(rowIds[index] ?? -1);
            }
        });
        stages.push(out);
    }
    return { ...testCase, stages };
}

const log = [
    '[4711] 2026-03-13T09:14:02.105Z START GET /api/orders',
    '[4711] 2026-03-13T09:14:02.248Z END   200 GET /api/orders',
    '    at Checkout.run (checkout.js:12)',
    '[4712] 2026-03-13T09:14:02.110Z START POST /api/payments',
    '[4713] 2026-03-13T09:14:04.000Z START GET /health',
    '[4712] 2026-03-13T09:14:03.031Z END   201 POST /api/payments',
    '',
    '[4713] 2026-03-13T09:14:04.007Z end   204 GET /health',
];
const ocr = ['    Lang   72dpi   300dpi  600dpi  best', '     afr   2.8%   95.6%   95.2%   95.6%', '     amh   33.2%   27.0%   48.6%   48.6%', '     ara   32.4%   77.8%   78.0%   78.0%', '     asm   21.0%   63.0%   62.8%   63.0%', '     aze   48.1%   95.4%   96.0%   100.0%'];
const filter = (include, exclude = [], extra = {}) => ({ type: 'filter', include: include.map((text) => typeof text === 'string' ? { text, caseSensitive: false } : text), exclude: exclude.map((text) => ({ text, caseSensitive: false })), sources: [], ...extra });
const sort = (extra) => ({ type: 'sort', mode: 'positional', pos: 0, len: 1, anchor: '', anchorSide: 'after', valueType: 'string', order: 'asc', ...extra });
const diff = (extra) => ({ type: 'diff', pos: 0, len: 1, valueType: 'number', invert: false, ...extra });
const extract = (extra) => ({ type: 'extract', ...extra });

const cases = [
    { name: 'the REST duration recipe of the guide', files: [log], operators: [filter([' START \n END ']), sort({ pos: 1, len: 4 }), diff({ pos: 7, len: 24, valueType: 'date-time' }), filter([' END ']), sort({ pos: 7, len: 24, valueType: 'time-span' })] },
    { name: 'filter: case, alternatives, exclude', files: [log], operators: [filter([{ text: 'END', caseSensitive: true }, 'checkout'], ['201'])] },
    { name: 'filter: only line breaks is a clause that matches nothing', files: [log], operators: [filter(['\n'])] },
    { name: 'filter: no text keeps all, exclude only', files: [log], operators: [filter([''], ['start'])] },
    { name: 'filter: only in files', files: [['a x', 'a y'], ['b x'], ['c x', 'c z']], operators: [filter(['x'], [], { sources: ['f1␟f1', 'f2␟f2'] })] },
    { name: 'replace: in rows, deleting, with unicode', files: [['a--b--c', 'žluť--kůň', 'none', '--']], operators: [{ type: 'replace', search: '--', replacement: '→' }, { type: 'replace', search: 'ů', replacement: '' }] },
    { name: 'replace: splitting rows', files: [['ts=1; level=E; user=42', 'next; row'], ['other; file']], operators: [{ type: 'replace', search: '; ', replacement: ';\n' }] },
    { name: 'replace: joining rows', files: [['ERROR failed', '    user=42', '    order=9', 'INFO ok', '    tail'], ['second', '    file']], operators: [{ type: 'replace', search: '\n    ', replacement: ' ' }] },
    { name: 'replace: every line break', files: [['a', 'b', '', 'c']], operators: [{ type: 'replace', search: '\n', replacement: '|' }] },
    { name: 'replace: search over three rows and overlapping candidates', files: [['x}', 'mid', '{y}', 'mid', '{z', 'aa', 'aa', 'aa', 'aa']], operators: [{ type: 'replace', search: '}\nmid\n{', replacement: '}{' }, { type: 'replace', search: 'aa\naa', replacement: 'A' }] },
    { name: 'replace: replacement with breaks at both ends', files: [['one,two', 'three']], operators: [{ type: 'replace', search: ',', replacement: '\n,\n' }] },
    { name: 'sort: numbers with followers and leading rows', files: [['header', '10 ten', '  more of ten', '9 nine', 'x', '-1,5 minus', '9 again', '1e2 hundred', '0x10 hex', ' 7  spaced']], operators: [sort({ len: 4, valueType: 'number' })] },
    { name: 'sort: numbers descending keeps ties in order', files: [['1 a', '2 b', '1 c', '2 d', '3 e']], operators: [sort({ valueType: 'number', order: 'desc' })] },
    { name: 'sort: text', files: [['b', 'A', 'a', 'B', 'C', '10', '9', '_x', '-y', ' z', 'a b', 'ab', 'a-b', 'a_b', 'A1', 'a1', '[4712]', '[4711]', '(1)', 'Zeta', 'alpha', 'Beta']], operators: [sort({ len: 6 })] },
    { name: 'sort: next to a text, after and before', files: [['GET status=500 took 30 ms', 'GET status=200 took 4 ms', 'no value', 'GET status=404 took 100 ms']], operators: [sort({ mode: 'anchor', anchor: 'status=', valueType: 'number' }), sort({ mode: 'anchor', anchor: ' ms', anchorSide: 'before', valueType: 'number', order: 'desc' })] },
    { name: 'sort: text after an anchor takes the rest of the row', files: [['k=b 2', 'k=a 9', 'k=b 1']], operators: [sort({ mode: 'anchor', anchor: 'k=' })] },
    { name: 'sort: dates in several notations', files: [['2026-03-13T09:14:02.105Z a', '2026-03-13T09:14:02.105+02:00 b', '2025-12-31T23:59:59Z c', 'not a date', '2026-03-13T09:14:02Z d']], operators: [sort({ len: 29, valueType: 'date-time' })] },
    { name: 'sort: durations', files: [['00:00:10.000', '00:00:02.500', '1.00:00:00', '-00:00:01', '250', '00:60:00', '12:30']], operators: [sort({ len: 12, valueType: 'time-span' })] },
    { name: 'diff: numbers with decimals, a sign column on the right', files: [ocr], operators: [diff({ pos: 34, len: 4 })] },
    { name: 'diff: numbers, the sign column on the left', files: [ocr], operators: [diff({ pos: 34, len: 4, outputExtend: 'left' })] },
    { name: 'diff: integers and a decimal comma', files: [['v=10 x', 'v=7 x', 'v=7,5 x', 'v=100 x', 'none']], operators: [diff({ pos: 2, len: 3 })] },
    { name: 'diff: durations', files: [['00:00:10.000 a', '00:00:12.500 b', '00:01:00.000 c', 'x', '00:00:59.999 d']], operators: [diff({ len: 12, valueType: 'time-span' })] },
    { name: 'diff: durations without seconds and with days', files: [['1.02:00 a', '1.03:30 b', '0.23:59 c']], operators: [diff({ len: 7, valueType: 'time-span' })] },
    { name: 'diff: text keeps changed characters', files: [['id=abc1', 'id=abd1', 'id=abd2', 'short', 'id=zzzzzz']], operators: [diff({ pos: 3, len: 4, valueType: 'string' })] },
    { name: 'diff then reverse restores the input', files: [ocr], operators: [diff({ pos: 34, len: 4 }), diff({ pos: 34, len: 4, invert: true })] },
    { name: 'reverse on its own: numbers, text, durations', files: [['10', ' 5', '-3', 'x', '2,5']], operators: [diff({ len: 3, invert: true })] },
    { name: 'reverse on its own: text', files: [['abcd', '  x ', 'y   ', '    ']], operators: [diff({ len: 4, valueType: 'string', invert: true })] },
    { name: 'reverse on its own: durations', files: [['00:00:10.000', '00:00:02.500', '00:00:00.001']], operators: [diff({ len: 12, valueType: 'time-span', invert: true })] },
    { name: 'extract: regex groups over the whole view', files: [['[{"ConnectionId":"a1","x":1},{"ConnectionId": "b2"}]', '{"ConnectionId":"c3"} {"ConnectionId":" d4 "}'], ['{"ConnectionId":""}', '{"connectionid":"e5"}']], operators: [extract({ mode: 'regex', pattern: '"ConnectionId"\\s*:\\s*"([^"]*)"' })] },
    { name: 'extract: regex per row, first match, case-insensitive, no trim', files: [['id=1 id=2', 'none', 'ID= 3 id=4']], operators: [extract({ mode: 'regex', pattern: 'id=(\\s*\\d)', scope: 'line', allMatches: false, caseSensitive: false, trim: false })] },
    { name: 'extract: regex group 0, anchors per row, empty matches', files: [['abc', '', 'a1b22']], operators: [extract({ mode: 'regex', pattern: '^[a-z]+|\\d*$', group: 0, skipEmpty: false })] },
    { name: 'extract: regex first match of the whole view only', files: [['x', 'k=1 k=2', 'k=3']], operators: [extract({ mode: 'regex', pattern: 'k=(\\d)', allMatches: false })] },
    { name: 'extract: optional group that takes no part', files: [['a1', 'b', 'a2b']], operators: [extract({ mode: 'regex', pattern: 'a(\\d)|b' })] },
    { name: 'extract: regex rows range', files: [['n=1', 'n=2', 'n=3', 'n=4']], operators: [extract({ mode: 'regex', pattern: 'n=(\\d)', scope: 'line', rowMode: 'range', fromRow: 2, toRow: 3 })] },
    { name: 'extract: column with a row range', files: [ocr], operators: [extract({ mode: 'column', pos: 5, len: 3, rowMode: 'range', fromRow: 2, toRow: 4 }), extract({ mode: 'column', pos: 1, len: 5, skipEmpty: false, trim: false })] },
    { name: 'extract: between, whole view and per row', files: [['<a> one </a><a>two</a>', 'x <a>three</a> <a>', '<A>four</A>']], operators: [extract({ mode: 'between', startAnchor: '<a>', endAnchor: '</a>', scope: 'line' })] },
    { name: 'extract: between without case, first only, open ends', files: [['k: one; K: two;', 'k: three']], operators: [extract({ mode: 'between', startAnchor: 'k:', endAnchor: ';', caseSensitive: false, scope: 'line' }), extract({ mode: 'between', startAnchor: '', endAnchor: 'e', scope: 'line' })] },
    { name: 'extract: between with no closing text', files: [['a=1 a=2', 'b', 'a=3']], operators: [extract({ mode: 'between', startAnchor: 'a=', endAnchor: '', scope: 'line' })] },
    { name: 'unique: stable, trim, empty, case', files: [['b', ' a ', 'b', '', 'a', 'B', '  '], ['a', 'c']], operators: [{ type: 'unique' }] },
    { name: 'unique: loose', files: [['b', ' a ', 'B', '', 'a', '']], operators: [{ type: 'unique', trim: false, ignoreEmpty: false, caseSensitive: false }] },
    { name: 'unique: sorted with numbers by value', files: [['id10', 'id9', 'id10', 'id100', 'ID9', 'b', 'a', 'A', 'a2', 'a10']], operators: [{ type: 'unique', order: 'asc' }, { type: 'unique', order: 'desc', caseSensitive: false }] },
    { name: 'join: delimiter, prefix, suffix', files: [['id1', '', ' id2 ', 'id3'], ['id4']], operators: [{ type: 'join', delimiter: '|', prefix: '(', suffix: ')' }] },
    { name: 'join: escapes make rows', files: [['a', 'b', 'c']], operators: [{ type: 'join', delimiter: ',\\n', prefix: '[\\n', suffix: '\\n]\\t\\\\n' }] },
    { name: 'join: trimmed, keeping empty, no escapes', files: [[' a ', '', 'b']], operators: [{ type: 'join', delimiter: '\\n', trim: true, skipEmpty: false, interpretEscapes: false }] },
    { name: 'join: nothing to join', files: [['', '  ']], operators: [{ type: 'join', trim: true, prefix: '<', suffix: '>' }, { type: 'join', trim: true }] },
    { name: 'collect unique values', files: [log], operators: [extract({ mode: 'regex', pattern: '^\\[(\\d+)\\]' }), { type: 'unique' }, { type: 'join', delimiter: '|' }] },
    { name: 'a long pipeline keeps sources and rows', files: [log.slice(0, 4), log.slice(4)], operators: [{ type: 'replace', search: '/api/', replacement: '/' }, filter(['START', 'END']), sort({ pos: 1, len: 4 }), diff({ pos: 7, len: 24, valueType: 'date-time' }), extract({ mode: 'column', pos: 0, len: 40 }), { type: 'unique' }] },
];

// What a selection is taken for
const typed = ['95.6', '12', '-1,5', '1e3', '0x1F', ' 7 ', '', 'abc', '2026-03-13T09:14:02.105Z', '2026-03-13', '1.2.2026', '31.2.2026', '00:00:10.000', '1.02:03:04.005', '12:30', '25:00', '01:60', '30:00:00', '1.24:00:00', 'Infinity', '1 2', '5.', '.5', '+7', '--1', '1,2,3', '0b101', '0o17', '12abc'].map((text) => ({
    text,
    type: api.inferSelectionValueType(text),
    // "1 2", "01:60", "--1"... are dates only to the lenient legacy parser of Date.parse
    legacyDate: api.inferSelectionValueType(text) === 'date-time' && !/\d{4}-\d{2}|\d{1,2}[./]\d{1,2}[./]\d{2,4}|[A-Za-z]{3}/.test(text),
    number: api.parseTypedValue(text, 'number', false).comparable,
    span: api.parseTypedValue(text, 'time-span', false).comparable,
}));

const target = path.join(__dirname, '..', 'core', 'tests', 'fixtures', 'parity.json');
fs.writeFileSync(target, JSON.stringify({ cases: cases.map(run), typed }, null, 1) + '\n');
console.log(`${target}: ${cases.length} pipelines, ${typed.length} values`);
