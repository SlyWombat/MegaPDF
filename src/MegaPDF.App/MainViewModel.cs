using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Editing;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace MegaPDF.App;

public partial class MainViewModel(Window window) : ObservableObject
{
    private readonly UndoStack _undoStack = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(OpenDocumentName), nameof(EmptyStateVisibility), nameof(DocumentVisibility), nameof(PageIndicator))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsCommand))]
    private string? _documentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(SaveButtonLabel))]
    private bool _hasUnsavedChanges;

    public bool IsDocumentOpen => DocumentPath is not null;

    public string OpenDocumentName => DocumentPath is null ? "" : Path.GetFileName(DocumentPath);

    // Unsaved-changes dot convention (SDD §2.2).
    public string WindowTitle =>
        DocumentPath is null ? "MegaPDF"
        : $"{(HasUnsavedChanges ? "● " : "")}{OpenDocumentName} — MegaPDF";

    public string SaveButtonLabel => HasUnsavedChanges ? "Save ●" : "Save";

    public string PageIndicator => IsDocumentOpen ? "Page 1 of 1" : "";

    public Visibility EmptyStateVisibility => IsDocumentOpen ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DocumentVisibility => IsDocumentOpen ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand]
    private async Task OpenAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        // Unpackaged apps must associate pickers with their window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            OpenDocument(file.Path);
    }

    public void OpenDocument(string path)
    {
        DocumentPath = path;
        HasUnsavedChanges = false;
        _undoStack.Clear();
    }

    private bool CanSave() => IsDocumentOpen;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        // Wired to IDocumentService atomic save once the engine lands (SDD §3.4).
        HasUnsavedChanges = false;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveAs()
    {
        // Wired to the save picker + IDocumentService once the engine lands (SDD §3.4).
    }

    private bool CanUndo() => _undoStack.CanUndo;
    private bool CanRedo() => _undoStack.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        _undoStack.Undo();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        _undoStack.Redo();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }
}
