# File Search Manager 帮助

`F1` 在应用内打开本帮助；`F12` 刷新 NTFS 索引。

## 筛选语法

- `report` 表示名称包含；`:report` 表示开头；`report:` 表示结尾；`:report:` 表示完全匹配。
- `pdf|docx` 表示任一项；以空格分隔的词按 **AND** 组合。
- `src\` 搜索父文件夹；`src\\` 搜索完整路径。
- `"C:\Work"` 搜索直接内容；`"C:\Work\\"` 递归搜索子项。

包含空格的路径请加引号。`Ctrl+Left`/`Ctrl+Right` 浏览历史，`Down` 显示建议。

## 搜索和快捷键

输入文本后按 `Enter`。`UTF-8` 和 `UTF-16` 搜索文本；`HEX` 搜索如 `48 65 6C 6C 6F` 的字节。`Delete` 删除，`F2` 重命名，`O` 打开，`U`/`Z` 解压/创建压缩包。`›` 打开子菜单，`Backspace` 返回，`Esc` 取消。
