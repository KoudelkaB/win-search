# Ayuda de File Search Manager

`F1` abre esta ayuda dentro de la aplicación; `F12` actualiza el índice NTFS. **Atajos** muestra las teclas válidas.

## Sintaxis de filtros

Los términos se separan con espacios y se combinan mediante **Y**. Use comillas para términos o rutas con espacios.

- `report` contiene el texto; `:report` empieza por él; `report:` termina por él; `:report:` es exacto.
- `pdf|docx` admite una alternativa; `report pdf:` combina ambas condiciones.
- `src\` busca el directorio padre inmediato; `src\\` busca en toda la ruta.
- `"C:\Work"` busca directamente en esa carpeta; `"C:\Work\\"` busca recursivamente debajo.

Ejemplos: `invoice pdf:`, `:IMG_ jpg|jpeg`, `"C:\Projects\\" cs:`. `Ctrl+Left` y `Ctrl+Right` recorren el historial; `Down` muestra sugerencias.

## Búsqueda y teclado

Escriba texto y pulse `Enter`. `UTF-8` y `UTF-16` buscan texto; `HEX` busca bytes separados por espacios, por ejemplo `48 65 6C 6C 6F`. Verde indica encontrado, rojo no encontrado, negro no buscado y azul carpeta.

- `Enter` filtra carpetas; `Delete` / `Shift+Delete` envía a la Papelera / elimina definitivamente.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` copian, cortan y pegan; `F2`, `F3`, `F4` renombran, muestran y editan.
- `O` / `A` abre / abre como administrador; `U` / `Z` extrae / crea un archivo.

`›` indica un submenú; `Backspace` retrocede y `Esc` cancela la secuencia.
