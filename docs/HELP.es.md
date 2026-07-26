# Ayuda de File Search Manager

File Search Manager tiene dos campos principales:

- **Filtro** reduce la lista de archivos por nombre, carpeta, ruta o directorios seleccionados.
- **Buscar** busca en el contenido de los archivos filtrados actualmente.

La lista de resultados está pensada para el teclado. Cuando tiene el foco, el panel **Pistas** muestra los comandos disponibles para la selección.

## Indexación y elevación

Al iniciarse, File Search Manager carga las unidades listas y vigila los cambios del sistema de archivos. Cada unidad se carga de forma independiente, por lo que una unidad de red lenta nunca retrasa a las demás.

De forma predeterminada solo se indexan las unidades NTFS: otros sistemas de archivos (montajes de red, memorias FAT) no pueden usar la lectura rápida de la MFT, y recorrerlos archivo por archivo puede dominar toda la carga. Use el botón **💽 Unidades…** de la barra de estado para elegir exactamente qué unidades se indexan; las unidades sin marcar no se analizan ni se vigilan.

En las unidades NTFS, la vía más rápida es leer la Master File Table de NTFS. File Search Manager puede hacerlo de una de estas maneras:

- Servicio de Windows: sin ningún aviso. Seleccionado de forma predeterminada en el instalador.
- Asistente con privilegios: un aviso de UAC por ejecución de la aplicación. Sin el servicio se ofrece al inicio, para que la indexación pueda empezar de inmediato. Con el servicio no se ofrece hasta la primera acción que realmente necesita permisos de administrador: la tecla `A` o la lectura de archivos reservados a administradores. El botón **🛡** junto a **🌐** hace que se pida al arrancar.
- Aplicación elevada directamente: ejecute File Search Manager como administrador.

Si no hay nada de eso disponible, File Search Manager recurre a recorrer las carpetas. La aplicación sigue funcionando, pero la carga inicial puede ser más lenta.

El servicio es de solo lectura para la indexación. Expone los datos de la MFT a la aplicación de escritorio y se llama `WinSearchService`.

## Sintaxis de filtros

Los términos del filtro se separan con espacios. Use comillas para términos o rutas que contengan espacios.

Coincidencia de nombres:

- `report` coincide con nombres que contienen `report`.
- `:report` coincide con nombres que empiezan por `report`.
- `report:` coincide con nombres que terminan en `report`.
- `:report:` coincide con el nombre exacto `report`.
- `.pdf:|.docx:` coincide con nombres que terminan en `.pdf` o `.docx`.
- Varios términos se combinan con Y, así que `report .pdf:` encuentra nombres que contienen `report` y terminan en `.pdf`.

Coincidencia de carpetas:

