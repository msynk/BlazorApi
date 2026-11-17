namespace BlazorApi.Demo.BlazorApp.Services;

public enum ToastKind
{
    Success,
    Info,
    Error,
}

public sealed record Toast(Guid Id, ToastKind Kind, string Message);

/// <summary>Short-lived notifications for the current user (one instance per circuit).</summary>
public sealed class ToastService
{
    private readonly List<Toast> _toasts = [];

    public event Action? Changed;

    public IReadOnlyList<Toast> Toasts => _toasts;

    public void Show(string message, ToastKind kind = ToastKind.Success)
    {
        var toast = new Toast(Guid.NewGuid(), kind, message);
        _toasts.Add(toast);
        Changed?.Invoke();
        _ = DismissLaterAsync(toast);
    }

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }

    private async Task DismissLaterAsync(Toast toast)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        Dismiss(toast.Id);
    }
}
