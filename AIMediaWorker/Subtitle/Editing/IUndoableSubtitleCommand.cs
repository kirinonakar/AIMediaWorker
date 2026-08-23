namespace AIMediaWorker.Subtitle.Editing;

public interface IUndoableSubtitleCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}

public sealed class SubtitleCommandHistory
{
    private readonly Stack<IUndoableSubtitleCommand> _undo = [];
    private readonly Stack<IUndoableSubtitleCommand> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.TryPeek(out var command) ? command.Description : null;
    public string? RedoDescription => _redo.TryPeek(out var command) ? command.Description : null;

    public event EventHandler? StateChanged;

    public void Execute(IUndoableSubtitleCommand command)
    {
        command.Execute();
        _undo.Push(command);
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var command)) return;
        command.Undo();
        _redo.Push(command);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var command)) return;
        command.Execute();
        _undo.Push(command);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class CompositeSubtitleCommand(string description, IReadOnlyList<IUndoableSubtitleCommand> commands) : IUndoableSubtitleCommand
{
    public string Description => description;
    public void Execute() { foreach (var command in commands) command.Execute(); }
    public void Undo() { for (var index = commands.Count - 1; index >= 0; index--) commands[index].Undo(); }
}
