using System.Windows;

namespace search
{
    /// <summary>
    /// A stable, language-independent sort key for a <see cref="System.Windows.Controls.GridViewColumn"/>.
    /// Column headers are localized, so the header-click sort must key off this instead of the
    /// visible header text (which only matched the model's English sort fields).
    /// </summary>
    public static class GridViewSort
    {
        public static readonly DependencyProperty SortKeyProperty =
            DependencyProperty.RegisterAttached(
                "SortKey", typeof(string), typeof(GridViewSort), new PropertyMetadata(null));

        public static string GetSortKey(DependencyObject element) => (string)element.GetValue(SortKeyProperty);
        public static void SetSortKey(DependencyObject element, string value) => element.SetValue(SortKeyProperty, value);
    }
}
