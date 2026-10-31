using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private async Task VerifyRulePerformanceSmokeAsync(string directory)
    {
        var measurements = new List<object>();
        RulesSearch.Clear(); ruleSearchFilter.Flush(); UpdateLayout();
        var total = filteredRules.Length;
        var realized = VisualChildren<ComboBox>(RulesGroups).Count();
        if (total < 100 || realized == 0 || realized >= total / 2)
            throw new InvalidOperationException($"Rule cards are not virtualized: {realized}/{total}");

        // Real composition events must leave the existing cards alone until commit.
        void Composition(RoutedEvent routedEvent) => RulesSearch.RaiseEvent(new TextCompositionEventArgs(
            Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, RulesSearch, "")) { RoutedEvent = routedEvent });
        var source = RulesGroups.ItemsSource;
        Composition(TextCompositionManager.PreviewTextInputStartEvent);
        RulesSearch.Text = "tian";
        await Task.Delay(160);
        Composition(TextCompositionManager.PreviewTextInputUpdateEvent);
        RulesSearch.Text = "天";
        await Task.Delay(160);
        if (!ReferenceEquals(source, RulesGroups.ItemsSource)) throw new InvalidOperationException("Rules changed during IME composition");
        Composition(TextCompositionManager.PreviewTextInputEvent);
        await Task.Delay(180); UpdateLayout();
        if (ReferenceEquals(source, RulesGroups.ItemsSource) || filteredRules.Any(r => !(r.Name + r.Definition.Key).Contains("天")))
            throw new InvalidOperationException("Committed Chinese search did not filter rules");

        foreach (var text in new[] { "", "a", "ab", "", "季", "季节", "" })
        {
            var timer = Stopwatch.StartNew(); RulesSearch.Text = text;
            var inputMs = timer.Elapsed.TotalMilliseconds;
            // Force pending work to measure card/layout cost separately from the idle delay.
            timer.Restart(); ruleSearchFilter.Flush(); UpdateLayout();
            var refreshMs = timer.Elapsed.TotalMilliseconds;
            if (inputMs > 100 || refreshMs > 500)
                throw new InvalidOperationException($"Rule search blocked the UI: input {inputMs:F1} ms, refresh {refreshMs:F1} ms");
            measurements.Add(new { Search = text, InputMs = inputMs, RefreshMs = refreshMs,
                MatchingRules = filteredRules.Length, RealizedEditors = VisualChildren<ComboBox>(RulesGroups).Count() });
        }
        source = RulesGroups.ItemsSource;
        ruleSearchFilter.Flush(); UpdateLayout();
        if (!ReferenceEquals(source, RulesGroups.ItemsSource)) throw new InvalidOperationException("Unchanged filter rebuilt the cards");

        var row = filteredRules[0]; var selected = row.Selected;
        row.Selected = row.Choices.First(c => c != selected);
        RulesScroll.ScrollToEnd(); await Task.Delay(100); UpdateLayout();
        if (RulesScroll.VerticalOffset < 100 || !VisualChildren<ComboBox>(RulesGroups).Any(c => ReferenceEquals(c.DataContext, filteredRules[^1])))
            throw new InvalidOperationException("Virtualized rules cannot scroll to the final card");
        RulesScroll.ScrollToTop(); await Task.Delay(100); UpdateLayout();
        var editor = VisualChildren<ComboBox>(RulesGroups).Single(c => ReferenceEquals(c.DataContext, row));
        if (!row.Dirty || ruleRows.Count(r => r.Dirty) != 1 || !ReferenceEquals(editor.SelectedItem, row.Selected) || !SaveRulesButton.IsEnabled)
            throw new InvalidOperationException("Rule recycling lost the selected option or pending changes");
        row.Selected = selected;
        var notifications = 0;
        void CountChanges(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => notifications++;
        foreach (var item in ruleRows) item.PropertyChanged += CountChanges;
        try { UpdateRuleEditing(); }
        finally { foreach (var item in ruleRows) item.PropertyChanged -= CountChanges; }
        if (notifications != 0) throw new InvalidOperationException("Unchanged editing state refreshed every rule card");
        RulesCategory.SelectedIndex = 1; UpdateLayout();
        if (filteredRules.Length == 0 || filteredRules.Any(r => r.Definition.Group != (string)RulesCategory.SelectedValue))
            throw new InvalidOperationException("Virtualized rule category filter failed");
        RulesCategory.SelectedIndex = 0; UpdateLayout();
        File.WriteAllText(Path.Combine(directory, "rule-performance-report.json"), JsonSerializer.Serialize(new
        {
            TotalRules = total, RealizedEditors = realized, ImeCompositionDeferred = true, CommittedChineseSearch = true,
            StableFilterPreservesContainers = true, ScrollToLastCard = true, RecycledSelectionPreserved = true,
            CategoryFilter = true, UnchangedEditingStateSkipsNotifications = true, Measurements = measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
