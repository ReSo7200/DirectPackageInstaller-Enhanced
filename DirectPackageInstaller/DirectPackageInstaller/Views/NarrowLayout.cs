using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DirectPackageInstaller.Views
{
    /// <summary>
    /// Phones and narrow windows. Pages get a "narrow" class for styles; values that
    /// are set locally in XAML (which beat style setters) are switched here instead,
    /// remembering the original so a desktop window can widen again.
    /// </summary>
    public static class NarrowLayout
    {
        public const double Threshold = 700;

        static readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, object?>> Saved = new();

        /// <summary>Calls <paramref name="Apply"/> whenever the page crosses the narrow threshold.</summary>
        public static void Watch(Control Page, Action<bool>? Apply = null)
        {
            bool? Last = null;
            Page.PropertyChanged += (_, e) =>
            {
                if (e.Property != Visual.BoundsProperty || Page.Bounds.Width <= 0)
                    return;
                bool Narrow = Page.Bounds.Width < Threshold;
                if (Narrow == Last)
                    return;
                Last = Narrow;
                Page.Classes.Set("narrow", Narrow);
                Apply?.Invoke(Narrow);
            };
        }

        /// <summary>Narrow: sets <paramref name="Value"/>. Wide: puts the original back.</summary>
        public static void Set(AvaloniaObject Target, AvaloniaProperty Property, object? Value, bool Narrow)
        {
            var Originals = Saved.GetOrCreateValue(Target);
            if (Narrow)
            {
                if (!Originals.ContainsKey(Property))
                    Originals[Property] = Target.GetValue(Property);
                Target.SetValue(Property, Value);
            }
            else if (Originals.Remove(Property, out var Original))
                Target.SetValue(Property, Original);
        }

        /// <summary>
        /// Settings-style rows (label in column 0, controls to the right): text boxes and
        /// combo boxes drop under the label and stretch, fixed-width columns collapse.
        /// Needs a second RowDefinition on the grid.
        /// </summary>
        public static void Reflow(Grid Row, bool Narrow)
        {
            int Columns = Math.Max(1, Row.ColumnDefinitions.Count);
            if (Row.RowDefinitions.Count < 2 || Columns < 2)
                return;

            foreach (var Column in Row.ColumnDefinitions)
                if (Column.Width.IsAbsolute)
                    Set(Column, ColumnDefinition.WidthProperty, GridLength.Auto, Narrow);

            bool LastColumnKept = false;
            foreach (var Child in Row.Children)
            {
                if (Grid.GetRow(Child) != 0 || Grid.GetColumn(Child) == 0)
                    continue;
                if (Child is TextBox || Child is ComboBox)
                {
                    Set(Child, Grid.RowProperty, 1, Narrow);
                    Set(Child, Grid.ColumnProperty, 0, Narrow);
                    Set(Child, Grid.ColumnSpanProperty, Columns > 2 ? Columns - 1 : Columns, Narrow);
                    Set(Child, Layoutable.MinWidthProperty, 0d, Narrow);
                    Set(Child, Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Stretch, Narrow);
                    Set(Child, Layoutable.MarginProperty, new Thickness(0, 10, 0, 0), Narrow);
                }
                else if (Grid.GetColumn(Child) == Columns - 1)
                    LastColumnKept = true;
            }

            foreach (var Child in Row.Children)
                if (Grid.GetRow(Child) == 0 && Grid.GetColumn(Child) == 0)
                    Set(Child, Grid.ColumnSpanProperty, LastColumnKept ? Columns - 1 : Columns, Narrow);
        }
    }
}