- `src\` coincide con elementos cuya carpeta padre inmediata se llama `src`.
- `src\\` coincide con elementos que tienen `src` en cualquier lugar de la ruta completa.
- Los anclajes de nombre se aplican a cada componente de la ruta, por lo que `:src:\\` encuentra elementos con una carpeta llamada exactamente `src` en cualquier lugar de la ruta.
- `"C:\Work"` coincide con elementos directamente dentro de `C:\Work`.
- `"C:\Work\\"` coincide de forma recursiva con elementos bajo `C:\Work`.

Historial:

- `Ctrl+Left` y `Ctrl+Right` recorren el historial de filtros.
- `Down` abre las sugerencias.
- `Del` elimina la sugerencia seleccionada.
- Mantenga `Ctrl` al abrir las sugerencias para usar el historial más reciente en lugar del más usado.

### Filtros fijados

- Haga clic en **📌 Fijar…** para añadir una pestaña editable a la fila de filtros fijados. Escriba su nombre allí mismo y pulse `Enter`; `Esc` cancela.
- Haga clic en un filtro con nombre para restaurar su expresión guardada. El filtro con nombre activo aparece resaltado.
- Haga clic con el botón derecho en un filtro con nombre para actualizarlo desde la expresión actual, cambiarle el nombre allí mismo o dejar de fijarlo.
- Los filtros fijados se restauran al iniciar la aplicación.

**Exportar…** guarda todos los filtros fijados y las entradas de la cesta de destinos en un único archivo de configuración JSON. **Importar…** valida un archivo de configuración y, tras la confirmación, reemplaza los filtros fijados y los destinos actuales por su contenido.

## Búsqueda en el contenido

Escriba un término en **Buscar** y pulse `Enter`.

Opciones de codificación:

- `UTF-8` busca texto codificado en UTF-8.
- `UTF-16` busca texto codificado en UTF-16 little-endian.
- `HEX` busca bytes escritos en hexadecimal separado por espacios, por ejemplo `48 65 6C 6C 6F`.

La casilla **Ignorar mayúsculas y minúsculas** se aplica a las búsquedas de texto.

Colores de los resultados tras una búsqueda en el contenido:

- Verde: se encontró el contenido.
- Rojo: no se encontró el contenido.
- Negro: el elemento no se buscó o se borró la búsqueda.
- Azul: carpeta.

Escribir cualquier cosa distinta de `Enter` en el campo de búsqueda borra el estado actual de los resultados de la búsqueda en el contenido.

## Acciones con el ratón

- Haga clic, Ctrl+clic o Mayús+clic en los resultados para seleccionar elementos individuales o rangos.
- Haga doble clic en el nombre de un archivo o carpeta para filtrar dentro de ese elemento.
- Haga doble clic en la columna de carpeta para abrir el Explorador con el elemento seleccionado.
- Ctrl + clic derecho en un resultado lo abre con la acción predeterminada de Windows.
- Haga clic en el encabezado de una columna para ordenar por ella.
- El clic derecho sobre los elementos seleccionados ofrece Abrir, Abrir con dinámico, portapapeles, cambio de nombre, archivado y eliminación.
- **Añadir a la cesta de destinos** sigue la columna en la que se hizo clic derecho: Nombre añade los elementos seleccionados, mientras que Carpeta añade sus carpetas padre. Se oculta cuando las rutas correspondientes no son destinos admitidos o ya están en la cesta.
- Arrastre desde la columna Nombre para enviar los archivos o directorios seleccionados a otra aplicación.
- Arrastre desde la columna Carpeta para enviar las carpetas padre de los elementos seleccionados.
- Suelte archivos sobre un directorio de la columna Nombre o sobre una ruta padre de la columna Carpeta. Elija copiar, mover, vínculo simbólico o vínculo físico.
- Suelte archivos sobre una celda Nombre ejecutable para iniciarla con las rutas soltadas como argumentos.
- Soltar archivos sigue los valores predeterminados del Explorador: una carpeta en el mismo volumen implica mover; otro volumen implica copiar. Mantenga `Ctrl` para copiar, `Shift` para mover o `Alt` para un vínculo simbólico. Varios destinos implican copiar.

## Ejemplos prácticos

- **Liberar espacio en disco** — borre **Filtro** para mostrar todos los elementos indexados. Haga clic en el encabezado **Tamaño** (de nuevo si es necesario) hasta que los elementos más grandes queden arriba, revise la lista desde arriba y elimine los elementos innecesarios con `Shift+Delete` (de forma permanente, sin enviarlos a la Papelera).
- **Observar dónde escribe una aplicación** — borre **Filtro** y haga clic en el encabezado **Modificado** hasta que los elementos modificados más recientemente queden arriba. Después, inicie o use la aplicación que desea observar. Los archivos en los que está escribiendo suben a medida que cambian y la columna **Carpeta** muestra su ubicación. Introduzca primero una ruta en **Filtro** si solo quiere observar una parte del sistema de archivos.
- **Buscar texto en archivos de código fuente** — por ejemplo, use `C:\Projects\\ .cs:` para limitar los resultados a archivos `.cs` de un proyecto. Escriba el texto buscado en **Buscar** y pulse `Enter`.

## Menú contextual

El menú contextual se adapta a la selección actual:

- **Abrir con** solo lista aplicaciones instaladas compatibles con la selección. Una herramienta de comparación configurada aparece la primera cuando hay exactamente dos archivos o directorios.
- **Abrir carpeta contenedora** abre el Explorador en la carpeta padre del elemento.
- **Copiar ruta** copia las rutas completas de los elementos seleccionados.
- **7-Zip** se muestra cuando `7zFM.exe` está instalado.
- **Zip** crea un archivo ZIP.
- **Crear archivo 7z** solo se muestra cuando `7z.exe` o `7zz.exe` está instalado.
- **Descomprimir** se muestra para las selecciones de archivos comprimidos admitidos.
- **Mover a la Papelera** realiza una eliminación recuperable.

### Eliminación y restauración

La eliminación intenta primero cada archivo o carpeta seleccionada como una única operación rápida. Si un elemento bloqueado, inaccesible o de otro modo imposible de borrar impide la operación sobre una carpeta entera, File Search Manager continúa con los subárboles eliminables más grandes y después con archivos individuales. Los elementos que aun así no pueden eliminarse —y las carpetas padre necesarias para contenerlos— permanecen en su sitio. Los errores se informan después de haber intentado todos los hermanos accesibles.

`Delete` envía a la Papelera cada elemento procesado correctamente y conserva sus atributos y su ubicación original. Una operación parcial puede aparecer como varias entradas de la Papelera, porque las subcarpetas intactas se mantienen juntas siempre que es posible y solo se dividen las partes bloqueadas. Use el comando **Restaurar** de la Papelera, en lugar de copiar manualmente sus entradas, para devolver cada elemento a su ruta original.

`Shift+Delete` usa el mismo recorrido, pero elimina de forma permanente. Quita el atributo `Solo lectura` donde sea necesario; este comportamiento es general y no se limita a los repositorios Git. Los archivos bloqueados contra la eliminación y las rutas denegadas por los permisos del sistema de archivos permanecen y se incluyen en el informe final de errores.

Soltar directorios y `Ctrl+V` muestran un selector de acción. Los conflictos de nombres existentes ofrecen sobrescribir, omitir, cambio de nombre automático y aplicar a todos. Las transferencias muestran el progreso por elemento y pueden cancelarse entre operaciones de nivel superior.

## Cesta de destinos

La cesta de destinos de la parte inferior mantiene destinos de arrastre reutilizables:

- **+ Nombre** añade los elementos seleccionados de la columna Nombre. Las carpetas, los archivos comprimidos admitidos y los ejecutables tienen un comportamiento propio de cada tipo de destino.
- **+ Carpeta** añade las carpetas padre de los elementos seleccionados.
- Mientras hay un arrastre en curso, la barra muestra dos zonas grandes: **＋ Agregar como destino** y **📤 Enviar a todos los destinos (n)**. Aparecen en cuanto empieza un arrastre en la lista de resultados, o cuando un arrastre externo pasa sobre la barra, y desaparecen al terminar.
- **Agregar como destino** añade exactamente las rutas producidas por el arrastre. Por tanto, un arrastre iniciado en la columna Carpeta añade carpetas padre; uno iniciado en la columna Nombre añade los elementos nombrados. Soltar en el espacio vacío tras las fichas de destino también añade destinos.
- **Enviar a todos los destinos** usa todos los destinos disponibles tras mostrar un resumen cuando es necesario. Soltar directamente sobre una ficha usa solo ese destino.
- Los destinos de tipo carpeta muestran el selector de copiar, mover, vínculo simbólico y vínculo físico.
- Los destinos de tipo archivo comprimido añaden los orígenes al archivo. Actualizar formatos distintos de ZIP requiere tener instalado `7z.exe` o `7zz.exe`.
- Los destinos ejecutables se inician con las rutas de origen como argumentos y requieren confirmación.
- **Enviar el portapapeles a todos (n)…** es la misma operación con el portapapeles como origen; está deshabilitada mientras no haya destinos definidos.
- Haga doble clic en un destino para abrirlo. Haga clic derecho en un destino para filtrar hacia él, abrirlo, quitarlo o borrar todos los destinos. El clic derecho en el fondo de la barra también ofrece **Borrar destinos**.

La cesta se guarda automáticamente entre ejecuciones de la aplicación. Los destinos que falten permanecen en la configuración guardada, pero se omiten hasta que vuelvan a estar disponibles.

Todos los comandos de destinos y filtros están bajo `Alt` y se comportan igual en toda la ventana: mantenga `Alt` para verlos en el panel **Pistas** y después pulse `N` (añadir los nombres seleccionados como destinos), `F` (añadir carpetas padre), `V` (enviar el portapapeles a todos los destinos: siga manteniendo y añada `L`/`H`/`O` para vínculo, vínculo físico o sobrescribir), `C` (borrar destinos), `P` (fijar el filtro), `I`/`E` (importar/exportar filtros fijados y destinos). Las secuencias de teclado transfieren directamente con la acción elegida; solo el botón de la barra de herramientas y las acciones con el ratón abren el diálogo de selección de acción.

Mientras una secuencia de teclado está activa, **Pistas** muestra solo las teclas siguientes válidas. `Esc` se ofrece únicamente cuando soltar las teclas mantenidas ejecutaría una acción y `Alt` no está pulsado. Esto también funciona en una secuencia con `Alt` después de soltarlo manteniendo otra tecla de la secuencia. `Backspace` siempre retrocede un paso. Los controles de flujo se separan de las opciones de comando mediante una línea.

Las entradas que terminan en `›` tienen otro nivel de opciones; mantenga una tecla de secuencia y pulse esa tecla para mostrar el submenú.

Con la lista de resultados enfocada, las secuencias de la tecla `T` también gestionan destinos: `T` añade los elementos seleccionados como destinos, `T` `F` añade sus carpetas padre, `T` `V` envía el portapapeles a todos los destinos y `T` `C` borra los destinos. `T` `V` admite los mismos modificadores que `V`: `L` envía como vínculos simbólicos, `H` como vínculos físicos y `O` sobrescribe los archivos existentes.

## Comandos de teclado

Enfoque la lista de resultados y pulse una tecla mostrada en el panel **Pistas**. Algunos comandos continúan mientras se mantienen las teclas y terminan al soltarlas todas.

Comandos habituales:

- `Enter`: filtrar dentro de las carpetas seleccionadas.
- `Delete`: mover los elementos seleccionados a la Papelera tras confirmar.
- `Shift+Delete`: eliminar permanentemente los elementos seleccionados sin confirmación.
- `Ctrl+C`: copia estándar compatible con el shell.
- `Ctrl+X`: cortar estándar compatible con el shell.
- `Ctrl+V`: pegar tras elegir copiar, mover, vínculo simbólico o vínculo físico.
- `C`: copiar los elementos seleccionados al portapapeles. Añada `V`, `T`, `W` o `A` para copiar en su lugar la versión del archivo, la fecha de creación, la de última escritura o la de último acceso; un `+` inicial añade al portapapeles en vez de reemplazarlo.
- `X`: cortar los elementos seleccionados al portapapeles.
- `D`: comparar con la herramienta de comparación configurada. Solo disponible con exactamente dos elementos seleccionados.
- `V`: pegar los archivos del portapapeles en las carpetas seleccionadas o en sus carpetas padre.
- `O`: abrir los elementos seleccionados en otra aplicación.
- `A`: abrir los elementos seleccionados como administrador.
- `F2`: cambiar el nombre de un único archivo o carpeta física directamente en su celda Nombre. Con varios elementos, use uno de los comandos de transformación de abajo.
- `F3`: ver los elementos seleccionados.
- `F4`: editar los elementos seleccionados.
- `N`: copiar los nombres seleccionados.
- `P`: copiar las rutas completas seleccionadas.
- `F`: copiar las rutas de carpeta seleccionadas.
- `M`: mostrar una barra integrada para crear un directorio en las carpetas seleccionadas.
- `S`: comandos de selección.
- `T`: comandos de destinos — añadir la selección como destinos; después `F` para carpetas padre, `V` para enviar el portapapeles a todos los destinos (con `L`/`H`/`O` para vínculo, vínculo físico o sobrescribir), `C` para borrar los destinos.
- `U`: descomprimir los archivos seleccionados. Añada `NumPad7` para llamar a `7z.exe` en lugar del tratamiento integrado.
- `Z`: comprimir los elementos seleccionados. Añada `NumPad7` para llamar a `7z.exe`.
- `F1`: abrir este archivo de ayuda. Funciona en cualquier parte de la ventana.
- `F12`: actualizar desde NTFS. Funciona en cualquier parte de la ventana y no necesita selección; cada unidad se actualiza de forma independiente, por lo que una unidad de red lenta nunca retrasa a las demás. `F1` y `F12` siguen visibles en **Pistas** cuando la cuadrícula de resultados tiene el foco.
- `Right Shift`: devolver el foco al campo de filtro.

Comandos bajo `Ctrl`:

- `Ctrl+A`: seleccionar o deseleccionar todo.
- `Ctrl+D`: filtrar hacia las carpetas padre de los elementos seleccionados.
- `Ctrl+F`: filtrar hacia las carpetas seleccionadas.
- `Ctrl+N`: crear carpetas nuevas dentro de las carpetas seleccionadas.
- `Ctrl+J`: ir al siguiente elemento seleccionado; `Ctrl+Shift+J` al anterior.

Destinos de apertura tras `O` o `A`. Cada entrada aparece solo si se detectó la aplicación:

- `B`: Explorador de archivos.
- `W`: navegador web predeterminado detectado.
- `C`: Chrome.
- `F`: Firefox.
- `E`: Edge.
- `O`: Opera.
- `I`: Internet Explorer.
- `A`: Adobe Reader.
- `T`: visor de texto.
- `D`: Visual Studio Code.
- `V`: Visual Studio.
- `Y`: Antigravity.
- `G`: Ghostscript.
- `P`: GhostPCL.
- `X`: GhostXPS.
- `R`: visor detectado para contenido PRN.
- `S` y luego `P`: PowerShell.
- `S` solo: símbolo del sistema.

`G`, `P`, `X` y `R` piden un valor de PPP antes de ejecutarse.

Comandos de selección tras `S`:

- `A`: seleccionar o deseleccionar todo.
- `D`: seleccionar directorios.
- `F`: seleccionar archivos.
- `I`: invertir la selección.
- `G`: seleccionar filas verdes.
- `R`: seleccionar filas rojas.
- `B`: seleccionar filas negras.

Comandos de cambio de nombre y modificación tras `F2`:

- `V`: tomar la nueva ruta o el nuevo nombre del portapapeles.
- `N`: cambiar el nombre.
- `E`: cambiar la extensión; `E` y luego `Delete` la elimina.
- `.`: añadir extensión.
- `Delete`: eliminar texto del nombre.
- `F`: añadir prefijo.
- `L`: añadir sufijo.
- `Insert`: insertar texto en una posición.
- `R`: reemplazar texto.
- `C`: cambiar la fecha de creación y después `V` para la fecha del portapapeles o `C` para la actual.
- `W`: cambiar la fecha de última escritura, con las mismas opciones `V` y `C`.
- Añada `O` al principio para sobrescribir destinos existentes cuando esté admitido.

## Datos y solución de problemas

El estado del usuario se guarda en:

```text
%LOCALAPPDATA%\win-search
```

Si la carga de NTFS es lenta, instale el servicio desde el instalador —está seleccionado de forma predeterminada— o acepte el aviso de elevación. La barra de estado indica si cada unidad usó el servicio, el acceso directo, el asistente de administrador o el recorrido de carpetas.

Si una unidad asignada o externa no está disponible, File Search Manager la omite tras una breve comprobación de disponibilidad para que el inicio no se detenga en almacenamiento inaccesible.
