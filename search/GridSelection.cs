using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;

namespace search
{
    /// <summary>
    /// Bulk selection for the result DataGrid. Its public SelectedItems.Add looks every item
    /// up linearly, so selecting tens of thousands of rows one by one is quadratic (seconds
    /// to minutes on the 100k-row window). The grid's own range selection is internal; it is
    /// used through reflection - contiguous runs of rows become one call each - with the
    /// public API as the fallback should a future DataGrid version rename it.
    /// </summary>
    internal static class GridSelection
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly MethodInfo setRowsSelection = typeof(DataGrid).GetMethod("SetRowsSelection", Instance, null,
            new[] { typeof(int), typeof(int) }, null);
        static readonly MethodInfo clearRowSelection = typeof(DataGrid).GetMethod("ClearRowSelection", Instance, null,
            new[] { typeof(bool) }, null);
        static readonly MethodInfo slotFromRowIndex = typeof(DataGrid).GetMethod("SlotFromRowIndex", Instance, null,
            new[] { typeof(int) }, null);
        static readonly PropertyInfo noSelectionChangeCount = typeof(DataGrid).GetProperty("NoSelectionChangeCount", Instance);
        static readonly MethodInfo setCurrentCellCore = typeof(DataGrid).GetMethod("SetCurrentCellCore", Instance, null,
            new[] { typeof(int), typeof(int) }, null);

        static bool FastPath => setRowsSelection != null && clearRowSelection != null && slotFromRowIndex != null
            && noSelectionChangeCount != null;

        /// <summary>Replace the selection with the given items (in any order)</summary>
        public static void Select(DataGrid grid, IList items, IEnumerable<object> toSelect)
        {
            var wanted = new HashSet<object>(toSelect.Where(x => x != null), ReferenceEqualityComparer.Instance);
            if (wanted.Count == 0)
            {
                grid.SelectedItems.Clear();
                return;
            }
            if (wanted.Count == items.Count)
            {
                grid.SelectAll();
                return;
            }
            if (FastPath)
            {
                try
                {
                    SelectRuns(grid, Runs(items, wanted));
                    return;
                }
                catch (Exception e) { $"fast grid selection failed: {e.Message}".Debug(); }
            }
            grid.SelectedItems.Clear();
            foreach (var item in items) if (wanted.Contains(item)) grid.SelectedItems.Add(item);
        }

        static IEnumerable<(int Start, int End)> Runs(IList items, HashSet<object> wanted)
        {
            var start = -1;
            for (var i = 0; i < items.Count; i++)
            {
                if (wanted.Contains(items[i])) { if (start < 0) start = i; }
                else if (start >= 0) { yield return (start, i - 1); start = -1; }
            }
            if (start >= 0) yield return (start, items.Count - 1);
        }

        static void SelectRuns(DataGrid grid, IEnumerable<(int Start, int End)> runs)
        {
            var count = (int)noSelectionChangeCount.GetValue(grid);
            //One SelectionChanged for the whole replacement, raised when the count returns
            noSelectionChangeCount.SetValue(grid, count + 1);
            try
            {
                clearRowSelection.Invoke(grid, new object[] { true });
                foreach (var (start, end) in runs)
                    setRowsSelection.Invoke(grid, new object[] { Slot(grid, start), Slot(grid, end) });
            }
            finally
            {
                noSelectionChangeCount.SetValue(grid, count);
            }
        }

        static int Slot(DataGrid grid, int rowIndex) => (int)slotFromRowIndex.Invoke(grid, new object[] { rowIndex });

        /// <summary>
        /// Move the keyboard row (the anchor of Shift+arrows) without touching the selection.
        /// False when the grid does not support it - the caller then only scrolls.
        /// </summary>
        public static bool SetCurrentRow(DataGrid grid, int rowIndex)
        {
            if (setCurrentCellCore == null || slotFromRowIndex == null || rowIndex < 0) return false;
            try
            {
                var column = grid.CurrentColumn?.DisplayIndex ?? 0;
                var columnIndex = grid.CurrentColumn != null ? grid.Columns.IndexOf(grid.CurrentColumn) : 1;
                return (bool)setCurrentCellCore.Invoke(grid, new object[] { Math.Max(0, columnIndex), Slot(grid, rowIndex) });
            }
            catch { return false; }
        }

        public static void ToggleSelectAll(DataGrid grid, int itemCount)
        {
            if (grid.SelectedItems.Count == itemCount) grid.SelectedItems.Clear();
            else grid.SelectAll();
        }
    }
}
