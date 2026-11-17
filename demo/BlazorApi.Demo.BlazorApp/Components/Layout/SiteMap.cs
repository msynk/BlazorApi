namespace Petstore.BlazorApp.Components.Layout;

public sealed record NavItem(string Title, string Href, string Icon, string? Description = null);

public sealed record NavSection(string Title, IReadOnlyList<NavItem> Items);

/// <summary>The app's pages, for the sidebar and the guide's previous/next links.</summary>
public static class SiteMap
{
    public static IReadOnlyList<NavItem> Demos { get; } =
    [
        new("Pets", "pets", "grid", "Pagination, filters, create form with server validation"),
        new("Inventory", "inventory", "chart", "ApiResultView, API key authentication"),
        new("Error playground", "playground", "flask", "Every failure kind, live"),
    ];

    public static IReadOnlyList<NavItem> Guide { get; } =
    [
        new("Getting started", "docs/getting-started", "zap", "Add a contract, register the client, call it"),
        new("Calling operations", "docs/calling", "arrow-right", "Value and result methods, parameters, options"),
        new("Handling errors", "docs/errors", "alert", "ApiError, error kinds, typed error bodies, exceptions"),
        new("Blazor components", "docs/components", "layers", "ApiResultView, ApiErrorView, ApiValidationErrors"),
        new("Authentication", "docs/authentication", "shield", "Scheme-aware bearer, API key and Basic handlers"),
        new("Pagination", "docs/pagination", "list", "Cursor detection and page and item walkers"),
        new("Models", "docs/models", "box", "Polymorphism, merge patch, enums, validation attributes"),
        new("Testing", "docs/testing", "test", "Generated mocks, fixtures, MockApiHandler, contract checks"),
    ];

    public static IReadOnlyList<NavItem> Explore { get; } =
    [
        new("Contract & code", "code", "code", "The contract, its operations and the generated C#"),
        new("Source browser", "source", "folder", "Every file of the samples"),
    ];

    public static IReadOnlyList<NavSection> Sections { get; } =
    [
        new("Start", [new NavItem("Overview", "", "home")]),
        new("Live demo", Demos),
        new("Guide", Guide),
        new("Explore", Explore),
    ];
}
