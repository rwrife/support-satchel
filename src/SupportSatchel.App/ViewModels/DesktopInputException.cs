namespace SupportSatchel.App.ViewModels;

/// <summary>An operator-correctable desktop workflow input error safe to display.</summary>
public sealed class DesktopInputException : InvalidOperationException
{
    /// <summary>Creates an input error with a non-sensitive message.</summary>
    public DesktopInputException(string message)
        : base(message)
    {
    }
}
