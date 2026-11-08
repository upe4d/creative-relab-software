using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PCCare
{
    public sealed class TableView
    {
        private readonly DataGrid grid;
        private readonly Predicate<object> match;
        private readonly TextBlock counter;
        private ListCollectionView view;
        public event EventHandler Changed;
        public ListCollectionView View { get { return view; } }
        public TableView(DataGrid grid, TextBox search, ComboBox choice, TextBlock counter, Predicate<object> match)
        {
            this.grid = grid; this.counter = counter; this.match = match;
            grid.CanUserSortColumns = true;
            foreach (DataGridTextColumn column in grid.Columns.OfType<DataGridTextColumn>())
                column.ElementStyle = (Style)grid.FindResource("CellText");
            grid.Sorting += delegate(object sender, DataGridSortingEventArgs e)
            {
                if (string.IsNullOrEmpty(e.Column.SortMemberPath) || view == null) { e.Handled = true; return; }
                e.Handled = true;
                ListSortDirection direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                foreach (DataGridColumn column in grid.Columns) column.SortDirection = null;
                using (view.DeferRefresh())
                {
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, direction));
                }
                e.Column.SortDirection = direction;
                Notify();
            };
            if (search != null) search.TextChanged += delegate { Refresh(); };
            if (choice != null) choice.SelectionChanged += delegate { Refresh(); };
        }
        public void SetItems(IList items)
        {
            SortDescription[] sorts = view == null ? new SortDescription[0] : view.SortDescriptions.ToArray();
            view = new ListCollectionView(items);
            view.Filter = match;
            grid.ItemsSource = view;
            // DataGrid resets sorting when its source changes, so restore it after binding.
            using (view.DeferRefresh()) foreach (SortDescription sort in sorts) view.SortDescriptions.Add(sort);
            foreach (DataGridColumn column in grid.Columns)
            {
                SortDescription[] same = sorts.Where(s => s.PropertyName == column.SortMemberPath).ToArray();
                column.SortDirection = same.Length == 0 ? (ListSortDirection?)null : same[0].Direction;
            }
            Notify();
        }
        public void Refresh()
        {
            if (view != null) view.Refresh();
            Notify();
        }
        private void Notify()
        {
            if (counter != null) counter.Text = view == null ? "Показано: 0" : "Показано: " + view.Count + " из " + view.SourceCollection.Cast<object>().Count();
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
        public static bool Search(string query, params string[] values)
        {
            string[] words = (query ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return words.All(word => values.Any(value => value != null && value.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0));
        }
        public static long Minimum(ComboBox choice)
        {
            ComboBoxItem item = choice.SelectedItem as ComboBoxItem;
            long value;
            return item != null && long.TryParse(Convert.ToString(item.Tag, System.Globalization.CultureInfo.InvariantCulture), out value) ? value : 0;
        }
    }
}
