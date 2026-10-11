using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Services;

/// <summary>Save and open dialogs through the window's storage provider.</summary>
internal sealed class AvaloniaFileService(Func<TopLevel?> topLevel) : IFileService
{
    public async Task<SavedFile?> SaveAsync(SaveFileRequest request, Func<FileTypeChoice, Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var storage = Storage();
        if (!storage.CanSave)
        {
            throw new InvalidOperationException("Saving files is not supported on this system.");
        }

        var result = await storage.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            Title = request.Title,
            SuggestedFileName = request.SuggestedName,
            DefaultExtension = request.Types[0].Extension.TrimStart('.'),
            FileTypeChoices = request.Types.Select(t => new FilePickerFileType(t.Name) { Patterns = [$"*{t.Extension}"] }).ToList(),
            ShowOverwritePrompt = true,
        });
        if (result.File is not { } file)
        {
            return null;
        }

        using (file)
        {
            // The type picked in the dialog decides the format; failing that, the extension typed.
            var type = request.Types.FirstOrDefault(t => t.Name == result.SelectedFileType?.Name)
                ?? request.Types.FirstOrDefault(t => file.Name.EndsWith(t.Extension, StringComparison.OrdinalIgnoreCase))
                ?? request.Types[0];
            await using (var stream = await file.OpenWriteAsync())
            {
                await write(type, stream, cancellationToken);
            }

            return new SavedFile(file.Name, file.TryGetLocalPath());
        }
    }

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions)
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = extensions.Count == 0
                ? [FilePickerFileTypes.All]
                : [new FilePickerFileType(string.Join(", ", extensions)) { Patterns = extensions.Select(e => $"*{e}").ToList() }, FilePickerFileTypes.All],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private IStorageProvider Storage() =>
        topLevel()?.StorageProvider ?? throw new InvalidOperationException("The window is not open.");
}

internal sealed class AvaloniaClipboardService(Func<TopLevel?> topLevel) : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (topLevel()?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}

/// <summary>Timers on the UI thread at background priority, so input is always handled first.</summary>
internal sealed class AvaloniaUiScheduler : IUiScheduler
{
    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => tick());
        timer.Start();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
