namespace GlyphLens.App.Overlay;

/// <summary>What a global shortcut does.</summary>
public enum OverlayHotkeyAction
{
    ToggleTranslation,
    ToggleOverlay,
    TogglePause,
    ToggleInteractive,
    ClearResults,
}

/// <summary>A shortcut: modifier flags (Win32 <c>MOD_*</c>) and a virtual-key code.</summary>
public sealed record HotkeyBinding(OverlayHotkeyAction Action, uint Modifiers, uint VirtualKey, string Display);

/// <summary>The outcome of registering one shortcut.</summary>
public sealed record HotkeyRegistration(HotkeyBinding Binding, bool Registered)
{
    /// <summary>A readable explanation when the shortcut could not be registered (another program owns it).</summary>
    public string? Problem => Registered ? null : $"{Binding.Display} is already used by another program, so it is not available in GlyphLens.";
}

/// <summary>The operating-system calls behind the shortcuts (replaceable in tests).</summary>
public interface IHotkeyNative : IDisposable
{
    /// <summary>Registers a system-wide shortcut; <see langword="false"/> when it is taken or invalid.</summary>
    bool Register(int id, uint modifiers, uint virtualKey);

    void Unregister(int id);

    /// <summary>Raised on the UI thread with the id of a shortcut that was pressed.</summary>
    event EventHandler<int>? HotkeyPressed;
}

/// <summary>
/// Registers GlyphLens' global shortcuts and turns key presses into <see cref="OverlayHotkeyAction"/>s. A shortcut that another
/// program already owns is reported, never forced; the rest keep working.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;

    /// <summary>Ctrl+Alt+T, O, P, I and C.</summary>
    public static IReadOnlyList<HotkeyBinding> DefaultBindings { get; } =
    [
        new(OverlayHotkeyAction.ToggleTranslation, ModControl | ModAlt | ModNoRepeat, 'T', "Ctrl+Alt+T"),
        new(OverlayHotkeyAction.ToggleOverlay, ModControl | ModAlt | ModNoRepeat, 'O', "Ctrl+Alt+O"),
        new(OverlayHotkeyAction.TogglePause, ModControl | ModAlt | ModNoRepeat, 'P', "Ctrl+Alt+P"),
        new(OverlayHotkeyAction.ToggleInteractive, ModControl | ModAlt | ModNoRepeat, 'I', "Ctrl+Alt+I"),
        new(OverlayHotkeyAction.ClearResults, ModControl | ModAlt | ModNoRepeat, 'C', "Ctrl+Alt+C"),
    ];

    private readonly IHotkeyNative _native;
    private readonly Dictionary<int, HotkeyBinding> _registered = [];
    private bool _disposed;

    public GlobalHotkeyService(IHotkeyNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
        _native.HotkeyPressed += OnPressed;
    }

    /// <summary>Raised on the UI thread when a registered shortcut is pressed.</summary>
    public event EventHandler<OverlayHotkeyAction>? ActionRequested;

    /// <summary>Registers the bindings and reports, for each, whether it worked.</summary>
    public IReadOnlyList<HotkeyRegistration> RegisterAll(IReadOnlyList<HotkeyBinding>? bindings = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var results = new List<HotkeyRegistration>();
        int id = 0x6C00; // a private range of ids for this application
        foreach (var binding in bindings ?? DefaultBindings)
        {
            id++;
            bool ok = _native.Register(id, binding.Modifiers, binding.VirtualKey);
            if (ok)
            {
                _registered[id] = binding;
            }

            results.Add(new HotkeyRegistration(binding, ok));
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _native.HotkeyPressed -= OnPressed;
        foreach (int id in _registered.Keys)
        {
            _native.Unregister(id);
        }

        _registered.Clear();
        _native.Dispose();
    }

    private void OnPressed(object? sender, int id)
    {
        if (_registered.TryGetValue(id, out var binding))
        {
            ActionRequested?.Invoke(this, binding.Action);
        }
    }
}
