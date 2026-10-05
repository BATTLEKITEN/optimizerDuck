using System.Reflection;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Services.Customize;
using optimizerDuck.Services.Optimization;
using optimizerDuck.UI.Pages.Customize;
using optimizerDuck.UI.Pages.Optimizations;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace optimizerDuck.UI.Windows;

/// <summary>
///     The search box above the navigation: finds any optimization or customize setting by name
///     and opens its page filtered to it.
/// </summary>
internal sealed class GlobalSearch(
    AutoSuggestBox box,
    NavigationView navigation,
    INavigationViewPageProvider pageProvider,
    OptimizationRegistry optimizationRegistry,
    CustomizeRegistry customizeRegistry
)
{
    private Dictionary<string, (string Name, Type Page)> _entries = [];
    private bool _building;

    public void Attach()
    {
        box.GotKeyboardFocus += async (_, _) => await BuildAsync();
        box.SuggestionChosen += (_, args) => Open(args.SelectedItem as string);
        box.QuerySubmitted += (_, args) =>
            Open(
                _entries.Keys.FirstOrDefault(k =>
                    k.Contains(args.QueryText, StringComparison.CurrentCultureIgnoreCase)
                )
            );
    }

    private async Task BuildAsync()
    {
        if (_building)
            return;
        _building = true;
        try
        {
            await optimizationRegistry.EnsurePreloadedAsync();
            await customizeRegistry.EnsurePreloadedAsync();

            var entries = new Dictionary<string, (string, Type)>();
            foreach (var category in optimizationRegistry.OptimizationCategories)
            {
                var page = category
                    .GetType()
                    .GetCustomAttribute<OptimizationCategoryAttribute>()
                    ?.PageType;
                if (page is null)
                    continue;
                foreach (var optimization in category.Optimizations)
                    entries.TryAdd(
                        $"{optimization.Name} · {category.Name}",
                        (optimization.Name, page)
                    );
            }

            foreach (var category in customizeRegistry.Categories)
            {
                var page = category
                    .GetType()
                    .GetCustomAttribute<CustomizeCategoryAttribute>()
                    ?.PageType;
                if (page is null)
                    continue;
                foreach (var setting in category.Features)
                    entries.TryAdd($"{setting.Name} · {category.Name}", (setting.Name, page));
            }

            _entries = entries;
            box.OriginalItemsSource = entries.Keys.OrderBy(k => k).ToList();
        }
        finally
        {
            _building = false;
        }
    }

    private void Open(string? key)
    {
        if (key is null || !_entries.TryGetValue(key, out var entry))
            return;

        navigation.Navigate(entry.Page);
        switch (pageProvider.GetPage(entry.Page))
        {
            case OptimizationPage optimizationPage:
                optimizationPage.ViewModel.SearchText = entry.Name;
                break;
            case CustomizeCategoryPage customizePage:
                customizePage.ViewModel.SearchText = entry.Name;
                break;
        }

        box.Text = string.Empty;
    }
}
