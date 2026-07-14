# Ajuda do File Search Manager

`F1` abre esta ajuda no aplicativo; `F12` atualiza o índice NTFS.

## Filtros

- `report` contém o texto; `:report` começa com ele; `report:` termina com ele; `:report:` é exato.
- `pdf|docx` aceita uma alternativa; termos separados por espaço são combinados com **E**.
- `src\` procura a pasta pai; `src\\` procura no caminho completo.
- `"C:\Work"` procura diretamente na pasta; `"C:\Work\\"` procura recursivamente abaixo dela.

Use aspas para caminhos com espaços. `Ctrl+Left` e `Ctrl+Right` percorrem o histórico; `Down` mostra sugestões.

## Pesquisa e teclas

Digite o texto e pressione `Enter`. `UTF-8` e `UTF-16` procuram texto; `HEX` procura bytes como `48 65 6C 6C 6F`. `Delete` exclui, `F2` renomeia, `O` abre, `U` extrai e `Z` cria arquivo. `›` abre submenu, `Backspace` volta e `Esc` cancela.
