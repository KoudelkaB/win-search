# Ayuda de File Search Manager

`F1` abre esta ayuda dentro de la aplicación; `F12` actualiza el índice NTFS. **Atajos** muestra las teclas válidas.

## Sintaxis de filtros

Los términos se separan con espacios y se combinan mediante **Y**. Use comillas para términos o rutas con espacios.

- `report` contiene el texto; `:report` empieza por él; `report:` termina por él; `:report:` es exacto.
- `.pdf:|.docx:` coincide con nombres que terminan en una de esas extensiones; `report .pdf:` combina ambas condiciones.
- `src\` busca el directorio padre inmediato; `src\\` busca en toda la ruta.
- Los anclajes de nombre se aplican a cada componente de la ruta, por lo que `:src:\\` encuentra elementos con una carpeta llamada exactamente `src` en cualquier lugar de la ruta.
- `"C:\Work"` busca directamente en esa carpeta; `"C:\Work\\"` busca recursivamente debajo.

Ejemplos: `invoice .pdf:`, `:IMG_ .jpg:|.jpeg:`, `C:\Projects\\ .cs:`. `Ctrl+Left` y `Ctrl+Right` recorren el historial; `Down` muestra sugerencias.

## Ejemplos prácticos

- **Liberar espacio en disco** — borre **Filtro** para mostrar todos los elementos indexados. Haga clic en **Tamaño** (de nuevo si es necesario) hasta que los elementos más grandes queden arriba, revise la lista desde arriba y elimine los elementos innecesarios con `Shift+Delete` (de forma permanente, sin enviarlos a la Papelera).
- **Observar dónde escribe una aplicación** — borre **Filtro** y haga clic en **Modificado** hasta que los elementos modificados más recientemente queden arriba. Después, inicie o use la aplicación que desea observar. Los archivos en los que está escribiendo suben a medida que cambian y la columna **Carpeta** muestra su ubicación. Introduzca primero una ruta en **Filtro** si solo quiere observar una parte del sistema de archivos.
- **Buscar texto en archivos de código fuente** — por ejemplo, use `C:\Projects\\ .cs:` para limitar los resultados a archivos `.cs` de un proyecto. Escriba el texto buscado en **Buscar** y pulse `Enter`.

## Búsqueda y teclado

Escriba texto y pulse `Enter`. `UTF-8` y `UTF-16` buscan texto; `HEX` busca bytes separados por espacios, por ejemplo `48 65 6C 6C 6F`. Verde indica encontrado, rojo no encontrado, negro no buscado y azul carpeta.

- `Enter` filtra carpetas; `Delete` / `Shift+Delete` envía a la Papelera / elimina definitivamente.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` copian, cortan y pegan; `F2`, `F3`, `F4` renombran, muestran y editan.
- `O` / `A` abre / abre como administrador; `U` / `Z` extrae / crea un archivo.

## Eliminación y restauración

`Delete` intenta primero mover cada carpeta seleccionada completa a la Papelera. Si un elemento bloqueado o inaccesible lo impide, la aplicación continúa con las subcarpetas más grandes posibles y finalmente con archivos individuales. Los elementos que no se puedan eliminar y las carpetas superiores necesarias permanecen en su sitio.

Los elementos de la Papelera conservan sus atributos y ubicación original. Use **Restaurar** en vez de copiarlos manualmente; después de una operación parcial, el árbol puede aparecer como varias entradas. `Shift+Delete` aplica el mismo método de forma permanente y elimina el atributo `Solo lectura` cuando sea necesario.

`›` indica un submenú; `Backspace` retrocede y `Esc` cancela la secuencia.
