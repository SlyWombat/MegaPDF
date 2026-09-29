using System.Text.Json;
using System.Text.Json.Serialization;
using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Services;

/// <summary>
/// User settings (SDD §4.4): plain JSON in the user-data folder
/// (<see cref="UserDataPaths"/>), atomic writes.
/// Deliberately tiny — settings most users never need don't earn a place here.
/// </summary>
public sealed class AppSettings
{
    private readonly string _path;
    private Model _model;

    public AppSettings(string? path = null)
    {
        _path = path ?? UserDataPaths.InAppFolder("settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _model = Load();
    }

    /// <summary>Default ✗ per the 2026-07-08 stakeholder decision (SDD Appendix B #3).</summary>
    public CheckMarkStyle MarkStyle
    {
        get => _model.MarkStyle;
        set { _model = _model with { MarkStyle = value }; Save(); }
    }

    /// <summary>"" = follow the system theme; otherwise "Light" or "Dark".</summary>
    public string Theme
    {
        get => _model.Theme;
        set { _model = _model with { Theme = value }; Save(); }
    }

    public bool ReopenLastFile
    {
        get => _model.ReopenLastFile;
        set { _model = _model with { ReopenLastFile = value }; Save(); }
    }

    /// <summary>The SDD §5.4 "Make MegaPDF your PDF app?" card shows once, ever.</summary>
    public bool DefaultAppCardShown
    {
        get => _model.DefaultAppCardShown;
        set { _model = _model with { DefaultAppCardShown = value }; Save(); }
    }

    /// <summary>SDD §3.3 "flatten on save" — off by default to preserve editability.</summary>
    public bool FlattenOnSave
    {
        get => _model.FlattenOnSave;
        set { _model = _model with { FlattenOnSave = value }; Save(); }
    }

    /// <summary>
    /// UI language as a BCP-47 tag ("fr-CA"), or "" to follow the operating
    /// system (#91). Storage only: the apps apply it at startup, Core never reads it.
    /// </summary>
    public string Language
    {
        get => _model.Language;
        set { _model = _model with { Language = value }; Save(); }
    }

    /// <summary>
    /// Reading mode's page colours (#168 tier 2, #510/#511): "" for the page as the
    /// document draws it, "Sepia" or "Night". Stored as the string, not the enum, so a
    /// settings.json written by a later version that grows a fourth value still loads
    /// here — an unknown value reads back as <see cref="PageTint.Normal"/>.
    ///
    /// One field, two UIs: the WinUI ⚙ flyout and the Avalonia Options flyout both
    /// read and write this same file (SDD §4.4). Coordinate the name here, not per app.
    /// </summary>
    public string PageColours
    {
        get => _model.PageColours;
        set { _model = _model with { PageColours = value }; Save(); }
    }

    /// <summary>
    /// <see cref="PageColours"/> as the engine's tint. Unknown values are Normal rather
    /// than an exception: a settings file is user data, and a bad value must not stop
    /// a document opening.
    /// </summary>
    public PageTint PageTint
    {
        get => TintOf(PageColours);
        set => PageColours = NameOf(value);
    }

    /// <summary>The name <see cref="PageColours"/> stores for a tint, and back again.</summary>
    public static string NameOf(PageTint tint) => tint switch
    {
        PageTint.Sepia => "Sepia",
        PageTint.Night => "Night",
        _ => "",
    };

    /// <inheritdoc cref="NameOf"/>
    public static PageTint TintOf(string? name) => name switch
    {
        "Sepia" => PageTint.Sepia,
        "Night" => PageTint.Night,
        _ => PageTint.Normal,
    };

    /// <summary>
    /// Whether a document opens straight into reading mode (#168 decision 2, which
    /// dropped per-document memory in favour of this one app-level switch). Off by
    /// default: the app opens the way it always has unless someone asks otherwise.
    /// </summary>
    public bool OpenInReadingMode
    {
        get => _model.OpenInReadingMode;
        set { _model = _model with { OpenInReadingMode = value }; Save(); }
    }

    private Model Load()
    {
        if (!File.Exists(_path))
            return new Model();
        try
        {
            return JsonSerializer.Deserialize<Model>(File.ReadAllText(_path), JsonOptions) ?? new Model();
        }
        catch (JsonException)
        {
            return new Model();
        }
    }

    private void Save() =>
        AtomicFileWriter.Write(_path, s => JsonSerializer.Serialize(s, _model, JsonOptions));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Model
    {
        public CheckMarkStyle MarkStyle { get; init; } = CheckMarkStyle.Cross;
        public string Theme { get; init; } = "";
        public bool ReopenLastFile { get; init; }
        public bool DefaultAppCardShown { get; init; }
        public bool FlattenOnSave { get; init; }
        public string Language { get; init; } = "";
        public string PageColours { get; init; } = "";
        public bool OpenInReadingMode { get; init; }
    }
}
