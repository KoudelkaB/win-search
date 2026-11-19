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

// The native viewer opens dropped JSON as text. Keep the shared help apart from the
// HTML app's final sentence about drops and its profile import instructions.
const profileHelp = {
    en: 'Profiles are stored locally. Use the app menu (≡) to export or import a JSON profile bundle.',
    cs: 'Profily se ukládají lokálně. Balíček profilů JSON můžete exportovat nebo importovat v nabídce aplikace (≡).',
    de: 'Profile werden lokal gespeichert. Über das App-Menü (≡) lassen sich JSON-Profilpakete exportieren und importieren.',
    es: 'Los perfiles se almacenan localmente. Use el menú de la aplicación (≡) para exportar o importar un paquete de perfiles JSON.',
    fr: 'Les profils sont stockés localement. Le menu de l’application (≡) permet d’exporter ou d’importer un lot de profils JSON.',
    it: 'I profili sono archiviati localmente. Usa il menu dell’app (≡) per esportare o importare un pacchetto di profili JSON.',
    ja: 'プロファイルはローカルに保存されます。アプリメニュー (≡) から JSON プロファイルバンドルをエクスポートまたはインポートできます。',
    ko: '프로필은 로컬에 저장됩니다. 앱 메뉴(≡)에서 JSON 프로필 번들을 내보내거나 가져올 수 있습니다.',
    pl: 'Profile są przechowywane lokalnie. Menu aplikacji (≡) umożliwia eksport i import pakietów profili JSON.',
    'pt-BR': 'Os perfis são armazenados localmente. Use o menu do aplicativo (≡) para exportar ou importar um pacote de perfis JSON.',
    'zh-Hans': '配置文件保存在本地。使用应用菜单 (≡) 导出或导入 JSON 配置文件包。',
};
const closeView = {
    cs: 'Zavřít zobrazení souborů',
    de: 'Dateiansicht schließen',
    es: 'Cerrar la vista de archivos',
    fr: 'Fermer la vue des fichiers',
    it: 'Chiudi la vista dei file',
    ja: 'ファイルビューを閉じる',
    ko: '파일 보기 닫기',
    pl: 'Zamknij widok plików',
    'pt-BR': 'Fechar a visualização de arquivos',
    'zh-Hans': '关闭文件视图',
};
const excludeSelection = {
    en: 'Add the selection as an exclude clause the same way',
    cs: 'Stejně přidat výběr jako vylučující podmínku',
    de: 'Auswahl auf dieselbe Weise als Ausschlussklausel hinzufügen',
    es: 'Agregar la selección como cláusula de exclusión del mismo modo',
    fr: 'Ajouter la sélection comme clause d’exclusion de la même manière',
    it: 'Aggiungi la selezione come clausola di esclusione allo stesso modo',
    ja: '同様に選択範囲を除外条件として追加',
    ko: '같은 방식으로 선택 영역을 제외 조건으로 추가',
    pl: 'W ten sam sposób dodaj zaznaczenie jako warunek wykluczania',
    'pt-BR': 'Adicionar a seleção como cláusula de exclusão da mesma forma',
    'zh-Hans': '以相同方式将所选内容添加为排除子句',
};
const showLineNumbers = {
    cs: 'Zobrazit čísla řádků',
    de: 'Zeilennummern anzeigen',
    es: 'Mostrar números de línea',
    fr: 'Afficher les numéros de ligne',
    it: 'Mostra numeri di riga',
    ja: '行番号を表示',
    ko: '줄 번호 표시',
    pl: 'Pokaż numery wierszy',
    'pt-BR': 'Mostrar números de linha',
    'zh-Hans': '显示行号',
};
const htmlFilterTooltip = 'Keep or hide lines with the selected text (F; Enter keeps, Esc hides)';
const nativeFilterTooltip = (text) => text.replace('Enter', 'Enter/+').replace('Esc', '-');
for (const [language, entries] of Object.entries(ordered)) {
    entries['help.load.1'] = entries['help.load.1']
        .replace(/([.。])\s*[^.。]+[.。]$/u, '$1')
        .replace('▤', `<strong>${entries['Open file'] || 'Open file'}</strong>`);
    entries['help.profiles.2'] = profileHelp[language];
    delete entries['A JSON profile bundle is imported instead.'];
    if (closeView[language]) entries['Close file view'] = closeView[language];
    if (showLineNumbers[language]) entries['Show line numbers'] = showLineNumbers[language];
    delete entries['help.keys.esc'];
    entries['help.keys.exclude'] = excludeSelection[language];
    if (entries[htmlFilterTooltip]) entries[nativeFilterTooltip(htmlFilterTooltip)] = nativeFilterTooltip(entries[htmlFilterTooltip]);
    delete entries[htmlFilterTooltip];
}

const target = path.join(__dirname, '..', 'core', 'assets', 'translations.json');
fs.writeFileSync(target, JSON.stringify(ordered, null, 1) + '\n');
console.log(`${target}: ${Object.keys(ordered).join(', ')}; ${Object.keys(english).length} help texts`);
