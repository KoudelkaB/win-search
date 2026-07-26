# Ajuda do File Search Manager

O File Search Manager tem dois campos principais:

- **Filtro** restringe a lista de arquivos por nome, pasta, caminho ou diretórios selecionados.
- **Pesquisar** pesquisa o conteúdo dos arquivos dentro dos itens filtrados no momento.

A lista de resultados foi pensada para uso com o teclado. Quando ela está em foco, o painel **Dicas** mostra os comandos disponíveis para a seleção.

## Indexação e elevação

Ao iniciar, o File Search Manager carrega as unidades prontas e monitora as alterações do sistema de arquivos. Cada unidade é carregada de forma independente, portanto uma unidade de rede lenta nunca atrasa as demais.

Por padrão, apenas unidades NTFS são indexadas — outros sistemas de arquivos (montagens de rede, pendrives FAT) não conseguem usar a leitura rápida da MFT, e percorrê-los arquivo a arquivo pode dominar todo o carregamento. Use o botão **💽 Unidades…** na barra de status para escolher exatamente quais unidades são indexadas; unidades desmarcadas não são verificadas nem monitoradas.

Em unidades NTFS, o caminho mais rápido é ler a Master File Table do NTFS. O File Search Manager pode fazer isso de uma destas formas:

- Serviço do Windows: nenhuma solicitação. Selecionado por padrão no instalador.
- Auxiliar com privilégios elevados: uma solicitação do UAC por execução do aplicativo. Sem o serviço, ela é oferecida na inicialização, para que a indexação comece imediatamente. Com o serviço, só é oferecida na primeira ação que realmente exige direitos de administrador: a tecla `A` ou a leitura de arquivos restritos a administradores. O botão **🛡** ao lado de **🌐** faz a solicitação já na inicialização.
- Aplicativo elevado diretamente: execute o File Search Manager como administrador.

Se nada disso estiver disponível, o File Search Manager recorre à varredura de pastas. O aplicativo continua funcionando, mas o carregamento inicial pode ser mais lento.

O serviço é somente leitura para indexação. Ele expõe os dados da MFT ao aplicativo e se chama `WinSearchService`.

## Sintaxe de filtros

Os termos do filtro são separados por espaços. Use aspas em termos ou caminhos que contenham espaços.

Correspondência de nomes:

- `report` corresponde a nomes que contêm `report`.
- `:report` corresponde a nomes que começam com `report`.
- `report:` corresponde a nomes que terminam com `report`.
- `:report:` corresponde exatamente ao nome `report`.
- `.pdf:|.docx:` corresponde a nomes que terminam com `.pdf` ou `.docx`.
- Vários termos são combinados com E, portanto `report .pdf:` encontra nomes que contêm `report` e terminam com `.pdf`.

Correspondência de pastas:

