using System.Text.Json;
using System.Text.Json.Serialization;
using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Services;

/// <summary>
/// User settings (SDD §4.4): plain JSON under %LOCALAPPDATA%\MegaPDF, atomic writes.
/// Deliberately tiny — settings most users never need don't earn a place here.
/// </summary>
public sealed class AppSettings
{
    private readonly string _path;
    private Model _model;

    public AppSettings(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MegaPDF", "settings.json");
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

    /// <summary>Startup update check against GitHub releases — on by default.</summary>
    public bool CheckForUpdates
    {
        get => _model.CheckForUpdates;
        set { _model = _model with { CheckForUpdates = value }; Save(); }
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
        public bool CheckForUpdates { get; init; } = true;
    }
}
