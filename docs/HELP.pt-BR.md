# Ajuda do File Search Manager

`F1` abre esta ajuda no aplicativo; `F12` atualiza o índice NTFS.

## Filtros

- `report` contém o texto; `:report` começa com ele; `report:` termina com ele; `:report:` é exato.
- `.pdf:|.docx:` corresponde a nomes que terminam com uma dessas extensões; termos separados por espaço são combinados com **E**.
- `src\` procura a pasta pai; `src\\` procura no caminho completo.
- As âncoras de nome se aplicam a cada componente do caminho, portanto `:src:\\` encontra itens com uma pasta chamada exatamente `src` em qualquer lugar do caminho.
- `"C:\Work"` procura diretamente na pasta; `"C:\Work\\"` procura recursivamente abaixo dela.

Use aspas para caminhos com espaços. `Ctrl+Left` e `Ctrl+Right` percorrem o histórico; `Down` mostra sugestões.

## Exemplos práticos

- **Liberar espaço em disco** — limpe **Filtro** para mostrar todos os itens indexados. Clique no cabeçalho **Tamanho** (novamente, se necessário) até que os maiores itens fiquem no topo, examine a lista de cima para baixo e exclua os itens desnecessários com `Shift+Delete` (permanentemente, sem enviá-los para a Lixeira).
- **Observar onde um aplicativo grava** — limpe **Filtro** e clique no cabeçalho **Alterado** até que os itens modificados mais recentemente fiquem no topo. Em seguida, inicie ou use o aplicativo que deseja observar. Os arquivos nos quais ele está gravando sobem para o topo conforme mudam, e a coluna **Pasta** mostra a localização. Digite primeiro um caminho em **Filtro** se quiser observar apenas parte do sistema de arquivos.
- **Pesquisar texto em arquivos de código-fonte** — por exemplo, use `C:\Projects\\ .cs:` para limitar os resultados aos arquivos `.cs` de um projeto. Digite o texto procurado em **Pesquisar** e pressione `Enter`.

## Pesquisa e teclas

Digite o texto e pressione `Enter`. `UTF-8` e `UTF-16` procuram texto; `HEX` procura bytes como `48 65 6C 6C 6F`. `Delete` exclui, `F2` renomeia, `O` abre, `U` extrai e `Z` cria arquivo. `›` abre submenu, `Backspace` volta e `Esc` cancela.