- `src\` corresponde a itens cuja pasta pai imediata se chama `src`.
- `src\\` corresponde a itens com `src` em qualquer lugar do caminho completo.
- As âncoras de nome se aplicam a cada componente do caminho, portanto `:src:\\` encontra itens com uma pasta chamada exatamente `src` em qualquer lugar do caminho.
- `"C:\Work"` corresponde a itens diretamente dentro de `C:\Work`.
- `"C:\Work\\"` corresponde recursivamente a itens abaixo de `C:\Work`.

Histórico:

- `Ctrl+Left` e `Ctrl+Right` percorrem o histórico de filtros.
- `Down` abre as sugestões.
- `Del` remove a sugestão selecionada.
- Segure `Ctrl` ao abrir as sugestões para usar o histórico mais recente em vez do mais usado.

### Filtros fixados

- Clique em **📌 Fixar…** para adicionar uma guia editável à linha de filtros fixados. Digite o nome no local e pressione `Enter`; `Esc` cancela.
- Clique em um filtro nomeado para restaurar sua expressão salva. O filtro nomeado ativo fica destacado.
- Clique com o botão direito em um filtro nomeado para atualizá-lo a partir da expressão atual, renomeá-lo no local ou desafixá-lo.
- Os filtros fixados são restaurados quando o aplicativo inicia.

**Exportar…** salva todos os filtros fixados e as entradas da cesta de destinos em um único arquivo de configurações JSON. **Importar…** valida um arquivo de configurações e, após confirmação, substitui os filtros fixados e os destinos atuais pelo conteúdo dele.

## Pesquisa no conteúdo

Digite um termo em **Pesquisar** e pressione `Enter`.

Opções de codificação:

- `UTF-8` pesquisa texto codificado em UTF-8.
- `UTF-16` pesquisa texto codificado em UTF-16 little-endian.
- `HEX` pesquisa bytes escritos em hexadecimal separado por espaços, por exemplo `48 65 6C 6C 6F`.

A caixa **Ignorar maiúsculas e minúsculas** se aplica às pesquisas de texto.

Cores dos resultados após uma pesquisa no conteúdo:

- Verde: o conteúdo foi encontrado.
- Vermelho: o conteúdo não foi encontrado.
- Preto: o item não foi pesquisado ou a pesquisa foi limpa.
- Azul: pasta.

Digitar qualquer coisa que não seja `Enter` no campo de pesquisa limpa o estado atual dos resultados da pesquisa no conteúdo.

## Ações com o mouse

- Clique, Ctrl+clique ou Shift+clique nos resultados para selecionar itens individuais ou intervalos.
- Clique duas vezes no nome de um arquivo ou pasta para filtrar dentro desse item.
- Clique duas vezes na coluna de pasta para abrir o Explorador de Arquivos com o item selecionado.
- Ctrl + clique com o botão direito em um resultado o abre com a ação padrão do Windows.
- Clique no cabeçalho de uma coluna para classificar por ela.
- Clique com o botão direito nos itens selecionados para Abrir, Abrir com dinâmico, área de transferência, renomear, compactar e excluir.
- **Adicionar à cesta de destinos** segue a coluna em que foi feito o clique com o botão direito: Nome adiciona os itens selecionados, enquanto Pasta adiciona as pastas pai deles. Fica oculto quando os caminhos correspondentes não são destinos compatíveis ou já estão na cesta.
- Arraste da coluna Nome para enviar os arquivos ou diretórios selecionados a outro aplicativo.
- Arraste da coluna Pasta para enviar as pastas pai dos itens selecionados.
- Solte arquivos em um diretório na coluna Nome ou em um caminho pai na coluna Pasta. Escolha copiar, mover, link simbólico ou link físico.
- Solte arquivos sobre uma célula Nome executável para iniciá-la com os caminhos soltos como argumentos.
- Soltar arquivos segue os padrões do Explorador: uma pasta no mesmo volume resulta em mover; outro volume resulta em copiar. Segure `Ctrl` para copiar, `Shift` para mover ou `Alt` para um link simbólico. Vários destinos resultam em copiar.

## Exemplos práticos

- **Liberar espaço em disco** — limpe **Filtro** para mostrar todos os itens indexados. Clique no cabeçalho **Tamanho** (novamente, se necessário) até que os maiores itens fiquem no topo, examine a lista de cima para baixo e exclua os itens desnecessários com `Shift+Delete` (permanentemente, sem enviá-los para a Lixeira).
- **Observar onde um aplicativo grava** — limpe **Filtro** e clique no cabeçalho **Alterado** até que os itens modificados mais recentemente fiquem no topo. Em seguida, inicie ou use o aplicativo que deseja observar. Os arquivos nos quais ele está gravando sobem para o topo conforme mudam, e a coluna **Pasta** mostra a localização. Digite primeiro um caminho em **Filtro** se quiser observar apenas parte do sistema de arquivos.
- **Pesquisar texto em arquivos de código-fonte** — por exemplo, use `C:\Projects\\ .cs:` para limitar os resultados aos arquivos `.cs` de um projeto. Digite o texto procurado em **Pesquisar** e pressione `Enter`.

## Menu de contexto

O menu de contexto se adapta à seleção atual:

- **Abrir com** lista apenas aplicativos instalados compatíveis com a seleção. Uma ferramenta de comparação configurada aparece primeiro quando há exatamente dois arquivos ou diretórios.
- **Abrir pasta que contém** abre o Explorador de Arquivos na pasta pai do item.
- **Copiar caminho** copia os caminhos completos dos itens selecionados.
- **7-Zip** aparece quando o `7zFM.exe` está instalado.
- **Zip** cria um arquivo ZIP.
- **Criar arquivo 7z** aparece apenas quando `7z.exe` ou `7zz.exe` está instalado.
- **Descompactar** aparece para seleções de arquivos compactados compatíveis.
- **Mover para a Lixeira** faz uma exclusão recuperável.

### Exclusão e restauração

A exclusão tenta primeiro cada arquivo ou pasta selecionada como uma única operação rápida. Se um item bloqueado, inacessível ou de outra forma não removível impedir a operação sobre uma pasta inteira, o File Search Manager continua com as maiores subárvores removíveis e depois com arquivos individuais. Os itens que ainda não puderem ser removidos — e as pastas pai necessárias para contê-los — permanecem no lugar. Os erros são relatados depois que todos os itens irmãos acessíveis forem tentados.

`Delete` envia para a Lixeira cada item processado com êxito e preserva seus atributos e o local original. Uma operação parcial pode aparecer como várias entradas da Lixeira, porque as subpastas intactas são mantidas juntas sempre que possível e apenas as partes bloqueadas são divididas. Use o comando **Restaurar** da Lixeira, em vez de copiar manualmente suas entradas, para devolver cada item ao caminho original.

`Shift+Delete` usa o mesmo percurso, mas exclui permanentemente. Ele remove o atributo `Somente leitura` onde necessário; esse comportamento é geral e não se limita a repositórios Git. Arquivos bloqueados contra exclusão e caminhos negados pelas permissões do sistema de arquivos permanecem e entram no relatório final de erros.

Soltar diretórios e `Ctrl+V` mostram um seletor de ação. Conflitos de nomes existentes oferecem substituir, ignorar, renomear automaticamente e aplicar a todos. As transferências mostram o progresso por item e podem ser canceladas entre operações de nível superior.

## Cesta de destinos

A cesta de destinos na parte inferior mantém destinos reutilizáveis:

- **+ Nome** adiciona os itens selecionados da coluna Nome. Pastas, arquivos compactados compatíveis e executáveis têm comportamento específico para cada tipo de destino.
- **+ Pasta** adiciona as pastas pai dos itens selecionados.
- Durante um arrasto, a barra mostra duas grandes zonas: **＋ Adicionar como destino** e **📤 Enviar para todos os destinos (n)**. Elas aparecem assim que um arrasto começa na lista de resultados, ou quando um arrasto externo passa sobre a barra, e somem quando o arrasto termina.
- **Adicionar como destino** adiciona exatamente os caminhos produzidos pelo arrasto. Um arrasto iniciado na coluna Pasta adiciona, portanto, as pastas pai; um iniciado na coluna Nome adiciona os itens nomeados. Soltar no espaço vazio após as fichas de destino também adiciona destinos.
- **Enviar para todos os destinos** usa todos os destinos disponíveis, exibindo antes um resumo quando necessário. Soltar diretamente em uma ficha usa somente aquele destino.
- Destinos de pasta mostram o seletor de copiar, mover, link simbólico e link físico.
- Destinos de arquivo compactado adicionam as origens ao arquivo. Atualizar formatos diferentes de ZIP exige `7z.exe` ou `7zz.exe` instalado.
- Destinos executáveis são iniciados com os caminhos de origem como argumentos e exigem confirmação.
- **Enviar a área de transferência para todos (n)…** é a mesma operação com a área de transferência como origem; fica desativada enquanto não houver destinos definidos.
- Clique duas vezes em um destino para abri-lo. Clique com o botão direito em um destino para filtrar até ele, abri-lo, removê-lo ou limpar todos os destinos. O clique com o botão direito no fundo da barra também oferece **Limpar destinos**.

A cesta é salva automaticamente entre execuções do aplicativo. Destinos ausentes permanecem na configuração salva, mas são ignorados até ficarem disponíveis novamente.

Todos os comandos de destinos e filtros ficam sob `Alt` e se comportam da mesma forma em toda a janela: segure `Alt` para vê-los no painel **Dicas** e então pressione `N` (adicionar os nomes selecionados como destinos), `F` (adicionar as pastas pai), `V` (enviar a área de transferência para todos os destinos — continue segurando e acrescente `L`/`H`/`O` para link, link físico, substituição), `C` (limpar destinos), `P` (fixar o filtro), `I`/`E` (importar/exportar filtros fixados e destinos). As sequências de teclado transferem diretamente com a ação escolhida; apenas o botão da barra de ferramentas e as ações com o mouse abrem a caixa de escolha de ação.

Enquanto uma sequência de teclado está ativa, **Dicas** mostra apenas as próximas teclas válidas. `Esc` só é oferecido quando soltar as teclas pressionadas executaria uma ação e `Alt` não está pressionado. Isso também vale em uma sequência com `Alt` depois de soltá-lo mantendo outra tecla da sequência. `Backspace` sempre volta um passo. Os controles de fluxo são separados das opções de comando por uma linha.

Entradas terminadas em `›` têm outro nível de opções; mantenha uma tecla da sequência pressionada e pressione essa tecla para mostrar o submenu.

Com a lista de resultados em foco, as sequências da tecla `T` também gerenciam destinos: `T` adiciona os itens selecionados como destinos, `T` `F` adiciona as pastas pai deles, `T` `V` envia a área de transferência para todos os destinos e `T` `C` limpa os destinos. `T` `V` aceita os mesmos modificadores de `V`: `L` envia como links simbólicos, `H` como links físicos e `O` substitui arquivos existentes.

## Comandos de teclado

Coloque o foco na lista de resultados e pressione uma tecla mostrada no painel **Dicas**. Alguns comandos continuam enquanto as teclas estão pressionadas e terminam quando todas são soltas.

Comandos comuns:

- `Enter`: filtrar dentro das pastas selecionadas.
- `Delete`: mover os itens selecionados para a Lixeira após confirmação.
- `Shift+Delete`: excluir permanentemente os itens selecionados sem confirmação.
- `Ctrl+C`: cópia padrão compatível com o shell.
- `Ctrl+X`: recorte padrão compatível com o shell.
- `Ctrl+V`: colar após escolher copiar, mover, link simbólico ou link físico.
- `C`: copiar os itens selecionados para a área de transferência. Acrescente `V`, `T`, `W` ou `A` para copiar em vez disso a versão do arquivo, a data de criação, a da última gravação ou a do último acesso; um `+` inicial anexa à área de transferência em vez de substituí-la.
- `X`: recortar os itens selecionados para a área de transferência.
- `D`: comparar na ferramenta de comparação configurada. Disponível apenas com exatamente dois itens selecionados.
- `V`: colar os arquivos da área de transferência nas pastas selecionadas ou nas pastas pai.
- `O`: abrir os itens selecionados em outro aplicativo.
- `A`: abrir os itens selecionados como administrador.
- `F2`: renomear um único arquivo ou pasta física diretamente na célula Nome. Com vários itens, use um dos comandos de transformação abaixo.
- `F3`: visualizar os itens selecionados.
- `F4`: editar os itens selecionados.
- `N`: copiar os nomes selecionados.
- `P`: copiar os caminhos completos selecionados.
- `F`: copiar os caminhos de pasta selecionados.
- `M`: mostrar uma barra embutida para criar um diretório nas pastas selecionadas.
- `S`: comandos de seleção.
- `T`: comandos de destinos — adicionar a seleção como destinos; depois `F` para pastas pai, `V` para enviar a área de transferência a todos os destinos (com `L`/`H`/`O` para link, link físico, substituição), `C` para limpar os destinos.
- `U`: descompactar os arquivos selecionados. Acrescente `NumPad7` para chamar o `7z.exe` em vez do tratamento interno.
- `Z`: compactar os itens selecionados. Acrescente `NumPad7` para chamar o `7z.exe`.
- `F1`: abrir este arquivo de ajuda. Funciona em qualquer parte da janela.
- `F12`: atualizar a partir do NTFS. Funciona em qualquer parte da janela e não exige seleção; cada unidade é atualizada de forma independente, portanto uma unidade de rede lenta nunca atrasa as demais. `F1` e `F12` continuam visíveis em **Dicas** quando a grade de resultados está em foco.
- `Right Shift`: devolver o foco ao campo de filtro.

Comandos sob `Ctrl`:

- `Ctrl+A`: selecionar ou desmarcar tudo.
- `Ctrl+D`: filtrar para as pastas pai dos itens selecionados.
- `Ctrl+F`: filtrar para as pastas selecionadas.
- `Ctrl+N`: criar novas pastas dentro das pastas selecionadas.
- `Ctrl+J`: ir para o próximo item selecionado; `Ctrl+Shift+J` para o anterior.

Destinos de abertura após `O` ou `A`. Cada entrada aparece somente se o aplicativo tiver sido detectado:

- `B`: Explorador de Arquivos.
- `W`: navegador da Web padrão detectado.
- `C`: Chrome.
- `F`: Firefox.
- `E`: Edge.
- `O`: Opera.
- `I`: Internet Explorer.
- `A`: Adobe Reader.
- `T`: visualizador de texto.
- `D`: Visual Studio Code.
- `V`: Visual Studio.
- `Y`: Antigravity.
- `G`: Ghostscript.
- `P`: GhostPCL.
- `X`: GhostXPS.
- `R`: visualizador detectado para conteúdo PRN.
- `S` e depois `P`: PowerShell.
- `S` sozinho: Prompt de Comando.

`G`, `P`, `X` e `R` pedem um valor de DPI antes de executar.

Comandos de seleção após `S`:

- `A`: selecionar ou desmarcar tudo.
- `D`: selecionar diretórios.
- `F`: selecionar arquivos.
- `I`: inverter a seleção.
- `G`: selecionar linhas verdes.
- `R`: selecionar linhas vermelhas.
- `B`: selecionar linhas pretas.

Comandos de renomeação e alteração após `F2`:

- `V`: obter o novo caminho ou nome da área de transferência.
- `N`: alterar o nome.
- `E`: alterar a extensão; `E` e depois `Delete` remove a extensão.
- `.`: adicionar extensão.
- `Delete`: excluir texto do nome.
- `F`: adicionar prefixo.
- `L`: adicionar sufixo.
- `Insert`: inserir texto em uma posição.
- `R`: substituir texto.
- `C`: alterar a data de criação e depois `V` para a data da área de transferência ou `C` para a atual.
- `W`: alterar a data da última gravação, com as mesmas opções `V` e `C`.
- Acrescente `O` primeiro para substituir destinos existentes quando houver suporte.

## Dados e solução de problemas

O estado do usuário é armazenado em:

```text
%LOCALAPPDATA%\win-search
```

Se o carregamento do NTFS estiver lento, instale o serviço pelo instalador — ele vem selecionado por padrão — ou aprove a solicitação de elevação. A barra de status informa se cada unidade usou o serviço, o acesso direto, o auxiliar de administrador ou a varredura de pastas.

Se uma unidade mapeada ou externa estiver indisponível, o File Search Manager a ignora após uma breve verificação de prontidão, para que a inicialização não trave em um armazenamento inacessível.
